# Tempest

[![CI](https://github.com/coulibalyousmane/Tempest/actions/workflows/ci.yml/badge.svg)](https://github.com/coulibalyousmane/Tempest/actions/workflows/ci.yml)
[![Licence](https://img.shields.io/badge/licence-Apache%202.0-blue.svg)](LICENSE)

> Une tempête de trafic, à la demande.

Moteur de test de charge haute performance, asynchrone et *cloud-native*, écrit en C# / .NET 10.
Conçu pour simuler des dizaines de milliers de requêtes par seconde depuis une seule machine,
avec une empreinte mémoire minimale et une mesure de latence honnête
(**correction du *coordinated omission***).

## Démarrage rapide

```bash
git clone https://github.com/coulibalyousmane/Tempest.git
cd Tempest
dotnet run --project src/Tempest.Cli -- run --target-url https://votre-cible --rps 50 --duration 30s
```

Pas de scénario écrit, pas de configuration : ces trois commandes suffisent pour un premier tir
contre n'importe quelle URL. `tempest run --help` documente le reste (scénarios déclaratifs,
seuils, rapports HTML/JSON) — voir [Interface de ligne de
commande](#interface-de-ligne-de-commande) pour l'installation en outil global ou en binaire
autonome, sans dépendre de `git clone` ni du SDK .NET.

## Pourquoi

| Problème | Réponse de Tempest |
|---|---|
| Les outils naïfs sous-estiment la latence dès que la cible sature (*coordinated omission*) | Chaque mesure porte son instant de départ **théorique** ; les percentiles sont calculés depuis cette référence |
| Un injecteur saturé produit des résultats faux sans le dire | La dette d'ordonnancement est une métrique de premier ordre, exposée en continu |
| Les rapports arrivent après coup | Métriques temps réel via `System.Diagnostics.Metrics` → OpenTelemetry / Prometheus |
| Le GC ruine la précision sous charge | `MetricResult` est une structure *unmanaged* de 48 octets transitant par un `Channel<T>` sans verrou |

## Architecture

Clean Architecture, dépendances dirigées vers l'intérieur :

```
Tempest.Domain          ← aucune dépendance
   ↑          ↑
Tempest.Application   Tempest.Scenarios
   ↑
Tempest.Infrastructure
   ↑
Tempest.Host
```

| Projet | Rôle |
|---|---|
| `Tempest.Domain` | Contrats et objets-valeurs purs : `MetricResult`, `IWorkflow`, `LoadProfile`, `TempestClock` |
| `Tempest.Application` | Orchestration : `ILoadScheduler` / `CoordinatedRateLimiter`, `TargetRpsLoadEngine`, `VirtualUserWorker`, `VirtualUserContext` |
| `Tempest.Infrastructure` | `MetricsProcessor` (consommateur du channel), `TempestMeter`, câblage OpenTelemetry |
| `Tempest.Scenarios` | Parcours métier concrets (données Bogus, appels HTTP, assertions) |
| `Tempest.Host` | Point d'entrée ASP.NET Core, endpoints `/metrics`, `/report`, `/report/live` |
| `samples/Tempest.SampleTarget` | Cible HTTP de démonstration : latence simulée, capacité finie, jetons qui expirent |

### Flux d'exécution

1. `CoordinatedRateLimiter` déroule l'échéancier issu du `LoadProfile` et émet des jetons horodatés.
2. `TargetRpsLoadEngine` distribue ces jetons aux `VirtualUserWorker` disponibles.
3. Le `IWorkflow` exécute ses étapes via le `HttpClient` partagé et déclare leur issue.
4. Les `MetricResult` partent en non bloquant vers le `MetricsProcessor`, qui agrège et publie.

### Câblage par injection de dépendances

```csharp
services.AddHttpClient();
services.AddSingleton<IWorkflow, DynamicCheckoutWorkflow>();

services.AddTempestEngine(
    LoadProfile.Create()
        .RampTo(5_000, TimeSpan.FromSeconds(30))
        .Sustain(TimeSpan.FromMinutes(5))
        .Build(),
    new LoadTestOptions { MaxVirtualUsers = 1_024 });
```

Tous les enregistrements passent par `TryAdd` : déclarer son propre `ILoadScheduler` ou
`IMetricSink` avant l'appel suffit à le substituer, sans renoncer au reste du câblage.

## Conventions de code

Le [`.editorconfig`](.editorconfig) à la racine fait autorité et est appliqué à la compilation
(`EnforceCodeStyleInBuild` + `TreatWarningsAsErrors`) :

- constantes en `UPPER_CASE`, champs non publics en `_camelCase` — en `severity = warning`,
  donc **bloquants au build** ;
- types explicites lorsque le type est apparent, `var` seulement quand il ne l'est pas ;
- constructeurs primaires, `namespace` file-scoped, accolades Allman, UTF-8 BOM sans
  newline finale ;
- aucune valeur magique : codes de statut, seuils et capacités sont des constantes nommées.

```bash
dotnet format Tempest.sln --verify-no-changes --severity info
```

> Le fichier configure aussi des règles SonarAnalyzer (`S101`, `S3358`, `S1135`). Le paquet
> `SonarAnalyzer.CSharp` n'est pas référencé : ces lignes sont inertes tant qu'il ne l'est pas.
> Dis-le si tu veux l'ajouter — avec `TreatWarningsAsErrors`, prévoir une passe de nettoyage.

## Décisions structurantes

- **Temps en ticks `Stopwatch`, jamais en `DateTime`.** Horloge monotone, aucune conversion sur le chemin critique.
- **Échéancier par intégrale, pas par délai.** Le régulateur compare le nombre de requêtes *dues*
  (`LoadProfile.PlannedRequestsUpTo`) au nombre *émises*. La dérive ne s'accumule pas et le retard
  se mesure exactement.
- **Pas de `string` dans `MetricResult`.** Les noms d'étapes sont résolus une fois au démarrage via
  `StepRegistry` ; seul un `StepId` (int) circule. Un test vérifie que la structure reste *unmanaged*.
- **`StepScope` n'est pas `IDisposable`.** Un `using` oublié enregistrerait un faux succès :
  l'issue d'une étape doit être déclarée explicitement.

- **Un thread dédié pour l'horloge.** Le régulateur ne tourne pas sur le `ThreadPool` : sous
  charge, celui-ci peut faire attendre une tâche prête pendant des centaines de millisecondes,
  ce qu'une horloge de tir ne survit pas.
- **`ReadAsync`, jamais `WaitToReadAsync`, côté consommateurs.** Ce dernier réveille *tous* les
  lecteurs en attente à chaque jeton écrit ; les N−1 perdants se réinscrivent aussitôt. Coût
  mesuré : 12 407 o/itération avec 256 utilisateurs virtuels, contre 105 après correction.
- **Le moteur ne dépend que de `ILoadScheduler`.** Il ne sait pas d'où vient la cadence : un
  profil aujourd'hui, un maître distant ou un rejeu de trafic enregistré demain. C'est aussi ce
  qui permet de tester sa mécanique avec une cadence déterministe, sans horloge ni test instable.
- **`LoadTestOptions` ne contient pas le profil de charge.** *Comment* l'injecteur se comporte et
  *quoi* tirer changent pour des raisons différentes : un même réglage d'injecteur sert tous les
  profils, et inversement.
- **Deux périmètres statistiques, une seule structure.** Cumulé pour le verdict CI, glissant pour
  le temps réel. L'histogramme sait fusionner, donc une fenêtre n'est qu'une somme de paniers
  temporels : le surcoût se limite à un second incrément de tableau par mesure.
- **Un centile ne sous-estime jamais.** Les valeurs rapportées sont les bornes hautes des paniers,
  plafonnées au maximum réellement observé. Pour une vérification de SLO, se tromper par excès est
  la seule erreur acceptable.
- **Tempest n'a aucune dépendance à un exportateur.** Il alimente les instruments
  `System.Diagnostics.Metrics` de la BCL ; OpenTelemetry, Prometheus ou un simple `MeterListener`
  viennent les écouter.
- **Le panier de `DynamicCheckoutWorkflow` ne coordonne rien entre deux processus.** Il se
  construit à partir de la réponse *réelle* de l'étape `browse`, jamais d'un pool de produits
  pré-généré côté client. Deux projets indépendants (scénario, cible) qui s'accorderaient sur
  des identifiants à l'avance seraient fragiles au moindre changement de l'un des deux.
- **Un singleton enregistré mais jamais résolu ne se construit jamais.** `MetricsAggregator` et
  `TempestMeter` en ont chacun fait les frais lors du premier tir réel (détails ci-dessous) :
  un conteneur d'injection de dépendances ne garantit ni un ordre de construction entre
  singletons indépendants, ni qu'un service sans consommateur direct soit jamais instancié.

## Métriques exposées

Meter `Tempest` — les jauges lisent la fenêtre glissante, les compteurs le cumul :

| Instrument | Type | Étiquettes |
|---|---|---|
| `tempest.latency` | jauge (ms) | `step`, `kind` (`response` \| `service`), `quantile` |
| `tempest.requests` | compteur | `step`, `outcome` |
| `tempest.bytes.received` | compteur | `step` |
| `tempest.scheduling.delay.max` | jauge (ms) | `step` |
| `tempest.metrics.dropped` | compteur | — |

`kind=response` est la latence corrigée du *coordinated omission*, `kind=service` la mesure brute.
Les superposer sur un même graphe montre le moment exact où l'injecteur ou la cible décroche.

```csharp
services.AddTempestMetrics();                       // agrégation + consommateur + instruments
services.AddTempestOpenTelemetry(builder => builder.AddOtlpExporter());
```

## Démarrage

```bash
dotnet test Tempest.sln -c Release
```

Étalonnage de l'injecteur (scénario à vide, ni réseau ni agrégation) :

```bash
dotnet run --project tools/Tempest.Probe -c Release
```

Premier tir réel, contre la cible de démonstration — deux processus, deux terminaux :

```bash
dotnet run --project samples/Tempest.SampleTarget -c Release   # écoute sur :5299
dotnet run --project src/Tempest.Host -c Release                # tire, écoute sur :5280
```

Pendant et après le tir : `http://localhost:5280/report/live` (fenêtre glissante),
`.../report` (cumulé), `.../report.html` (le même rapport cumulé, en page HTML autonome —
lisible directement dans un navigateur, sans serveur ni JSON à interpréter), `.../metrics`
(Prometheus).

`/report.html` (`LoadTestReport.ToHtml`) reprend les mêmes chiffres que `/report`, plus le
verdict des seuils configurés s'il y en a (même contenu que `/thresholds`, en couleur) — CSS et
contenu entièrement inline, aucune ressource externe. Les noms d'étape, qui viennent en
définitive d'un fichier de scénario potentiellement écrit par quelqu'un d'autre que l'opérateur
qui ouvre ce rapport, sont échappés avant insertion (`WebUtility.HtmlEncode`) : une étape
nommée `<script>...</script>` s'affiche comme texte, jamais comme code. Même endpoint côté
maître en mode distribué, construit à partir du rapport final fusionné.

Vérifié par un vrai tir : le tableau du rapport HTML reflète exactement les chiffres du rapport
JSON (245 itérations, p95 89,60 ms) et affiche correctement le verdict d'un seuil respecté
(`[OK] __iteration: ResponseP95Milliseconds < 500`).

## Interface de ligne de commande

`Tempest.Host` reste piloté par `appsettings.json`/variables d'environnement — adapté à un hôte
qui reste actif. `Tempest.Cli` (`tempest`) répond à un besoin différent : lancer un tir depuis un
terminal, avec des options qui priment sur la configuration, et se terminer à la fin.

```bash
dotnet run --project samples/Tempest.SampleTarget -c Release   # écoute sur :5281

dotnet run --project src/Tempest.Cli -c Release -- run scenarios/smoke-test.yaml \
  --target-url http://localhost:5281 \
  --rps 20 --duration 30s \
  --threshold "__iteration:ResponseP95Milliseconds:LessThan:500" \
  --report-html rapport.html --report-json rapport.json
```

Sans fichier de scénario, `--workflow <nom>` sélectionne un scénario intégré
(`dynamic-checkout` par défaut, `websocket-echo`, `grpc-echo`, `grpc-stream-echo`,
`grpc-client-stream-echo`, `grpc-bidi-stream-echo`) — les mêmes que `Tempest.Host`. Le profil de
charge est soit constant (`--rps <n> --duration <d>`), soit une rampe
(`--from-rps <n> --to-rps <n> --duration <d>`) ; `--duration` accepte `30s`, `5m`, `1h`, `500ms`,
ou un nombre de secondes. `--threshold` (répétable) prend le format
`etape:grandeur:comparaison:limite[:nom]`, les mêmes valeurs que `ThresholdRule`.

`--rps`/`--from-rps`/`--to-rps` et `--threshold` restent optionnels si un `appsettings.json` du
répertoire courant fournit déjà `Tempest:Profile` / `Tempest:Thresholds` (même format que celui de
`Tempest.Host`) : la CLI complète la configuration plutôt que de l'exiger en double. `--target-url`
suit la même règle avec `Tempest:TargetBaseUrl`.

Le processus se termine toujours à la fin du tir (`ExitAfterRun` implicite), avec le même code de
sortie que `Tempest.Host` — c'est la seule différence de comportement documentée entre les deux :
un hôte reste actif pour continuer à servir `/metrics`, une CLI non. `--report-html`/`--report-json`
compensent cette différence en écrivant le rapport final sur disque avant que le processus ne
disparaisse (`--report-json` produit le même format que `/report`, directement réutilisable par
`Tempest.Compare`).

Vérifié par de vrais tirs : profil constant et rampe contre `Tempest.SampleTarget`, scénario
déclaratif (`scenarios/smoke-test.yaml`), workflow intégré, seuil respecté (code de sortie 0) et
seuil délibérément trop strict (code de sortie 1, `[ECHEC] trop strict (observe : 121,34)`),
rapports HTML et JSON écrits sur disque, et repli sur un `appsettings.json` du répertoire courant
en l'absence de `--rps`/`--target-url`.

**Limites de cette première version**, documentées dans `tempest run --help` : un seul processus
autonome — pas de mode distribué (Master/Workers) depuis la CLI, qui reste l'affaire de
`Tempest.Host`.

### Installation

`Tempest.Cli` s'empaquette comme un [outil global
dotnet](https://learn.microsoft.com/dotnet/core/tools/global-tools) (`PackAsTool`, commande
`tempest`, distincte du nom de paquet `Tempest.Cli`) :

```bash
dotnet pack src/Tempest.Cli -c Release
dotnet tool install --global --add-source src/Tempest.Cli/bin/Release Tempest.Cli
tempest run --target-url http://localhost:5299 --rps 50 --duration 30s
```

Pas encore publié sur nuget.org — le dépôt reste privé (bullet suivant de la phase 1) — donc
l'installation se fait aujourd'hui depuis une source locale (`--add-source`) ou un flux privé.
Sans empaquetage, `tempest` reste utilisable via `dotnet run --project src/Tempest.Cli --`.

Vérifié par un vrai `dotnet tool install --global`, `tempest` réellement sur le `PATH`, puis un
tir depuis un répertoire sans rapport avec le dépôt (`/tmp`), contre `Tempest.SampleTarget`, avec
`--report-json` écrit sur disque.

### Binaires autonomes

`Tempest.Cli` se publie aussi en binaire autonome (*self-contained*, fichier unique) pour Windows,
Linux et macOS (x64 et arm64) — aucun SDK ni runtime .NET requis sur la machine qui l'exécute :

```bash
dotnet publish src/Tempest.Cli -c Release -r win-x64 \
  -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# -> src/Tempest.Cli/bin/Release/net10.0/win-x64/publish/tempest.exe (linux-x64/osx-x64/osx-arm64 : "tempest")
```

**Pas de compilation Native AOT.** Essayé réellement (`-p:PublishAot=true`) : la publication
échoue avec `IL3050`/`IL2026` sur `ScenarioDefinitionLoader` — `YamlDotNet.DeserializerBuilder`
(constructeur par défaut) et une désérialisation JSON par réflexion utilisent toutes les deux du
code dynamique incompatible avec l'AOT. Les corriger demanderait de réécrire ce pipeline avec le
générateur statique de YamlDotNet et un `JsonSerializerContext` — un chantier séparé, plus risqué,
non traité ici. Le binaire reste *self-contained* (runtime .NET embarqué) mais pas nativement
compilé : plus volumineux (~105 Mo par plateforme) qu'un binaire AOT ne l'aurait été.

Un job GitHub Actions dédié (`.github/workflows/release.yml`) publie les quatre archives en
release GitHub à chaque tag `vX.Y.Z` poussé — pas encore déclenché, aucune release n'a encore été
coupée.

Vérifié par de vrais tirs : le `tempest.exe` win-x64 publié, exécuté directement (sans `dotnet
run`), contre `Tempest.SampleTarget`, rapport JSON écrit sur disque, code de sortie 0 — et les
trois autres RID compilés sans erreur (non exécutables depuis cette machine Windows). Le nettoyage
des fichiers hérités de `Tempest.Host` (`appsettings*.json`, `web.config`) a été vérifié : sans
lui, un `tempest run` sans `--target-url` aurait silencieusement trouvé la cible de démonstration
de l'hôte au lieu d'échouer avec le message d'erreur attendu.

**Limite restante** : dépôt encore privé — dernier bullet de la
[phase 1 de ROADMAP.md](ROADMAP.md#phase-1--rendre-tempest-installable).

## Paquets NuGet

Le moteur se consomme aussi comme une bibliothèque C#, à la NBomber : `Tempest.Domain`,
`Tempest.Application`, `Tempest.Infrastructure` et `Tempest.Scenarios` s'installent séparément,
chacun avec son propre rôle.

| Paquet | Contenu | Dépend de |
|---|---|---|
| `Tempest.Domain` | Contrats (`IWorkflow`, `IVirtualUserContext`), profils de charge, rapports. Zéro dépendance NuGet. | — |
| `Tempest.Application` | Le moteur (`TargetRpsLoadEngine`, `AddTempestEngine`) — déroule un `IWorkflow` à un débit cible. | `Tempest.Domain` |
| `Tempest.Infrastructure` | La chaîne de mesure (`AddTempestMetrics`, export OpenTelemetry) — transforme les mesures en `LoadTestReport`. | `Tempest.Domain`, `Tempest.Application` |
| `Tempest.Scenarios` | Scénarios de référence (`DynamicCheckoutWorkflow`, gRPC, WebSocket) et le format déclaratif. | `Tempest.Domain` |

```csharp
// Ecrire et lancer un scenario depuis un projet xUnit, sans cloner ce depot :
HostApplicationBuilder builder = Host.CreateApplicationBuilder();
builder.Services.AddSingleton(new HttpClient { BaseAddress = new Uri("https://votre-cible") });
builder.Services.AddSingleton<IWorkflow>(new DynamicCheckoutWorkflow());

LoadProfile profile = new([LoadStage.Ramp(fromRps: 0, toRps: 50, TimeSpan.FromSeconds(30))]);
builder.Services.AddTempestEngine(profile, new LoadTestOptions { MaxVirtualUsers = 100 });
builder.Services.AddTempestMetrics();

using IHost host = builder.Build();
await host.StartAsync();

TargetRpsLoadEngine engine = host.Services.GetRequiredService<TargetRpsLoadEngine>();
MetricsProcessor metricsProcessor = host.Services.GetRequiredService<MetricsProcessor>();
metricsProcessor.Start();
LoadTestSummary summary = await engine.RunAsync(CancellationToken.None);
await metricsProcessor.StopAsync();

LoadTestReport report = metricsProcessor.Aggregator.Snapshot(StatisticsScope.Cumulative);
```

Le bullet initial de ROADMAP.md ne citait que `Tempest.Domain` et `Tempest.Scenarios` — élargi à
`Tempest.Application` et `Tempest.Infrastructure` : sans eux, un projet externe pouvait écrire un
scénario mais pas le lancer, ce qui aurait manqué la parité avec NBomber explicitement visée.

Vérifié par un vrai tir depuis un projet xUnit **entièrement externe** (aucun `ProjectReference`
vers ce dépôt, uniquement les quatre `.nupkg` via une source NuGet locale) : `DynamicCheckoutWorkflow`
(`Tempest.Scenarios`) exécuté à travers `AddTempestEngine`/`AddTempestMetrics`, contre
`Tempest.SampleTarget`, avec un rapport contenant bien l'étape `checkout`.

**Limite restante** : pas encore publiés sur nuget.org (le dépôt reste privé). Les quatre paquets
s'installent aujourd'hui depuis une source locale (`dotnet pack` puis `--add-source`) ou un flux
privé, comme `Tempest.Cli`.

## Scénario de référence

`DynamicCheckoutWorkflow` (login → browse → checkout) illustre trois capacités du moteur en
même temps :

- **jeton mis en cache par utilisateur virtuel** — `login` n'est rejoué qu'à la première
  itération, ou après un 401 ;
- **corrélation minimale** — le panier de `checkout` référence les identifiants réellement
  renvoyés par `browse`, jamais un pool pré-généré côté client ;
- **JSON sans réflexion** — sérialisation générée à la compilation
  (`System.Text.Json.Serialization.JsonSerializerContext`) des deux côtés du contrat HTTP,
  politique `camelCase` déclarée explicitement (voir plus bas pourquoi).

`Tempest.SampleTarget` simule une vraie capacité finie (`SemaphoreSlim` bornée, 503 au-delà)
et des jetons qui expirent, pour que le scénario ait vraiment quelque chose à saturer et à
rafraîchir plutôt qu'un simple écho instantané.

## Ce que le premier tir réel a révélé

Trois défauts qu'aucun test unitaire — y compris les 128 déjà écrits à l'étape 3 — n'avait
pu voir, parce qu'aucun n'exerçait à la fois un vrai réseau et le câblage DI complet :

1. **JSON camelCase vs PascalCase.** ASP.NET Core sérialise en camelCase par défaut ; un
   `JsonSerializerContext` sans `[JsonSourceGenerationOptions]` explicite attend les noms de
   propriété exactement tels que déclarés (PascalCase). Sans accord explicite entre les deux
   contextes (client et serveur), un jeton de connexion serait arrivé au client sous un nom qui
   ne correspond à rien — `Token` resterait `null`, échec silencieux, sans exception. Corrigé en
   déclarant `PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase` explicitement des deux côtés.
2. **`MetricsAggregator` exigeait un `StepRegistry` déjà rempli à la construction.** Or le
   registre n'est peuplé qu'au démarrage du tir (`TargetRpsLoadEngine.RunAsync`), pas à la
   construction du moteur — et un conteneur DI ne garantit aucun ordre entre deux singletons
   indépendants. Corrigé en construisant les accumulateurs **paresseusement**, au premier
   enregistrement ou à la première lecture, plutôt qu'au constructeur.
3. **`TempestMeter` n'était résolu par personne.** Le `Meter` et ses instruments ne se créent
   qu'à la construction de `TempestMeter` — mais rien d'autre n'en dépend directement (il existe
   pour être observé de l'extérieur, pas pour être appelé). Résultat : Prometheus n'exposait que
   `target_info`, aucune métrique Tempest. Corrigé par un `IHostedService` dédié
   (`MeterActivationHostedService`) dont l'unique rôle est de forcer la résolution.

Les trois sont désormais couverts par des tests de régression qui reproduisent l'ordre exact
qui a échoué, sans dépendre de réseau.

Résultat du premier tir (100 utilisateurs virtuels, rampe 0→100→0 RPS sur 35 s, contre la
cible de démonstration) : 2 750 itérations, **0 échec, 0 abandon**, 100 connexions pour 100
utilisateurs virtuels (le jeton mis en cache fonctionne), dette d'ordonnancement maximale
14 ms. La queue de latence (P99 à 811 ms sur `__iteration`, contre P50 à 55 ms) vient du
démarrage à froid — JIT, première connexion TCP par utilisateur virtuel — pas d'une
saturation réelle ; c'est exactement le genre de détail qu'une moyenne aurait masqué.

## Seuils CI/CD

Un `ThresholdRule` transforme un rapport en verdict binaire : grandeur, comparaison, limite.
Volontairement absent de la liste des grandeurs disponibles : toute variante `Service*` (le
temps brut, non corrigé). Gater un pipeline sur le temps de service reviendrait à faire
confiance à la mesure même que Tempest existe pour corriger — un seuil ne peut porter que sur
la latence de réponse déjà corrigée, le taux d'erreur, la dette d'ordonnancement ou le nombre
de mesures.

```json
"Tempest": {
  "Thresholds": [
    { "StepName": "__iteration", "Metric": "ResponseP95Milliseconds", "Comparison": "LessThan", "Limit": 200 },
    { "StepName": "__iteration", "Metric": "ErrorRate", "Comparison": "LessThanOrEqual", "Limit": 0.01 }
  ],
  "ExitAfterRun": true
}
```

`ExitAfterRun` est **faux par défaut** : sans lui, l'hôte reste actif pour continuer à servir
`/metrics` comme avant, même si des seuils sont configurés. C'est le scénario CI (un script qui
attend un code de sortie) qui doit l'activer explicitement — jamais un changement de
comportement silencieux pour du code déjà en production. Une étape introuvable (nom mal
orthographié) est un **échec**, pas un succès par défaut : une règle mal configurée doit se voir
dans le verdict, pas disparaître dans un pipeline qui continue de passer au vert.

Vérifié par un vrai tir : seuil trop strict → code de sortie 1, seuil desserré → code de sortie
0, `ExitAfterRun` absent → l'hôte reste actif comme à l'étape précédente. `/thresholds` expose
le même verdict en JSON, à tout moment du tir.

## Comparaison entre tirs

Un `ThresholdRule` gate sur une limite **absolue**, à redéfinir manuellement à chaque évolution
légitime de la cible. `tools/Tempest.Compare` répond à une question différente — "a-t-on
régressé *depuis le dernier tir de référence*, indépendamment de la limite absolue ?" — à
partir de deux rapports `/report` exportés en JSON, sans qu'aucun autre seuil n'ait besoin
d'être redéfini :

```bash
dotnet run --project tools/Tempest.Compare -- reference.json actuel.json \
  --html comparaison.html --max-regression-percent 20
```

Trois usages du même calcul (`LoadTestReportComparison.Compare`), pas trois outils : une table
console (usage manuel ou log CI), `--html` pour un rapport comparatif ouvrable dans un
navigateur (régressions en rouge, améliorations en vert), `--max-regression-percent` pour un
code de sortie 1 si une étape régresse au-delà de ce pourcentage de p95 par rapport à la
référence. Les étapes sont appariées par nom : une étape apparue ou disparue entre les deux
tirs est signalée comme telle, jamais ignorée en silence.

`Tempest.Compare` ne déserialise pas directement vers `LoadTestReport` : comme
`ScenarioDefinitionDto` côté scénarios déclaratifs, `System.Text.Json` ne sait pas construire un
`IReadOnlyList<T>` par réflexion — un DTO à types concrets fait la frontière avant de mapper
vers le type Domain réel.

Vérifié par deux vrais tirs contre des cibles de latence différente (5–15 ms puis 40–80 ms) :
la comparaison détecte correctement une régression de p95 de +205,6 % sur `__iteration`,
`--max-regression-percent 10` échoue (code de sortie 1), `--max-regression-percent 1000`
passe (code de sortie 0) — et le rapport HTML colore chaque étape régressée en rouge.

## Pipeline CI

Tout l'outillage orienté CI (seuils, `ExitAfterRun`, `Tempest.Compare`) restait, jusqu'ici,
jamais exercé automatiquement. `.github/workflows/ci.yml` ferme cette boucle sur chaque push et
pull request vers `main`, en deux temps :

- **`build-and-test`** — restauration, compilation en `Release`, suite de tests complète,
  `dotnet format --verify-no-changes`.
- **`smoke-e2e`** — un **vrai tir**, pas seulement des tests unitaires : démarre
  `Tempest.SampleTarget`, attend qu'il réponde, puis lance `Tempest.Host` en mode autonome
  avec des seuils configurés et `ExitAfterRun=true` contre lui. C'est ce genre de vérification
  qui a déjà révélé des bugs réels dans ce projet (limite Kestrel HTTP/1.1+2, `UriFormatException`
  sur l'hôte `+`, `TargetUri` jamais propagé aux workers) — aucun test isolé ne les aurait
  trouvés. Seuils volontairement larges (P95 < 1 000 ms) : ce job vérifie que la chaîne
  seuils → code de sortie fonctionne en CI, pas la performance elle-même, qui varie trop d'un
  runner partagé à l'autre pour un seuil serré.

Vérifié en local, commande par commande, avant de pousser le workflow (pas d'accès direct aux
runs GitHub Actions depuis cet environnement — `gh` n'est pas installé, cf. plus haut) : restore,
build, 299 tests, format, puis un tir de fumée réel avec les mêmes seuils — code de sortie 0.

## Configuration déclarative

Un scénario HTTP peut se décrire en YAML ou JSON plutôt qu'en C#, sans recompiler :

```yaml
name: smoke-test
steps:
  - name: login
    method: POST
    path: /api/auth/login
    body: '{"username":"demo","password":"demo"}'
  - name: browse
    method: GET
    path: /api/catalog/products
    expectedStatusCodes: [200]
```

```json
"Tempest": { "ScenarioFile": "scenarios/smoke-test.yaml" }
```

`ScenarioFile` absent (par défaut) : l'hôte utilise `DynamicCheckoutWorkflow`, comme avant —
même logique de non-régression que pour `ExitAfterRun`. Renseigné : il construit un
`DeclarativeWorkflow` à partir du fichier, qui reçoit le **même** traitement que n'importe
quel scénario codé en dur — métriques, seuils, `/metrics`, sans aucun cas particulier.

Sans `expectedStatusCodes`, l'heuristique usuelle s'applique (2xx = succès). Avec, la
correspondance est exacte : un 2xx absent de la liste devient un `AssertionFailed`, pas un
succès — le scénario a dit ce qu'il attendait, et un 200 inattendu ne le satisfait pas
silencieusement.

> YamlDotNet et `System.Text.Json` ne savent construire, par réflexion, ni
> `IReadOnlyList<T>` ni `IReadOnlyDictionary<K,V>` — découvert en écrivant les tests, pas en
> le supposant. `ScenarioDefinitionDto` (types concrets, mutables) isole ce compromis à la
> frontière de désérialisation ; `ScenarioDefinition` (Domain) reste un objet-valeur immuable.

### Corrélation dynamique (Regex/XPath/JsonPath)

Une étape peut extraire une valeur de sa réponse et la rendre disponible aux étapes
suivantes via `{{nom}}` — c'est ce qui comblait la limite décrite plus haut dans les
versions précédentes de ce document (un jeton d'authentification ne pouvait pas se propager
d'une étape à l'autre) :

```yaml
steps:
  - name: login
    method: POST
    path: /api/auth/login
    body: '{"username":"demo","password":"demo"}'
    extract:
      - variable: token
        regex: '"token":"([^"]+)"'
  - name: checkout
    method: POST
    path: /api/checkout
    headers:
      Authorization: "Bearer {{token}}"
    expectedStatusCodes: [200]
```

Exactement une expression par règle : `regex` (universelle, sur texte brut), `xpath` (pour un
corps XML) ou `jsonPath` (pour un corps JSON) :

```yaml
extract:
  - variable: token
    jsonPath: $.token
```

`jsonPath` ne couvre volontairement qu'un sous-ensemble pratique — accès par propriété
(`.nom`) et par index (`[n]`), par ex. `$.data.items[0].id` — sans caractères génériques,
filtres, descente récursive (`..`) ni tranches : une extraction Regex suffisait jusqu'ici sur
un corps JSON, ce sous-ensemble couvre le reste des cas usuels sans réimplémenter la
spécification JSONPath entière. Implémenté avec `System.Text.Json.Nodes` (BCL) uniquement —
`Tempest.Domain` n'a aucune dépendance NuGet externe, pas de bibliothèque JSONPath dédiée.

Les trois syntaxes sont validées au chargement du scénario, pas au premier appel : une
expression mal formée échoue immédiatement, avant le premier tir.

Portée volontairement limitée à ce seul vocabulaire — pas de branchement, pas de boucle. Les
variables extraites sont **locales à une itération** : une étape qui référence `{{nom}}` sans
qu'aucune extraction précédente ne l'ait renseigné échoue en `AssertionFailed` *avant même
l'envoi de la requête* — une erreur de configuration du scénario, pas un échec de transport à
mesurer comme tel. De même, une extraction configurée mais manquée transforme un 2xx en
`AssertionFailed` : le scénario attendait une valeur que la réponse n'a pas fournie, ce n'est
pas un succès silencieux.

[`scenarios/smoke-test.yaml`](scenarios/smoke-test.yaml) démontre le cas réel : `login`
extrait le jeton effectivement émis par `Tempest.SampleTarget`, `checkout` le réutilise dans
son en-tête `Authorization` — vérifié par un vrai tir, `checkout` passe désormais à 0 %
d'échec (il retournait systématiquement 401 dans les versions précédentes de ce fichier,
faute de pouvoir propager un jeton réel).

Vérifié à nouveau après l'ajout de JsonPath, avec ce même scénario ré-écrit pour extraire le
jeton via `$.token` plutôt qu'un motif Regex : 125 itérations, **0 échec** sur
`login`/`browse`/`checkout` — `checkout` reste à 0 % d'échec, confirmant que le jeton extrait
par JsonPath se propage correctement à l'en-tête `Authorization`, exactement comme avec Regex.

### Jeux de données

Premier bullet de la [roadmap phase 2](ROADMAP.md#phase-2--des-scénarios-quon-peut-réellement-écrire) :
sans jeu de données, tous les utilisateurs virtuels envoient les mêmes identifiants — le pool
généré par Bogus dans `DynamicCheckoutWorkflow` ne couvrait ce besoin que pour ce seul scénario,
codé en dur. Un scénario déclaratif peut désormais charger un fichier CSV ou JSON et piocher une
ligne à chaque itération, exposée à n'importe quelle étape via `{{jeu.colonne}}` — même mécanisme
de substitution que les variables extraites, juste préfixé par le nom du jeu :

```yaml
name: dataset-example
datasets:
  - name: user
    path: scenarios/users.csv
    strategy: uniquePerVirtualUser   # circular (défaut) | random | uniquePerVirtualUser
steps:
  - name: login
    method: POST
    path: /api/auth/login
    body: '{"username":"{{user.username}}","password":"{{user.password}}"}'
```

```csv
username,password
alice,alice-pw
bob,bob-pw
```

Trois stratégies de choix d'une ligne, portées par `DataSet` (`Tempest.Domain.Data`) :

- **`circular`** (défaut) — parcourt les lignes dans l'ordre, en boucle, un curseur **partagé**
  par tous les utilisateurs virtuels (`Interlocked.Increment`, sans verrou).
- **`random`** — une ligne tirée uniformément au hasard à chaque lecture.
- **`uniquePerVirtualUser`** — une ligne fixe par utilisateur virtuel
  (`VirtualUserId % nombre de lignes`), la même à chaque itération de cet utilisateur —
  exactement le principe déjà à l'œuvre dans `DynamicCheckoutWorkflow.ExecuteAsync`, généralisé
  à n'importe quelle source de données.

Une ligne est choisie **une fois par itération**, pas une fois par étape : toutes les étapes
d'une même itération voient la même ligne, comme les variables extraites. Le fichier est chargé
une seule fois dans `IWorkflow.SetUpAsync`, jamais sur le chemin critique — un jeu de données
volumineux ne coûte rien pendant le tir.

Vérifié par de vrais tirs contre `Tempest.SampleTarget` : un scénario déclaratif dont `checkout`
substitue `productId`/`quantity` depuis un CSV avec `uniquePerVirtualUser` passe à 0 % d'échec
sur 3 utilisateurs virtuels, chacun recevant sa propre ligne à chaque itération (confirmé par
instrumentation temporaire) ; `scenarios/scripted-checkout.csx` (voir plus bas) recevant de même
un identifiant distinct par utilisateur virtuel depuis `scenarios/users.csv`.

### Checks

Deuxième bullet de la [roadmap phase 2](ROADMAP.md#phase-2--des-scénarios-quon-peut-réellement-écrire) :
une assertion logique sur la réponse d'une étape, qui enregistre un échec **sans jamais faire
échouer la requête HTTP** dont elle dérive — `checkout` reste un 200 même si un check sur son
corps échoue. Chaque check devient sa **propre étape** dans le rapport (même table, mêmes
`/metrics`, même seuil possible via `--threshold`), avec son propre compte de succès/échec :

```yaml
steps:
  - name: login
    method: POST
    path: /api/auth/login
    body: '{"username":"demo","password":"demo"}'
    checks:
      - name: has-token
        jsonPath: $.token
      - name: status-ok
        jsonPath: $.status
        expected: ok
```

Même vocabulaire d'expression que la [corrélation dynamique](#corrélation-dynamique-regexxpathjsonpath)
— `regex`, `xpath` ou `jsonPath`, exactement une des trois — plutôt qu'un second langage
d'assertion : un check est une extraction dont on ne garde que le résultat booléen. Sans
`expected`, le check réussit dès que l'expression trouve quelque chose ; avec, il ne réussit que
si la valeur trouvée lui est identique (comparaison de texte exacte).

Le nom d'un check partage l'espace de noms des noms d'étape — les deux deviennent chacun leur
propre ligne du même rapport — un check ne peut donc pas porter le nom d'une étape existante, ni
d'un autre check, dans tout le scénario ; une collision est rejetée au chargement.

Un check qui échoue compte comme n'importe quelle étape pour l'issue de l'itération dans son
ensemble (`__iteration`) — seule la requête HTTP dont il dérive reste inchangée. C'est cohérent
avec l'extraction manquée (étape 9) : un problème logique reste visible dans le signal global,
sans être imputé à tort au transport.

Vérifié par un vrai tir contre `Tempest.SampleTarget` : `login` avec un check qui trouve
toujours son jeton (`has-token`, 0 % d'échec) et un second qui ne trouve jamais le champ qu'il
cherche (`status-ok`, absent de la réponse réelle, 100 % d'échec) — `login` lui-même reste à 0 %
d'échec sur les 15 itérations, confirmant qu'un check qui échoue ne rejaillit jamais sur la
requête HTTP dont il dérive.

Scénario **scripté** : rien de nouveau n'est nécessaire — un script a déjà accès à
`System.Text.RegularExpressions`/`System.Xml`/`System.Text.Json` et peut publier n'importe quelle
assertion comme sa propre étape (`registry.Register(...)` puis `context.BeginStep(...)` /
`.Complete(...)`), exactement le mécanisme que `CheckRule` automatise pour le format déclaratif.

## Scénarios scriptés (Roslyn)

Le format déclaratif ci-dessus ne sait pas exprimer de branchement ni de boucle — la limite
documentée depuis l'étape 6. Décision structurante de la [roadmap
phase 2](ROADMAP.md#phase-2--des-scénarios-quon-peut-réellement-écrire) : plutôt que d'enrichir
indéfiniment un langage de configuration, un scénario peut désormais être un vrai script C#,
compilé à la volée par Roslyn (`Microsoft.CodeAnalysis.CSharp.Scripting`).

Un fichier `.csx`/`.cs` doit se terminer par une expression qui produit un `IWorkflow` — le plus
souvent l'instanciation d'une classe déclarée juste au-dessus, exactement comme un scénario écrit
en dur dans `Tempest.Scenarios` :

```csharp
public sealed class PingWorkflow : IWorkflow
{
    private StepId _pingStep;
    public string Name => "ping";
    public void RegisterSteps(StepRegistry registry) => _pingStep = registry.Register("ping");

    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(_pingStep);
        using HttpResponseMessage response = await context.HttpClient.GetAsync("/ping", cancellationToken);
        scope.CompleteHttp((int)response.StatusCode);
    }
}

new PingWorkflow()
```

```bash
tempest run scenario.csx --target-url http://localhost:5299 --rps 50 --duration 30s
```

`System`, `System.Collections.Generic`, `System.Net.Http`, `System.Threading(.Tasks)`,
`Tempest.Domain.Data`, `Tempest.Domain.Execution`, `Tempest.Domain.Metrics` et
`Tempest.Scenarios.Data` sont importés par défaut ; un script ajoute ses propres `using` pour le
reste (`System.Text.Json.Nodes`, `System.Net.Http.Json`...). Toutes les assemblies déjà chargées
dans le processus hôte sont visibles du script sans configuration : `Tempest.Scenarios` pour
réutiliser `DynamicCheckoutWorkflow` comme base, ou charger un [jeu de données](#jeux-de-données)
via `DataSetLoader.LoadFromFile(...)` dans `SetUpAsync`, par exemple.

[`scenarios/scripted-checkout.csx`](scenarios/scripted-checkout.csx) démontre deux choses que le
déclaratif ne peut pas exprimer aussi simplement : une boucle de nouvelle tentative bornée sur
`checkout` (arrêt anticipé dès qu'il ne s'agit plus d'une 503 temporaire) ; et un jeu de données
([`scenarios/users.csv`](scenarios/users.csv)) chargé dans `SetUpAsync`, un identifiant réel par
utilisateur virtuel plutôt que `demo`/`demo` en dur pour tout le monde.

**Un script s'exécute avec la confiance totale du processus** : rien n'est sandboxé, comme un
script k6 (JavaScript) ou NBomber (C# aussi) — propriété inhérente à la décision, pas un oubli.

Vérifié par de vrais tirs : `scripted-checkout.csx` exécuté via `tempest run`, jeton mis en cache
par utilisateur virtuel (`context.State`) — 400 itérations avec `--max-vus 20`, seulement 20
appels réels à `login`, les 380 autres réutilisant le jeton mis en cache, exactement comme
`DynamicCheckoutWorkflow` ; une erreur de compilation et un script sans expression finale
produisent tous deux un message d'erreur clair plutôt qu'une exception Roslyn brute. Ré-exécuté
après l'ajout du jeu de données : chacun des 4 utilisateurs virtuels reçoit un nom d'utilisateur
distinct de `users.csv` (confirmé par instrumentation temporaire), toujours 0 % d'échec sur
`login`/`browse`/`checkout` — ce tir a aussi révélé qu'un script consommant un jeu de données a
besoin de `System.Collections.Generic` dans les imports par défaut (`IReadOnlyDictionary<,>`),
corrigé ici plutôt que découvert plus tard par un utilisateur externe.

**Limites** :

- Mode distribué (Master/Workers) non pris en charge pour les scénarios scriptés —
  `WorkerCoordinator` reste câblé uniquement sur le format déclaratif ; un `.csx` en mode
  distribué échoue avec l'erreur `NotSupportedException` existante (« Utilisez .yaml, .yml ou
  .json »).
- **Binaires autonomes fichier unique non pris en charge** (`--rps`/`--from-rps`/etc. et
  scénarios déclaratifs/intégrés continuent de fonctionner normalement depuis ces binaires,
  seuls les `.csx`/`.cs` sont concernés) : résoudre les références d'un script a besoin du
  chemin sur disque des assemblies déjà chargées (`Assembly.Location`), qui est toujours vide
  pour un publish `PublishSingleFile` — les assemblies vivent dans le bundle, jamais sur disque.
  Détecté explicitement (`NotSupportedException` avec message clair) plutôt que de laisser
  Roslyn échouer avec une liste de références vide. A d'abord cassé la publication elle-même
  (`IL3000`, promu en erreur par `TreatWarningsAsErrors`) avant d'être trouvé et corrigé.
  Utilisez `dotnet tool install`/`dotnet run` (dépendant du framework) pour un scénario scripté.

## Protocole WebSocket

Un scénario peut ouvrir une connexion WebSocket exactement comme il ouvre une requête HTTP,
via le même `IVirtualUserContext` :

```csharp
WebSocketConnection connection = await context.ConnectWebSocketAsync(uri, configureOptions: null, cancellationToken);
await connection.SendTextAsync("ping", cancellationToken);
WebSocketMessage reply = await connection.ReceiveAsync(cancellationToken);
await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken);
```

Aucun ajout à `StepScope` : `Success()` / `Fail()` suffisaient déjà, le protocole n'a rien de
spécifique à mesurer que la mécanique existante ne couvre pas. Contrairement à `HttpClient`,
une `ClientWebSocket` ne se mutualise pas — `ConnectWebSocketAsync` en crée une nouvelle à
chaque appel ; un scénario qui veut garder une connexion ouverte entre deux itérations doit la
conserver lui-même dans `IVirtualUserContext.State`.

`WebSocketEchoWorkflow` (scénario de référence : connexion → aller-retour d'un message texte →
fermeture propre) s'active via :

```json
"Tempest": { "Workflow": "websocket-echo" }
```

`Workflow` est sans effet dès que `ScenarioFile` est renseigné, qui garde la priorité —
`DynamicCheckoutWorkflow` reste le comportement par défaut si ni l'un ni l'autre n'est précisé.
`Tempest.SampleTarget` expose la cible correspondante, un écho Kestrel pur sur `/ws/echo`.

**Poignée de main de fermeture : un piège vérifié en pratique avant d'écrire la moindre ligne
de production.** Une sonde jetable (`ClientWebSocket` + `HttpListener`, hors du dépôt) a permis
de confirmer l'interopérabilité *avant* de construire la fonctionnalité — et a aussi révélé le
piège à éviter : `WebSocket.CloseAsync` effectue une poignée de main **complète** (elle attend
la trame de fermeture du pair) ; si un seul côté ne participe pas à cet échange, l'appel reste
bloqué indéfiniment. `WebSocketConnection.CloseAsync` délègue tel quel à
`ClientWebSocket.CloseAsync`, mais `WebSocketEchoWorkflow` et `Tempest.SampleTarget` s'assurent
tous deux de répondre à une fermeture reçue — vérifié par un test qui échouerait par *timeout*,
pas par assertion, en cas de régression.

Vérifié par un vrai tir (20 utilisateurs virtuels, rampe 0→10→0 RPS sur 10 s, contre
`Tempest.SampleTarget`) : 75 itérations, **0 échec, 0 abandon**, `ws-connect` et `ws-echo`
tous deux à 0 % d'échec, seuils respectés.

## Protocole gRPC — unaire

Portée délibérément minimale (choix explicite) : un appel unaire, un aller-retour.

```csharp
EchoService.EchoServiceClient client = new(channel);
PingResponse response = await client.PingAsync(new PingRequest { Message = "ping" }, cancellationToken: cancellationToken);
```

Aucune connexion à mesurer séparément, contrairement à WebSocket : l'établissement HTTP/2 est
transparent et mutualisé par le `GrpcChannel`, exactement comme le pool de `HttpClient`. Une
seule étape (`grpc-ping`) suffit donc. Le contrat (`protos/tempest_echo.proto`) est un fichier
unique référencé par `Tempest.Scenarios` (client), `Tempest.SampleTarget` (serveur) et
`Tempest.UnitTests` (serveur de test) : un désaccord de contrat échoue à la compilation, pas à
l'exécution — même discipline que pour la déclaration JSON `camelCase` de l'étape 4.

`GrpcEchoWorkflow` (scénario de référence) s'active via :

```json
"Tempest": { "Workflow": "grpc-echo" }
```

**Un piège vérifié en pratique, pas supposé** : un vrai démarrage a révélé que Kestrel, en
clair (`http://`, sans TLS), ne multiplexe **pas** HTTP/1.1 et HTTP/2 sur un même port — sans
négociation ALPN (qui exige TLS), un point d'écoute mixte reste silencieusement en HTTP/1.1
seul, ce qui casserait gRPC sans le moindre message d'erreur explicite au niveau applicatif.
`Tempest.SampleTarget` expose donc gRPC sur un port dédié, HTTP/2 pur
(`SampleTargetOptions.GrpcPort`, 5287 par défaut), à côté du port REST/WebSocket habituel.
`GrpcEchoWorkflowOptions.TargetUri` renseigne cette adresse séparée ; omis, le canal est dérivé
de la `BaseAddress` du client HTTP — suffisant dès que la cible négocie les deux protocoles via
TLS, le cas courant en production.

Second piège, cette fois côté client : `SocketsHttpHandler` refuse par défaut de négocier
HTTP/2 en clair (h2c). `GrpcEchoWorkflow` active explicitement
`AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)`
avant d'ouvrir le canal — sans ce commutateur, l'appel échoue silencieusement plutôt que de
révéler la vraie cause.

Vérifié par un vrai tir (20 utilisateurs virtuels, rampe 0→10→0 RPS sur 10 s, contre
`Tempest.SampleTarget`) : 75 itérations, **0 échec, 0 abandon**, `grpc-ping` à 0 % d'échec,
seuils respectés.

## Protocole gRPC — streaming serveur

Le premier des trois modes de streaming gRPC : un appel, un flux de messages reçus, dont le
nombre est décidé par le serveur.

```csharp
AsyncServerStreamingCall<StreamEchoMessage> call = client.StreamEcho(new StreamEchoRequest { Message = "ping" }, cancellationToken: cancellationToken);
while (await call.ResponseStream.MoveNext(cancellationToken))
{
    StreamEchoMessage current = call.ResponseStream.Current;
}
```

**Chaque message reçu est mesuré comme sa propre étape** (`grpc-stream-message`), pas l'appel
entier : `GrpcStreamEchoWorkflow` ouvre un `StepScope` frais juste avant chaque
`MoveNext`, donc la latence rapportée est celle de l'attente entre deux messages — la mesure
naturelle pour une API de flux, distincte du temps total de l'appel. Aucune étape "connexion"
séparée, même raisonnement que pour l'appel unaire : l'établissement HTTP/2 reste transparent.

Le nombre de messages envoyés est décidé par le **serveur**
(`SampleTargetOptions.StreamMessageCount`, 5 par défaut), jamais par le client : un client
réaliste ne dicte pas le comportement d'un flux auquel il s'abonne, il lit jusqu'à ce que le
serveur cesse d'émettre (`MoveNext` renvoie `false`) — ce dernier appel n'est pas mesuré comme
un message, puisqu'il n'en est pas un.

`GrpcStreamEchoWorkflow` (scénario de référence) s'active via :

```json
"Tempest": { "Workflow": "grpc-stream-echo" },
"GrpcEcho": { "TargetUri": "http://localhost:5287" }
```

Réutilise la même section `GrpcEcho` (donc la même `TargetUri`) que le scénario unaire : même
besoin, mêmes réglages, pas de raison d'en dupliquer une deuxième.

Vérifié par un vrai tir (20 utilisateurs virtuels, rampe 0→10→0 RPS sur 10 s, contre
`Tempest.SampleTarget`) : 75 itérations × 5 messages = 375 mesures sur `grpc-stream-message`,
**0 échec** — un nombre qui correspond exactement à la configuration serveur, confirmant que
chaque message du flux est bien compté une fois, ni plus ni moins.

## Protocole gRPC — streaming client

Le second mode, inverse du premier : c'est le **client** qui décide du nombre de messages
envoyés (`GrpcEchoWorkflowOptions.MessageCount`), le serveur se contente d'accumuler jusqu'à
la fermeture du flux montant puis répond une seule fois avec un récapitulatif.

```csharp
AsyncClientStreamingCall<ClientStreamMessage, ClientStreamSummary> call = client.ClientStreamEcho(cancellationToken: cancellationToken);
await call.RequestStream.WriteAsync(new ClientStreamMessage { Message = "ping", Sequence = 0 });
await call.RequestStream.CompleteAsync();
ClientStreamSummary summary = await call.ResponseAsync;
```

**Une seule étape mesure l'appel entier** (`grpc-client-stream-upload`), contrairement au
streaming serveur qui en mesure une par message reçu : un `WriteAsync` sur le flux montant ne
retourne qu'une fois le message mis en tampon, sans attendre de reconnaissance individuelle — il
n'existe donc aucune latence par message à mesurer avant la réponse récapitulative finale, seul
évènement réellement observable de ce côté.

`GrpcClientStreamEchoWorkflow` (scénario de référence) s'active via :

```json
"Tempest": { "Workflow": "grpc-client-stream-echo" },
"GrpcEcho": { "TargetUri": "http://localhost:5287", "MessageCount": 5 }
```

Réutilise la même section `GrpcEcho` que les deux scénarios précédents (`TargetUri`), avec un
réglage supplémentaire (`MessageCount`) propre aux flux pilotés par le client.

Vérifié par un vrai tir (20 utilisateurs virtuels, rampe 0→10→0 RPS sur 6+8+3 s, contre
`Tempest.SampleTarget`) : 125 itérations, **0 échec** sur `grpc-client-stream-upload` — le
récapitulatif renvoyé par le serveur (nombre de messages, octets totaux) correspond à chaque
fois exactement à ce qui a été envoyé.

## Protocole gRPC — streaming bidirectionnel

Le troisième et dernier mode : un flux ouvert une seule fois pour toute l'itération, sur lequel
client et serveur échangent en **ping-pong** — écrire un message, attendre son écho, mesurer,
recommencer — plutôt qu'en pipeline (écrire plusieurs messages d'avance sans attendre leurs
échos).

**Ce n'est pas une simplification arbitraire.** `IVirtualUserContext` et `StepScope` sont
documentés comme n'étant touchés que par leur propre travailleur, sans aucune synchronisation —
c'est ce qui permet au chemin de mesure de n'allouer et ne verrouiller rien, pour tous les
scénarios. Un pipeline exigerait une tâche d'écriture et une tâche de lecture tournant en
parallèle au sein d'une même itération, toutes deux ouvrant/clôturant des `StepScope` sur le
même contexte — cela violerait cette invariante et forcerait une synchronisation qui coûterait
à tous les scénarios, pas seulement celui-ci. Le ping-pong reste du vrai bidirectionnel au
niveau du protocole (un seul flux, deux sens, réutilisé pour toute l'itération) : c'est
seulement l'usage qu'en fait ce scénario qui reste séquentiel.

```csharp
AsyncDuplexStreamingCall<BidiStreamMessage, BidiStreamMessage> call = client.BidiStreamEcho(cancellationToken: cancellationToken);
await call.RequestStream.WriteAsync(new BidiStreamMessage { Message = "ping", Sequence = 0 });
await call.ResponseStream.MoveNext(cancellationToken);
BidiStreamMessage echo = call.ResponseStream.Current;
```

Comme le streaming serveur, **chaque message mesure sa propre étape**
(`grpc-bidi-stream-message`) : la latence rapportée est celle entre l'écriture d'un message et
la réception de son écho, message par message.

`GrpcBidiStreamEchoWorkflow` (scénario de référence) s'active via :

```json
"Tempest": { "Workflow": "grpc-bidi-stream-echo" },
"GrpcEcho": { "TargetUri": "http://localhost:5287", "MessageCount": 5 }
```

Vérifié par un vrai tir (20 utilisateurs virtuels, rampe 0→10→0 RPS sur 6+8+3 s, contre
`Tempest.SampleTarget`) : 125 itérations × 5 messages = 625 mesures sur
`grpc-bidi-stream-message`, **0 échec**.

## Mode distribué (Master/Workers)

Plusieurs `Tempest.Host` peuvent tirer en parallèle, coordonnés par un maître qui fusionne
leurs résultats en un seul rapport :

```json
"Tempest": { "Role": "master" },
"Master": { "ExpectedWorkers": 2, "RegistrationTimeoutSeconds": 30 }
```

```json
"Tempest": { "Role": "worker" },
"Worker": { "MasterUrl": "http://master:5299", "SelfUrl": "http://worker1:5300" }
```

**Auto-enregistrement dynamique** : chaque worker s'annonce au maître à son démarrage
(`POST /master/register`, avec plusieurs tentatives — rien ne garantit que le maître soit déjà
disponible). Le maître attend `ExpectedWorkers` enregistrements ou l'expiration de
`RegistrationTimeoutSeconds`, au premier des deux, puis distribue le tir aux workers déjà là.

**Fusion statistique correcte, pas une moyenne de centiles.** Chaque worker exporte l'état
**brut** de ses histogrammes de latence (les paniers eux-mêmes) plutôt que des centiles déjà
calculés — `ClusterReportAggregator` les fusionne panier par panier
(`LatencyHistogram.Add(HistogramSnapshot)`) et ne calcule les percentiles finaux qu'**une
seule fois**, sur le résultat fusionné. Combiner directement des centiles déjà calculés (par
exemple moyenner ou maximiser des p99 individuels) serait le piège classique du "centile de
centiles" — statistiquement faux quelle que soit la formule choisie, et inacceptable pour un
outil qui existe précisément pour corriger le *coordinated omission* ailleurs.

**Deux rapports combinés, deux garanties différentes.** `/report` sur le maître reste le
verdict **final et autoritatif** : construit une seule fois, à partir des rapports que chaque
worker pousse (`POST /master/report`) à la fin de son tir local — jamais recalculé après coup.
`/report/live` (nouveau) est rafraîchi en continu **pendant** le tir : le maître sonde
`GET /worker/report/raw` sur chaque worker toutes les `LivePollIntervalSeconds` (2 s par
défaut), fusionne les histogrammes bruts reçus — la même fusion exacte que pour le rapport
final, pas une approximation — et republie le résultat. Le seul compromis est temporel :
l'instant du sondage est approximatif, pas la fusion elle-même. Un worker temporairement
injoignable est ignoré pour ce cycle, pas fatal au tableau de bord.

**Un piège vérifié en pratique** : `/worker/prepare` et `/worker/start` sont deux appels
distincts, pas un seul. Le maître prépare tous les workers d'abord (construction du moteur,
scellement du registre d'étapes), puis les démarre tous en parallèle — rapprochant leurs
départs réels — plutôt que de laisser chaque worker démarrer dès l'arrivée de sa propre
requête HTTP, ce qui aurait cumulé un décalage d'un worker à l'autre.

Le profil de charge et le plafond d'utilisateurs virtuels sont divisés par le nombre de
workers effectivement enregistrés avant distribution — un tir à 1 000 RPS avec 4 workers
devient 250 RPS par worker.

**Propagation du scénario aux workers.** `/worker/prepare` transporte désormais tout ce qu'il
faut pour qu'un worker reconstruise exactement le même scénario que le maître aurait joué
seul : le **contenu** du fichier de scénario déclaratif (`ScenarioDefinitionLoader.ReadRaw`),
pas son chemin — un worker distant (conteneur ou machine séparée) n'a aucune raison de
partager le système de fichiers du maître — et les réglages de chaque scénario codé en dur
(`WebSocketEcho`, `GrpcEcho`, `DynamicCheckout`), lus par le maître depuis sa propre
configuration et transmis tels quels. Comble la limite documentée à l'étape 10 : les workers
construisaient jusqu'ici leur scénario avec des réglages par défaut, quoi que le maître ait
configuré.

**Un bug latent trouvé par cette vérification, pas supposé** : avant cette propagation, un tir
distribué en `grpc-echo` était cassé en pratique — chaque worker construisait
`GrpcEchoWorkflow()` sans options, donc `TargetUri` restait `null` et l'appel retombait sur
`HttpClient.BaseAddress` (le port HTTP classique de la cible), qui ne sait pas parler gRPC en
clair sans le port dédié (limite Kestrel déjà documentée à l'étape 8). Aucun test ne l'avait
révélé puisque personne n'avait encore tiré `grpc-echo` en mode distribué.

Vérifié par deux vrais tirs (1 maître, 2 workers, contre `Tempest.SampleTarget`) :
- Scénario déclaratif (`scenarios/smoke-test.yaml`, référencé uniquement par le maître) :
  154 itérations fusionnées, `login`/`browse`/`checkout` à **0 % d'échec** — jeton
  d'authentification extrait et propagé correctement d'une étape à l'autre sur les deux
  workers.
- `grpc-echo` avec `GrpcEcho:TargetUri` renseigné uniquement côté maître : 154 itérations
  fusionnées, **0 % d'échec** — confirme que le port gRPC dédié est bien atteint par les
  workers, là où le bug ci-dessus aurait échoué.

Vérifié par un vrai tir (1 maître, 2 workers, contre `Tempest.SampleTarget`) : `/report/live`
interrogé à mi-parcours affichait déjà 188 itérations combinées et cohérentes ; le rapport
final en comptait 274, **0 échec** sur toutes les étapes, seuils respectés — une progression
continue, pas un saut brutal entre "rien" et "tout" à la fin.

Autre vérification (1 maître, 2 workers, contre `Tempest.SampleTarget`) : les deux
workers se sont enregistrés, préparés, démarrés, ont tiré et remonté leur rapport, fusionné
en un total de 74 itérations, **0 échec** sur toutes les étapes, seuils respectés — le rapport
combiné se lit comme s'il venait d'un seul processus.

**Authentification du control plane.** `/master/register`, `/master/report`, `/worker/prepare`
et `/worker/start` — les quatre appels qui peuvent détourner un tir distribué (enregistrer un
faux worker, imposer un scénario, falsifier un rapport) — acceptent un secret partagé optionnel :

```json
"Tempest": { "ClusterSharedSecret": "un-secret-partage" }
```

Exigé en `Authorization: Bearer <secret>` dès qu'il est configuré, comparé en temps constant
(`CryptographicOperations.FixedTimeEquals`) pour ne rien révéler par le temps de réponse.
`null` par défaut : ces endpoints restent ouverts tant que l'opérateur ne configure rien —
**délibérément inspiré, puis durci, par rapport à k6** : sa REST API locale (`localhost:6565`)
n'est jamais authentifiée, la documentation officielle recommandant de ne compter que sur le
périmètre réseau (ne pas la lier à `0.0.0.0`) ; son mode distribué via `k6-operator` ne porte
pas non plus de jeton entre l'opérateur et les pods, la sécurité y reposant sur l'isolation
Kubernetes. Le seul jeton de l'écosystème k6 est celui de l'API k6 Cloud (SaaS Grafana) — un
client s'authentifiant vers le service cloud, pas un mécanisme entre les composants d'un tir.
Tempest applique cette même idée de jeton Bearer directement entre maître et workers, ce que
k6 lui-même ne fait pas.

Vérifié par un vrai tir (1 maître, 2 workers, secret partagé configuré des deux côtés) :
154 itérations fusionnées, **0 échec**, code de sortie 0 — l'en-tête est bien envoyé et
accepté sur les quatre appels. Vérifié aussi dans l'autre sens : une requête directe sans
en-tête `Authorization` est rejetée en 401 en moins de 3 ms, sans jamais atteindre la logique
de préparation du worker.

**Prometheus en mode distribué.** L'export existait déjà en mode autonome ; il couvre
maintenant aussi maître et workers, chacun sur son `/metrics` habituel — pas de nouveau port,
pas de nouvelle configuration à activer.

- Chaque **worker** expose ses propres métriques locales, exactement comme le mode autonome :
  `TempestMeter` est construit à la main dans `WorkerCoordinator.Prepare()`, une fois le
  `MetricsAggregator` du tir connu (il n'existe pas avant, voir plus haut) — jusque-là,
  `/metrics` répond, mais sans aucune série `tempest_*`.
- Le **maître** expose une vue **agrégée** de tout le cluster, pas un simple proxy vers un
  worker : `TempestMeter` y est câblé sur `MasterCoordinator.Snapshot`, qui renvoie
  `FinalReport` une fois le tir terminé, `LiveReport` pendant qu'il tourne (le même rapport que
  `/report/live`), ou un rapport vide avant le premier sondage. Simplification assumée : le
  maître n'a pas de fenêtre glissante propre (il ne fait que fusionner des rapports déjà
  construits par les workers), donc `tempest_latency_milliseconds` y reflète le même rapport
  fusionné que les compteurs cumulés, sans distinction glissant/cumulé.
- `TempestMeter` a été découplé de `MetricsAggregator` pour rendre ça possible : son
  constructeur accepte maintenant n'importe quelle source de rapport
  (`Func<StatisticsScope, LoadTestReport>`), avec une surcharge pratique pour le cas courant
  (un agrégateur local). Le mode autonome n'a rien à changer, `MetricsAggregator.Snapshot` s'y
  passe telle quelle.

Vérifié par un vrai tir (1 maître, 2 workers) : `/metrics` sur un worker affiche ses propres
compteurs locaux (`tempest_requests_total`, etc.) ; `/metrics` sur le maître, interrogé en
plein tir, affiche les centiles et compteurs **fusionnés** des deux workers (176 itérations
combinées à l'instant du sondage) — la même donnée que `/report/live`, sous forme Prometheus.

## Conteneurisation

Une seule image sert les trois rôles (autonome/maître/worker) — c'est `Tempest:Role`, pas
l'image, qui les distingue. `docker-compose.yml` démontre le mode distribué en conteneurs
réels, joints par le DNS interne de Docker (nom de service, pas adresse IP) :

```bash
docker compose up --build --exit-code-from master
```

Quatre services : `sampletarget` (la cible), `master` et deux `worker*`. Le maître s'arrête
seul une fois le tir terminé (`Tempest__ExitAfterRun=true`, code de sortie reflétant le
verdict des seuils — utilisable directement en CI) ; les workers restent actifs comme de
vrais services long-vivants, `docker compose down` pour tout arrêter.

**Un piège réel, pas supposé** : le premier démarrage conteneurisé a plané avec
`UriFormatException: Invalid URI: The hostname could not be parsed.` — `Tempest.SampleTarget`
extrayait son port principal via `new Uri(configuration["urls"])`, mais la convention
`ASPNETCORE_URLS=http://+:5281` (« toutes les interfaces », nécessaire en conteneur) utilise
`+` comme hôte, qui n'est pas une syntaxe d'hôte valide au sens strict de `System.Uri` — cette
même ligne fonctionnait en local uniquement parce que `--urls http://localhost:5281` n'a
jamais cette forme. Corrigé par une extraction manuelle du numéro de port, sans passer par
`Uri`.

Vérifié par un vrai `docker compose up` (4 conteneurs réels, résolution par nom de service
Docker) : même pipeline que la vérification manuelle — enregistrement, préparation, démarrage
synchronisé, tir local, remontée des rapports — fusionné en 224 itérations, **0 échec** sur
toutes les étapes, seuils respectés, code de sortie 0.

## Performances mesurées

256 utilisateurs virtuels, paliers de 3 s, 22 cœurs, ServerGC. La sonde exécute deux passes
pour que le prix de l'observabilité soit un chiffre et non une intuition :

| Cible | Moteur seul | Chaîne complète | Allocations (seul → complet) |
|---|---|---|---|
| 10 000 RPS | 9 971 | 9 996 | 115 → 205 o/itération |
| 50 000 RPS | 49 988 | 49 989 | 106 → 200 o/itération |
| 100 000 RPS | 99 974 | 99 986 | 105 → 197 o/itération |

Agréger deux distributions de latence et une fenêtre glissante coûte donc **~95 octets par
itération et zéro RPS** : le débit est identique avec et sans observabilité.

Le chemin de mesure lui-même n'alloue rien (`MetricResult` est *unmanaged*, un test le vérifie).
Les ~105 octets résiduels par itération viennent de l'`AsyncOperation` que le canal de jetons
alloue quand un utilisateur virtuel se met en attente — soit une allocation par itération, pas
par utilisateur. Les supprimer demanderait un tampon circulaire maison : pas encore justifié.

## État d'avancement

- [x] **Étape 1** — Squelette Clean Architecture + couche Domain (contrats, métriques, profils de charge)
- [x] **Étape 2** — `CoordinatedRateLimiter` + `TargetRpsLoadEngine` + `VirtualUserContext`
- [x] **Étape 3** — `LatencyHistogram` + agrégation cumulée *et* glissante + `MetricsProcessor` + instruments
- [x] **Étape 4** — `DynamicCheckoutWorkflow` + `Tempest.SampleTarget` + Host ASP.NET (`/metrics`, `/report`) + premier tir réel
- [x] **Étape 5** — `ThresholdRule`/`ThresholdReport` + `ExitAfterRun` + endpoint `/thresholds` (roadmap P2, avancée en premier : s'appuyait déjà sur le rapport de l'étape 3)
- [x] **Étape 6** — `ScenarioDefinition`/`DeclarativeWorkflow` (YAML/JSON) + `ScenarioFile` (roadmap P1, scope minimal : un scénario = une séquence de requêtes HTTP sans corrélation)
- [x] **Étape 7** — `IVirtualUserContext.ConnectWebSocketAsync` + `WebSocketConnection` + `WebSocketEchoWorkflow` + endpoint `/ws/echo` (roadmap P1, protocoles avancés : WebSocket fait, gRPC restant)
- [x] **Étape 8** — `GrpcEchoWorkflow` (unaire) + `protos/tempest_echo.proto` + port gRPC dédié sur `Tempest.SampleTarget` (roadmap P1, protocoles avancés : gRPC fait en scope minimal — unaire seul, streaming hors périmètre)
- [x] **Étape 9** — `ExtractionRule` (Regex/XPath) + substitution `{{nom}}` dans `DeclarativeWorkflow` (roadmap P3, comble la limite de l'étape 6 : un jeton d'authentification peut désormais se propager d'une étape à l'autre)
- [x] **Étape 10** — Mode distribué Master/Workers : auto-enregistrement dynamique, `ClusterReportAggregator` (fusion d'histogrammes bruts), agrégation en fin de tir (roadmap P2, scope minimal — pas de tableau de bord combiné en temps réel, pas de propagation des options de scénario avancées aux workers)
- [x] **Étape 11** — Conteneurisation : `Dockerfile` pour `Tempest.Host` et `Tempest.SampleTarget`, `docker-compose.yml` démontrant le mode distribué en conteneurs réels, joints par le DNS Docker
- [x] **Étape 12** — Tableau de bord distribué en temps réel : `GET /worker/report/raw` + sondage continu du maître (`MasterOrchestrationHostedService`) + `GET /report/live` combiné, comblant la limite de l'étape 10
- [x] **Étape 13** — `GrpcStreamEchoWorkflow` (streaming serveur) : chaque message reçu mesuré comme sa propre étape, complétant le protocole gRPC de l'étape 8 (roadmap P1, scope minimal — streaming client et bidirectionnel restent hors périmètre)
- [x] **Étape 14** — Propagation du scénario et des options aux workers : `ScenarioDefinitionLoader.ReadRaw` (contenu, pas chemin) + `WebSocketEcho`/`GrpcEcho`/`DynamicCheckout` transmis dans `WorkerPrepareRequest`, comblant la limite de l'étape 10 — a mis au jour un bug latent (`grpc-echo` en mode distribué joignait le mauvais port faute de propagation de `TargetUri`)
- [x] **Étape 15** — Authentification du control plane distribué (`ClusterAuthentication`, secret partagé optionnel en `Authorization: Bearer`, comparaison en temps constant) sur `/master/register`, `/master/report`, `/worker/prepare`, `/worker/start` — conception inspirée de k6, puis durcie (k6 n'authentifie ni sa REST API locale ni son mode distribué via `k6-operator`)
- [x] **Étape 16** — `GrpcClientStreamEchoWorkflow` (streaming client) et `GrpcBidiStreamEchoWorkflow` (streaming bidirectionnel, ping-pong séquentiel), complétant les quatre modes gRPC (unaire, streaming serveur/client/bidirectionnel) et fermant la dernière limite de l'étape 13
- [x] **Étape 17** — Extraction JsonPath (`ExtractionRule.JsonPath`, sous-ensemble propriété/index sur `System.Text.Json.Nodes`) aux côtés de Regex/XPath, fermant la limite documentée depuis l'étape 9
- [x] **Étape 18** — Prometheus en mode distribué : `TempestMeter` découplé de `MetricsAggregator` (source de rapport quelconque), workers exposant leurs métriques locales, maître exposant la vue agrégée du cluster via `MasterCoordinator.Snapshot`
- [x] **Étape 19** — Rapport HTML autonome (`LoadTestReport.ToHtml`, endpoint `/report.html`) : mêmes chiffres que `/report`, verdict des seuils inclus, noms d'étape échappés
- [x] **Étape 20** — Comparaison entre tirs (`LoadTestReportComparison`, outil `tools/Tempest.Compare`) : table console, rapport HTML comparatif, gate CI par pourcentage de régression — clôt le volet rapports/observabilité
- [x] **Étape 21** — Pipeline CI (`.github/workflows/ci.yml`) : build/tests/format sur chaque push et pull request, plus un job de tir de fumée réel (seuils + `ExitAfterRun`) contre `Tempest.SampleTarget`
- [x] **Étape 22** — `Tempest.Cli` (`tempest run [scenario] [options]`) : `--target-url`, `--rps` ou `--from-rps`/`--to-rps` + `--duration`, `--max-vus`, `--workflow`, `--threshold` (répétable), `--report-html`/`--report-json` ; extraction de `StandaloneHost.Run` hors de `Tempest.Host/Program.cs` pour être partagée sans dupliquer le câblage (roadmap phase 1, scope minimal — un seul bullet des cinq : pas de packaging `dotnet tool`, pas de binaires autonomes, pas de mode distribué depuis la CLI)
- [x] **Étape 23** — Packaging `dotnet tool` de `Tempest.Cli` (`PackAsTool`, commande `tempest`), comblant la limite de l'étape 22 — vérifié par une installation globale réelle et un tir depuis un répertoire hors du dépôt ; job CI dédié (`dotnet pack`). Reste hors périmètre : publication sur nuget.org (dépôt encore privé)
- [x] **Étape 24** — Binaires autonomes (Windows/Linux/macOS x64+arm64, self-contained, fichier unique) : `RuntimeIdentifiers` partagés (`Directory.Build.props`), nettoyage des fichiers hérités de `Tempest.Host` (`appsettings*.json`, `web.config`), workflow `release.yml` (publication en release GitHub sur tag `vX.Y.Z`, jamais encore déclenché) — vérifié par un vrai tir du `tempest.exe` win-x64 publié. Native AOT essayé et abandonné : `YamlDotNet.DeserializerBuilder` et une désérialisation JSON par réflexion dans `ScenarioDefinitionLoader` échouent la compilation AOT (`IL3050`/`IL2026`), migration hors périmètre
- [x] **Étape 25** — Licence ([Apache License 2.0](LICENSE)), démarrage rapide en trois commandes en tête de ce README. La visibilité du dépôt (privé → public) reste un geste manuel réservé au propriétaire du dépôt — jamais automatisé depuis une session d'assistant
- [x] **Étape 26** — [Paquets NuGet](#paquets-nuget) : `Tempest.Domain`, `Tempest.Application`, `Tempest.Infrastructure`, `Tempest.Scenarios` — élargi du texte initial de ROADMAP.md (Domain + Scenarios) pour permettre de lancer un tir depuis un projet externe, pas seulement d'écrire un scénario, en vraie parité avec NBomber. Ferme la phase 1 : il ne reste plus qu'un geste manuel (visibilité du dépôt). Vérifié par un vrai tir depuis un projet xUnit sans aucune référence à ce dépôt, uniquement via les paquets NuGet locaux
- [x] **Étape 27** — [Scénarios scriptés (Roslyn)](#scénarios-scriptés-roslyn) : `ScriptedWorkflowLoader`/`WorkflowFileLoader` (`Tempest.Scenarios`), décision structurante de la roadmap phase 2 mise en œuvre — un fichier `.csx`/`.cs` devient un `IWorkflow` compilé à la volée. Vérifié par un vrai tir (`scenarios/scripted-checkout.csx`, boucle de nouvelle tentative sur `checkout`, jeton mis en cache confirmé sur 400 itérations/20 utilisateurs virtuels). Limite documentée : mode distribué non pris en charge pour ce format
- [x] **Étape 28** — Deux corrections trouvées en vérifiant réellement la CI plutôt qu'en la supposant verte : `<RuntimeIdentifiers>` (étape 24) faisait échouer `dotnet pack --no-build` sur une arborescence propre — retiré, `dotnet publish -r <rid>` fonctionne tout aussi bien sans lui. `Assembly.Location` dans `ScriptedWorkflowLoader` (étape 27) faisait échouer la publication *fichier unique* (`IL3000`) — supprimé explicitement, avec un garde qui rejette maintenant un scénario scripté depuis ce genre de binaire par un message clair (`NotSupportedException`) plutôt qu'un crash. Les deux ont été reproduits sur une arborescence entièrement nettoyée avant d'être corrigés, pas devinés
- [x] **Étape 29** — [Jeux de données](#jeux-de-données) : `DataSet`/`DataSetIterationStrategy` (`Tempest.Domain.Data`), `DataSetLoader` CSV/JSON (`Tempest.Scenarios.Data`), section `datasets` du format déclaratif (`{{jeu.colonne}}`, même mécanisme de substitution que les variables extraites) et accès direct depuis un scénario scripté (imports par défaut élargis). Premier bullet de la roadmap phase 2. Vérifié par de vrais tirs : un scénario déclaratif substituant `productId`/`quantity` depuis un CSV avec `uniquePerVirtualUser` (0 % d'échec, une ligne distincte et stable par utilisateur virtuel confirmée par instrumentation temporaire) et `scenarios/scripted-checkout.csx` mis à jour pour utiliser `scenarios/users.csv` — a aussi révélé qu'un script consommant un jeu de données a besoin de `System.Collections.Generic` dans les imports par défaut, corrigé dans le même chantier
- [x] **Étape 30** — [Checks](#checks) : `CheckRule` (`Tempest.Domain.Declarative`, même vocabulaire Regex/XPath/JsonPath que l'extraction, plus une valeur attendue optionnelle), section `checks` par étape du format déclaratif. Chaque check devient sa propre étape du rapport (réutilise `StepId`/`StepScope`/`MetricResult` tels quels, aucun changement dans `Tempest.Application`/`Tempest.Infrastructure`) — un check qui échoue ne fait jamais échouer la requête HTTP dont il dérive, mais compte comme n'importe quelle étape pour l'issue de l'itération. Deuxième bullet de la roadmap phase 2. Sans effet sur les scénarios scriptés : un script publie déjà ce genre d'assertion via `StepRegistry`/`StepScope` directement. Vérifié par un vrai tir : un check qui trouve toujours son jeton (0 % d'échec) et un second qui ne trouve jamais un champ absent de la réponse réelle (100 % d'échec) — l'étape HTTP dont ils dérivent reste à 0 % d'échec dans les deux cas

## Roadmap initiale — close

Les trois priorités identifiées au départ sont faites, chacune dans un scope volontairement
minimal documenté à sa section :

| Priorité | Fonctionnalité |
|---|---|
| ~~P1~~ | ~~Protocoles avancés~~ : WebSockets et les quatre modes gRPC (unaire, streaming serveur/client/bidirectionnel) faits |
| ~~P2~~ | ~~Mode distribué Master/Workers~~ fait (étape 10), tableau de bord combiné en temps réel fait (étape 12) |
| ~~P3~~ | ~~Corrélation avancée : extraction par Regex / XPath / JsonPath~~ fait (étapes 9, 17) |

Trois chantiers de suivi, identifiés une fois les trois priorités closes, sont également faits :
sécurisation du control plane distribué (étape 15), propagation du scénario et des options aux
workers (étape 14), et rapports/observabilité — Prometheus distribué, rapport HTML, comparaison
entre tirs (étapes 18 à 20).

## Et ensuite

Cette roadmap initiale est close : elle traitait de ce que le moteur devait savoir faire. La
suite est un problème différent — Tempest n'est encore installable par personne, et ses scénarios
restent trop pauvres pour un test de charge réel.

**[ROADMAP.md](ROADMAP.md)** couvre ce qui manque pour exister face à k6, Gatling et NBomber :
matrice concurrentielle honnête, huit phases ordonnées par dépendance, et une correction
importante — l'argument « Tempest corrige le *coordinated omission*, contrairement aux autres »
est **faux** (les trois proposent un modèle ouvert) ; le différenciateur réel est ailleurs.

## Licence

[Apache License 2.0](LICENSE) — la même que k6 et Gatling.
