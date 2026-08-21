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
lisible directement dans un navigateur, sans serveur ni JSON à interpréter), `.../report/live.html`
(la fenêtre glissante en page HTML, qui se recharge seule pendant le tir — voir
[Tableau de bord temps réel](#tableau-de-bord-temps-réel)), `.../metrics` (Prometheus).

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

### Modèle fermé

Tempest ne sait piloter qu'un débit cible (modèle *ouvert*) : « exactement N utilisateurs
simultanés » — le besoin le plus courant dans les outils historiques — n'avait pas d'équivalent.
`--vus <n>` couvre ce cas, à côté du modèle ouvert plutôt qu'à sa place :

```bash
tempest run --target-url http://localhost:5281 --vus 50 --duration 30s
```

Exactement 50 utilisateurs virtuels enchaînent les itérations sans aucune pause imposée, jusqu'à
expiration de la durée — le débit résultant dépend entièrement de la latence de la cible, à
l'opposé du modèle ouvert. `--vus` est mutuellement exclusif avec `--rps`/`--from-rps`/`--to-rps`
(un seul modèle par tir) et avec `--max-vus` (`--vus` fixe déjà l'effectif exact, ce n'est pas un
plafond) ; il exige `--duration`, faute de profil de débit dont dériver une durée de tir.

**La mise en garde n'est pas cosmétique.** En modèle fermé, chaque jeton porte l'instant de sa
propre émission plutôt qu'un instant planifié à l'avance : il n'existe rien à comparer, donc pas
de correction du *coordinated omission* — précisément le biais que le modèle ouvert existe pour
éviter (voir [Décisions structurantes](#décisions-structurantes)). `LoadTestReport.ClosedModel`
porte ce fait jusque dans le rapport JSON, et `ToTable()`/`ToHtml()` l'affichent dans le même
emplacement que l'avertissement « mesures perdues » : un opérateur qui compare deux tirs sans
relire les options de la CLI doit encore voir que l'un des deux n'est pas comparable à l'autre.

Le nombre d'utilisateurs virtuels n'est pas un paramètre du nouvel ordonnanceur
(`ClosedModelScheduler`) : il vient du nombre de travailleurs déjà créés par le moteur
(`LoadTestOptions.MaxVirtualUsers`), qui borne également la concurrence en modèle ouvert.
`ClosedModelScheduler` se contente d'émettre en continu dans le canal borné existant ; c'est la
contre-pression du canal — un nouveau jeton n'est écrit que lorsqu'un utilisateur virtuel vient de
se libérer — qui fait émerger le modèle fermé, sans aucun mécanisme de synchronisation dédié.

Limite : mode distribué non pris en charge pour ce modèle — `WorkerCoordinator` reste câblé sur
`CoordinatedRateLimiter`/`LoadProfile` (modèle ouvert) seul, comme le format scripté l'était déjà
pour d'autres raisons. Vérifié par un vrai tir (`--vus 10 --duration 5s` contre
`Tempest.SampleTarget`) : effectif exact, avertissement présent dans le rapport texte et le JSON,
modèle ouvert inchangé en régression.

### Montée d'utilisateurs

Le modèle fermé ci-dessus fixe un effectif *constant*. Beaucoup de tirs réels veulent au contraire
observer une dégradation progressive — « monter à 50 utilisateurs sur 2 minutes » — sans jamais
viser un débit. `--vus-from`/`--vus-to` couvre ce cas :

```bash
tempest run --target-url http://localhost:5281 --vus-from 0 --vus-to 50 --duration 2m
```

L'effectif concurrent passe linéairement de 0 à 50 sur la durée donnée (une rampe descendante,
`--vus-from 50 --vus-to 0`, fonctionne symétriquement). Mêmes règles d'exclusion mutuelle que
`--vus` : incompatible avec `--rps`/`--from-rps`/`--to-rps` (modèle ouvert) et avec `--max-vus`
(l'effectif suit déjà les paliers, jusqu'à leur pic) ; `--duration` est obligatoire. Une rampe
« montée, plateau, descente » à plusieurs paliers reste possible via un `appsettings.json`, section
`Tempest:RampVus` — la CLI n'exprime qu'un seul palier, comme elle ne le fait déjà que pour un seul
palier de débit (`--from-rps`/`--to-rps`).

Techniquement, `RampingVirtualUserPool` (`Tempest.Application.Execution`) remplace la création
statique de travailleurs du moteur : il en crée de nouveaux quand l'effectif cible monte et en
arrête individuellement quand il descend — chaque travailleur reçoit son propre jeton d'annulation
plutôt que celui du tir, ce qui permet d'arrêter un utilisateur virtuel sans fermer la file de
jetons partagée par les autres. L'émission des jetons elle-même reste inchangée : un
`ClosedModelScheduler` configuré sur la durée totale du profil continue d'alimenter la file en
continu, exactement comme pour un effectif fixe. Même mise en garde de rapport que le modèle fermé
à effectif fixe (`LoadTestReport.ClosedModel`) : la montée d'utilisateurs n'a pas plus
d'échéancier théorique à comparer que l'effectif constant.

Limite : mode distribué non pris en charge, comme pour l'effectif fixe. Vérifié par un vrai tir
(`--vus-from 0 --vus-to 20 --duration 8s` contre `Tempest.SampleTarget`) : débit croissant au fil
de la rampe, avertissement présent dans le rapport texte et le JSON, modèles ouvert et fermé à
effectif fixe inchangés en régression.

### Itérations partagées et itérations par utilisateur

Ni le modèle fermé ni sa montée ne répondent à « fais tourner ce script 1 000 fois » ou « chaque
utilisateur en fait exactement 20, peu importe le temps que ça prend » — deux besoins pilotés par
un nombre d'itérations plutôt qu'une durée. Deux nouveaux exécuteurs couvrent ce cas.

**Itérations partagées** (`--iterations`) : un total dispute par au plus `--max-vus` utilisateurs
virtuels, premier arrivé premier servi — même convention de plafond que le modèle ouvert.

```bash
tempest run --target-url http://localhost:5281 --iterations 1000 --max-vus 20
```

**Itérations par utilisateur** (`--vus`/`--iterations-per-vu`) : chacun des `n` utilisateurs
virtuels fixés par `--vus` en exécute exactement `k`, indépendamment des autres — contrairement à
l'exécuteur partagé, un utilisateur virtuel rapide ne « vole » jamais les itérations d'un plus
lent. `--iterations-per-vu` prend la place de `--duration` comme condition d'arrêt de `--vus` :

```bash
tempest run --target-url http://localhost:5281 --vus 10 --iterations-per-vu 20
```

Aucun des deux n'a de notion de débit ni de durée : ni `--rps`/`--from-rps`/`--to-rps`, ni
`--vus-from`/`--vus-to`, ni `--duration` n'ont de sens ici (mutuellement exclusifs). Même mise en
garde de rapport que le modèle fermé (`LoadTestReport.ClosedModel`) : sans débit cible, il n'y a
pas d'échéancier théorique à comparer.

Techniquement, les deux réutilisent un seul nouvel ordonnanceur, `IterationCountScheduler`
(`Tempest.Application.Execution`) : il émet exactement un nombre fixe de jetons puis s'arrête,
plutôt que de s'arrêter sur une durée comme `ClosedModelScheduler`. La différence entre les deux
exécuteurs se joue entièrement côté travailleur : `VirtualUserWorker` accepte désormais un quota
personnel optionnel (`LoadTestOptions.IterationsPerVirtualUser`) au-delà duquel il s'arrête de
lui-même, sans jamais fermer la file partagée par les autres. Combiné à un
`IterationCountScheduler` dimensionné à effectif × quota, cet auto-arrêt garantit — par
construction, aucun travailleur ne peut en prendre plus que son quota et le total émis égale
exactement la somme des quotas — que chaque utilisateur virtuel en fait exactement sa part.
Itérations partagées n'utilise que l'ordonnanceur, sans quota individuel : la répartition inégale
au gré de qui répond le plus vite au canal partagé est le comportement voulu.

Limite : mode distribué non pris en charge pour les deux, comme pour le reste du modèle fermé.
Vérifié par de vrais tirs (`--iterations 300 --max-vus 20` puis `--vus 10 --iterations-per-vu 20`
contre `Tempest.SampleTarget`) : 300 puis 200 itérations exactement, avertissement présent dans le
rapport texte et le JSON, modèle ouvert inchangé en régression.

### Scénarios concurrents

Tous les modèles ci-dessus pilotent un seul scénario à la fois. Un tir réaliste veut souvent en
faire tourner plusieurs *en même temps* dans le même processus — « la navigation à 20 RPS pendant
que le paiement monte en charge » — chacun avec son propre profil, ses propres étiquettes et ses
propres seuils, sans que les mesures de l'un se mélangent à celles de l'autre.

Reste, comme un profil de charge à plusieurs paliers (`Tempest:RampVus`), l'affaire d'un
`appsettings.json` du répertoire courant plutôt que d'une syntaxe `--scenario` à inventer sur la
ligne de commande — un tableau de scénarios n'a pas d'équivalent plat raisonnable :

```json
{
  "Tempest": {
    "TargetBaseUrl": "http://localhost:5281",
    "Scenarios": [
      {
        "Name": "checkout-open",
        "MaxVirtualUsers": 10,
        "Profile": [{ "FromRps": 20, "ToRps": 20, "DurationSeconds": 60 }],
        "ScenarioFile": "checkout.yaml",
        "Thresholds": [{ "StepName": "browse", "Metric": "ErrorRate", "Comparison": "LessThanOrEqual", "Limit": 0.0 }]
      },
      {
        "Name": "browse-closed",
        "MaxVirtualUsers": 5,
        "ClosedModelDuration": "00:01:00",
        "ScenarioFile": "browse.yaml"
      }
    ]
  }
}
```

Chaque entrée de `Tempest:Scenarios` accepte le même vocabulaire que `TempestHostOptions` lui-même
(`Profile`, `ClosedModelDuration`, `RampVus`, `SharedIterations`, `IterationsPerVirtualUser`,
`MaxRequestsPerSecond`, `ScenarioFile`/`Workflow`, `Thresholds`) — chaque scénario choisit son
modèle de charge indépendamment des autres. `TargetBaseUrl` reste optionnel par scénario : omis, il
retombe sur celui du tir entier, ce qui couvre le cas courant où tous les scénarios visent la même
cible — `MaxRequestsPerSecond` suit la même convention de repli (voir [Bridage](#bridage)).

Techniquement, `MultiScenarioRunner` (`Tempest.Host.Execution`) construit à la main, pour chaque
scénario, sa propre chaîne complète — `IWorkflow`, `ILoadScheduler`, `HttpClient`,
`StepRegistry`/`MetricsAggregator` — plutôt que de passer par le conteneur d'injection de
dépendances, qui ne sait enregistrer qu'un singleton de chaque type. C'est cet isolement complet,
pas un simple préfixe de nom, qui garantit que deux scénarios déclarant tous les deux une étape
`browse` produisent deux lignes indépendantes dans le rapport combiné (`MultiScenarioReport`),
jamais une seule fusionnée. Les scénarios tournent en parallèle (`Task.WhenAll`) : un scénario plus
long n'est jamais tronqué par la fin anticipée d'un autre.

Limites de cette première version : mode distribué non pris en charge, `/report/live` et
`/metrics` (Prometheus) non alimentés — seuls `/report`, `/report.html` et `/thresholds` le sont,
une fois le tir entièrement terminé. Vérifié par un vrai tir à deux scénarios (l'un en modèle
ouvert, l'autre en modèle fermé, tous deux avec une étape `browse` de même nom) contre
`Tempest.SampleTarget` : deux entrées indépendantes dans le rapport (100 puis 943 itérations,
jamais 1043 fusionnées), étiquettes et seuils propres à chacune, avertissement modèle fermé présent
sur la seule entrée concernée, code de sortie reflétant le verdict combiné.

### Bridage

Tous les modèles ci-dessus décrivent *ce que le tir doit produire* — un débit cible, un effectif,
un nombre d'itérations. Aucun ne répond à « quoi que produise ce profil, ne dépasse jamais X
requêtes par seconde », utile pour respecter un quota côté cible ou reproduire un plafond
d'infrastructure réel. `--max-rps` couvre ce cas, en s'appliquant *par-dessus* le modèle choisi,
jamais à sa place :

```bash
tempest run --target-url http://localhost:5281 --rps 100 --duration 30s --max-rps 20
```

À la différence de tous les indicateurs précédents, `--max-rps` n'est mutuellement exclusif avec
rien : il compose avec `--rps`/`--from-rps`/`--to-rps` (modèle ouvert), `--vus`/`--vus-from`/
`--vus-to` (modèle fermé), `--iterations`/`--iterations-per-vu`, et avec `Tempest:Scenarios`, où il
sert de plafond par défaut pour tout scénario qui ne précise pas le sien (`MaxRequestsPerSecond`)
— même convention que `TargetBaseUrl`. Sans équivalent `--max-vus` distinct par scénario avant
cette fonctionnalité, un scénario concurrent peut désormais aussi porter son propre plafond,
indépendant de celui du tir entier.

Techniquement, `RateCappedScheduler` (`Tempest.Application.Execution`) est un décorateur
d'`ILoadScheduler` : il enveloppe le `ChannelWriter` remis à l'ordonnanceur choisi (modèle ouvert,
fermé, montée d'utilisateurs ou itérations) et retarde la transmission de chaque jeton jusqu'à ce
que l'intégrale du plafond l'autorise — même principe que `CoordinatedRateLimiter` (comparer prévu
et émis, jamais un délai par jeton, pour ne pas laisser la cadence dériver). Aucun des quatre
ordonnanceurs existants n'a besoin de savoir qu'il est bridé.

**Le retard ainsi imposé se mesure comme une dette d'ordonnancement ordinaire**, pas comme un cas
particulier à masquer : `ExecutionToken.ScheduledTicks` reste celui que l'ordonnanceur enveloppé
avait prévu, jamais réécrit par le décorateur, donc l'écart entre ce qui était prévu et l'instant où
la requête part réellement apparaît dans `Response` exactement comme un injecteur saturé — cohérent
avec le reste de Tempest, qui existe pour montrer ce genre d'écart, pas pour le cacher.

Vérifié par de vrais tirs contre `Tempest.SampleTarget` : `--rps 100 --duration 5s --max-rps 20`
a produit 500 itérations (le total planifié par le profil à 100 RPS) étalées sur 25s pour ne
jamais dépasser 20 RPS, avec une dette maximale d'environ 20s reflétant fidèlement le retard
imposé ; `--vus 10 --duration 5s --max-rps 15` (modèle fermé, qui produit naturellement 176 RPS
avec ces 10 utilisateurs virtuels contre cette cible) a été ramené à 15 RPS exactement ; et un tir
à deux scénarios concurrents a confirmé le plafond propre à un scénario (5 RPS) et le repli sur le
plafond global (8 RPS) pour celui qui n'en précise pas.

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

## Série temporelle

Un rapport `/report` classique ne dit que l'état final : le débit moyen sur tout le tir, jamais
le moment où les centiles ont décroché. `LoadTestReport.TimeSeries` ajoute la trajectoire — un
point relevé à intervalle régulier (`Tempest:TimeSeriesIntervalSeconds`, 2 s par défaut), du
début à la fin du tir, chacun portant le débit, le nombre d'utilisateurs virtuels actifs, le
taux d'erreur, les centiles p50/p95/p99 et la dette d'ordonnancement maximale — tous relevés sur
la fenêtre glissante, exactement comme `/report/live`.

```
serie temporelle (fenetre glissante)
      t     it/s vus actifs   echecs       p50       p95       p99  dette max
   2,0s        0          3   0,0 %    0,00ms    0,00ms    0,00ms     0,00ms
   4,1s       12          7   0,0 % 1384,45ms 2080,77ms 2118,87ms  2043,32ms
   6,1s       38         10   0,0 %  626,69ms 1941,50ms 2118,87ms  2043,32ms
   8,1s       76         14   0,0 %  532,48ms 1736,70ms 2080,77ms  2043,32ms
  10,1s      121         18   0,0 %  434,18ms 1384,45ms 2023,42ms  2043,32ms
```

Ce relevé, pris pendant une montée de 2 à 20 utilisateurs virtuels sur 10 s, rend visible ce
qu'un seul état final aurait caché : l'effectif qui grimpe (colonne « vus actifs »), le débit qui
suit, et une dette d'ordonnancement qui apparaît dès que la cible commence à ne plus absorber la
charge — la même donnée que `/report/live`, mais gardée dans le temps plutôt qu'écrasée à chaque
sondage.

Techniquement, `TimeSeriesRecorder` (`Tempest.Application.Metrics`) tourne en parallèle du moteur
plutôt qu'à l'intérieur de lui — `TargetRpsLoadEngine` ne détient aucune référence vers un
`MetricsAggregator`, il ne fait qu'écrire des mesures dans un puits — et relève à chaque
intervalle un `Snapshot(StatisticsScope.Sliding)` plus une nouvelle jauge,
`ActiveVirtualUserGauge` (`Tempest.Application.Execution`), incrémentée/décrémentée par chaque
`VirtualUserWorker` à l'entrée et à la sortie de sa boucle de consommation — la seule façon
d'observer la concurrence *réelle* dans le temps, par opposition au plafond ou à l'effectif
configuré. `LoadTestHostedService` démarre ce relevé sur un jeton d'annulation distinct de celui
du tir : `stoppingToken` ne se déclenche qu'à l'arrêt de l'hôte, jamais à la seule fin naturelle
d'un tir à durée ou à itérations fixes, donc le relevé a besoin de son propre jeton, annulé dès
que le tir se termine, pour ne pas tourner indéfiniment après.

Un tir plus court que l'intervalle de relevé garde toujours au moins un point — le dernier
relevé est pris sans condition juste avant de rendre la main, jamais seulement à l'intérieur de
la boucle périodique.

Limite de cette version : non alimentée pour un tir à
[scénarios concurrents](#scénarios-concurrents) (`MultiScenarioRunner` construit sa propre chaîne
de mesure sans enregistreur de série temporelle). Rendue en table *et* en graphe — voir
[Courbe de dette d'ordonnancement superposée](#courbe-de-dette-dordonnancement-superposée)
ci-dessous. Vérifié par un vrai tir (`--vus-from 2 --vus-to 20 --duration 10s`) contre
`Tempest.SampleTarget` : effectif actif croissant fidèle à la rampe (3 → 7 → 10 → 14 → 18), débit
croissant en conséquence, dernier point pris après la fin du tir avec effectif retombé à 0.

### Distribution des temps de réponse

Un centile ment par omission : un p95 propre et une distribution bimodale (la moitié des requêtes
très rapides, l'autre très lentes) peuvent produire exactement le même chiffre. `LatencyHistogram`
détient déjà les paniers bruts qui feraient la différence — il les utilise pour calculer les
centiles — mais ne les exposait pas. `StepStatistics.ResponseHistogram` les publie, un histogramme
par étape, agrégés dans `LoadTestReport.ToHtml` sous une section « Distribution des temps de
réponse » : les 3 072 paniers de `LatencyHistogram` (bien trop fins pour un graphe lisible) sont
regroupés par octave — le découpage natif de l'histogramme lui-même — puis seules les octaves qui
couvrent réellement la distribution observée sont rendues en barres SVG, avec l'infobulle de
chaque barre donnant sa borne haute exacte et son nombre de mesures.

Limite : seul le temps de réponse corrigé (`Response`) est exposé, jamais le temps de service brut
(`Service`) — c'est la distribution à publier, voir la remarque de classe de `StepStatistics`.
Vérifié par un vrai tir bridé (`--rps 300 --max-rps 20`) contre `Tempest.SampleTarget` : un
histogramme par étape dans le rapport HTML, cohérent avec les percentiles publiés à côté, et le
même histogramme brut (3 072 paniers) présent dans le rapport JSON.

### Courbe de dette d'ordonnancement superposée

Une table de centiles ne montre jamais *quand* un injecteur ou une cible décroche — seulement
l'état final. `LoadTestReport.ToHtml` superpose désormais, sur `LoadTestReport.TimeSeries`, un
graphe en ligne SVG : débit et dette d'ordonnancement maximale, chacun mis à l'échelle de son
propre maximum (leurs unités n'ont rien de comparable), sur le même axe des temps. Une dette qui
grimpe pendant que le débit stagne ou chute est la signature visuelle d'une saturation — invisible
dans un tableau de centiles, et le graphe qu'aucun outil qui ne corrige pas le *coordinated
omission* ne peut produire.

Rendu uniquement à partir de deux points de trajectoire ou plus (une seule mesure ne fait pas une
courbe) ; sans effet si `TimeSeries` est vide, même limite que la série temporelle elle-même.
Vérifié par un vrai tir volontairement bridé bien en deçà de la demande (`--rps 300 --max-rps 20`,
soit 15 fois le débit réellement transmis) : la courbe de débit plafonne immédiatement à 20 it/s
tandis que la dette d'ordonnancement grimpe de façon quasi linéaire, exactement le retard imposé
par le bridage — visible en un coup d'œil, là où le tableau juste en dessous demande de parcourir
des dizaines de lignes.

### Tableau de bord temps réel

`/report/live` existe depuis longtemps, mais reste du JSON brut — illisible pendant un tir en
cours sans outil pour l'interpréter. `/report/live.html` sert le même
`aggregator.Snapshot(StatisticsScope.Sliding)`, cette fois rendu par `LoadTestReport.ToHtml` (donc
avec la même distribution de latences et la même mise en page que le rapport final), et ajoute une
balise `<meta http-equiv="refresh">` qui recharge la page seule toutes les
`TempestHostOptions.LiveDashboardRefreshSeconds` (3 s par défaut) : ouvrir cette URL dans un
navigateur pendant le tir suffit, sans script ni extension.

Un rechargement de page entier plutôt qu'un flux `EventSource`/SSE — aucun des deux n'existait déjà
dans `Tempest.Host`, et un tableau de bord d'opérateur n'a pas besoin d'une latence de mise à jour
inférieure à quelques secondes pour rester utile. Limite : comme `/report/live`, non alimenté pour
un tir à [scénarios concurrents](#scénarios-concurrents). Vérifié par un vrai tir : `/report/live.html`
interrogé deux fois à quelques secondes d'écart pendant la montée en charge par défaut montre le
débit progresser (49 puis 99 itérations/s) avec la balise de rechargement présente à chaque fois,
tandis que `/report.html` (rapport cumulé, sans le paramètre d'auto-rechargement) ne la porte
jamais.

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

## Convertisseur HAR

Écrire un premier scénario à la main est le moment où l'on abandonne un outil : `tools/Tempest.HarConvert`
part d'un export « Enregistrer tout en HAR » des outils de développement d'un navigateur (Chrome,
Firefox) plutôt que d'une page blanche.

```bash
dotnet run --project tools/Tempest.HarConvert -- session.har scenario.csx --name mon-scenario
```

Conformément à la [décision structurante de la roadmap](ROADMAP.md) — les convertisseurs
génèrent du C#, pas du YAML/JSON — la sortie est un scénario **scripté** (`.csx`), directement
jouable via `--scenario`/`Tempest:ScenarioFile` sans aucun câblage supplémentaire, exactement
comme [`scenarios/scripted-checkout.csx`](scenarios/scripted-checkout.csx). Chaque requête HAR
devient une étape qui rejoue sa méthode, son chemin, son corps (avec le bon `Content-Type`) et
ses en-têtes via `context.HttpClient.SendAsync`.

Deux filtrages, comptés et rapportés sur la sortie standard, jamais silencieux :
- **Actifs statiques** (`.css`, `.js`, `.png`, polices, etc.) — un HAR de chargement de page
  complet en est majoritairement fait, et aucun n'a de sens à rejouer contre un tir de charge.
- **Hôtes secondaires** — l'hôte cible retenu est le **plus fréquent** du HAR, jamais le premier
  rencontré : un appel tiers (police, analytics, CDN) sans extension reconnue dans son chemin
  apparaît souvent avant le premier appel à l'API réellement testée, et le prendre pour hôte de
  base ferait passer la cible elle-même pour « un autre hôte » — bug réel trouvé en vérifiant un
  vrai HAR, corrigé avant de documenter cette section.

Limite volontaire, documentée en tête du fichier généré : les en-têtes `Authorization`/cookies
capturés sont des valeurs de session réelles, presque certainement expirées au moment de la
conversion — à revoir manuellement, comme toute corrélation dynamique (`Extract`) reste hors de
portée d'un simple mapping de requêtes. Corps multipart (upload de fichier) non pris en charge :
seul le texte brut d'un `postData.text` est converti.

Vérifié par un vrai tir : un HAR reconstitué à partir d'un véritable aller-retour login/catalogue/
checkout contre `Tempest.SampleTarget`, mêlé à un actif statique et à un appel vers un second hôte
sans extension reconnue (pour vérifier le choix de l'hôte le plus fréquent en conditions réelles),
converti puis exécuté via `tempest run scenario.csx --target-url ... --rps 5 --duration 5s` :
les 3 étapes réelles converties, actif statique et hôte secondaire bien exclus, `login` et
`browse` à 0 % d'échec, `checkout` à 100 % d'échec — le jeton capturé avait expiré au moment du
tir, exactement la mise en garde documentée plus haut, pas une anomalie.

## Convertisseur OpenAPI

Deuxième bullet de la phase 5 : `tools/Tempest.OpenApiConvert` part d'une spécification OpenAPI
3.x (JSON — l'export le plus courant, `swagger.json`/`openapi.json`) plutôt que d'un trafic
capturé. Contrairement au convertisseur HAR, une spécification ne décrit que la **forme** d'une
API, jamais des données réelles : la sortie est délibérément un **squelette**, pas un scénario
directement jouable.

```bash
dotnet run --project tools/Tempest.OpenApiConvert -- openapi.json scenario.csx --name mon-scenario
```

Même sortie scriptée (`.csx`) que le convertisseur HAR, pour la même raison — voir la
[décision structurante de la roadmap](ROADMAP.md). Une étape est générée par opération
(méthode + chemin) : les paramètres de chemin et les paramètres de requête **requis** sont
substitués par un placeholder dérivé du type du schéma (ou de l'`example` déclaré s'il y en a
un), le corps `application/json` est un exemple JSON construit récursivement à partir du schéma
(résolution des `$ref` locales vers `components/schemas`, avec garde anti-cycle pour un schéma
auto-référent).

Limites volontaires, comptées et documentées en tête du fichier généré plutôt que silencieuses :
- **Un seul type de contenu** : seul `application/json` est traduit en corps ; une opération dont
  le corps est `multipart/form-data` ou autre est générée sans corps, avec un commentaire dans le
  code plutôt qu'une étape manquante sans explication.
- **Aucun schéma d'authentification traduit** : comme pour le HAR, un jeton ou une clé d'API
  réelle ne peut venir que d'un humain, jamais de la spécification elle-même — les paramètres
  d'en-tête sont tout de même générés, avec un placeholder à remplacer.
- **Paramètres de requête optionnels omis** : seuls les paramètres requis sont ajoutés à l'URL,
  pour garder le squelette lisible plutôt que d'y jeter tous les paramètres facultatifs possibles.
- **YAML non pris en charge** dans cette première version — JSON seul, comme pour la plupart des
  exports d'outils (Swashbuckle, Swagger UI).

Vérifié par un vrai tir contre `Tempest.SampleTarget`, à partir d'une spécification décrivant
fidèlement ses trois routes réelles (`login`, `catalogue`, `checkout`, avec `$ref` vers des
schémas `components` pour les corps). Deux tirs, pour distinguer le squelette généré de son
usage réel :
- **Squelette non modifié** (`tempest run scenario.csx --target-url ... --rps 5 --duration 5s`) :
  `login` et `listProducts` à 0 % d'échec, `checkout` à 100 % d'échec — le placeholder
  `Authorization` n'est jamais un jeton valide, exactement la limite documentée plus haut.
- **Squelette complété à la main** (jeton lu dans la réponse de `login`, identifiant de produit lu
  dans la réponse de `listProducts`, exactement ce qu'un humain ajouterait) : les 3 étapes à 0 %
  d'échec.

## Convertisseur Postman

Troisième et dernier bullet de la phase 5 : `tools/Tempest.PostmanConvert` part d'une collection
Postman exportée (v2.1, le format courant de « Export » depuis l'application). Même nature de
sortie que le convertisseur OpenAPI — un **squelette**, pas un scénario directement jouable :
une collection décrit des requêtes qu'on a construites à la main dans Postman, pas un trafic
capturé avec de vraies données.

```bash
dotnet run --project tools/Tempest.PostmanConvert -- collection.json scenario.csx --name mon-scenario
```

Même sortie scriptée (`.csx`), pour la même raison — voir la [décision structurante de la
roadmap](ROADMAP.md). Les dossiers d'une collection (`item` imbriqués) sont parcourus
récursivement ; chaque requête feuille devient un step, nommé d'après le nom Postman qualifié par
ses dossiers parents (`Auth / Login`). Les variables **de collection** (`collection.variable`,
`{{nom}}`) sont substituées dans l'URL, les en-têtes et le corps — y compris quand elles résolvent
l'hôte lui-même (`{{baseUrl}}/api/x`) : une fois l'URL rendue absolue par la substitution, seul
`PathAndQuery` est conservé, l'hôte reste toujours celui de `--target-url` à l'exécution, quelle
que soit la valeur de `{{baseUrl}}` dans la collection.

Limites volontaires, comptées et documentées en tête du fichier généré :
- **Un environnement Postman séparé n'est pas lu** dans cette première version — seules les
  variables déclarées au niveau de la collection elle-même le sont. Une variable `{{...}}` sans
  valeur connue devient un placeholder générique (`valeur`), compté plutôt que silencieux.
- **Corps `formdata` non pris en charge** (comme le corps multipart du HAR et le
  `multipart/form-data` de l'OpenAPI) — seuls les modes `raw` et `urlencoded` sont traduits.
- **Aucun schéma d'authentification Postman traduit** (`auth` de la requête ou de la collection)
  — même raison que pour le HAR et l'OpenAPI : une vraie valeur ne peut venir que d'un humain.
- **Un placeholder substitué dans un corps JSON peut casser sa syntaxe** s'il apparaît sans
  guillemets — convention Postman courante pour injecter un nombre (`"productId":{{id}}`). Trouvé
  en vérifiant une vraie conversion (voir plus bas) : à corriger à la main, comme les autres
  placeholders, pas une régression du convertisseur — une variable Postman n'a pas de schéma pour
  deviner si elle attend une chaîne ou un nombre, contrairement à l'OpenAPI.

Vérifié par deux vrais tirs contre `Tempest.SampleTarget`, à partir d'une collection décrivant
fidèlement ses trois routes réelles, avec un dossier (`Auth / Login`) et une variable de
collection résolvant l'hôte (`{{baseUrl}}`) :
- **Squelette non modifié** : `Auth / Login` et `Catalogue` à 0 % d'échec, `Checkout` à 100 %
  d'échec — placeholder d'authentification et corps JSON invalide (`{{productId}}` non résolu et
  injecté sans guillemets), exactement les limites documentées plus haut.
- **Squelette complété à la main** (même principe que pour l'OpenAPI) : les 3 étapes à 0 % d'échec.

## Proxy enregistreur

Dernier bullet de la phase 5 : `tools/Tempest.RecorderProxy` capture du trafic HTTP réel en
direct, sans étape d'export manuel — à la différence du convertisseur HAR, qui suppose une
capture déjà faite (« Enregistrer tout en HAR » du navigateur). Scope volontairement réduit par
rapport au *recorder* de Gatling : un **reverse proxy à cible unique**, pas un proxy HTTP
générique multi-hôtes avec interception TLS (MITM) — cohérent avec le modèle `--target-url`
unique de `tempest run`, et ça évite tout le chantier certificat/confiance qu'un vrai proxy HTTPS
exigerait pour une fonctionnalité encore conditionnée à un vrai public.

```bash
dotnet run --project tools/Tempest.RecorderProxy -- --target-url http://localhost:5299 --out scenario.csx [--listen http://localhost:8888] [--name mon-scenario]
```

Pointez votre client (navigateur configuré avec cette adresse comme hôte, `curl`, l'application
elle-même) vers `--listen` au lieu de la cible réelle : chaque requête est retransmise fidèlement
vers `--target-url` — méthode, en-têtes, corps — et la réponse réelle relayée telle quelle,
pendant que la requête est enregistrée en arrière-plan. À l'arrêt (Ctrl+C, ou
`POST /__tempest-recorder/stop` pour un pilotage scripté), le proxy s'arrête proprement puis
génère le scénario **en réutilisant `HarConverter.Convert` tel quel** — la capture en direct
alimente exactement la même forme de données (`HarEntry`) qu'un export HAR de navigateur, donc le
filtrage des actifs statiques et la génération du `.csx` sont acquis gratuitement, sans code
dupliqué.

Limites volontaires, documentées :
- **HTTP seul dans cette première version** — pas d'interception TLS. Une cible HTTPS ne peut pas
  être enregistrée par ce proxy tel qu'il est aujourd'hui.
- **Seul un corps de type textuel reconnu est capturé** (JSON, XML, texte brut, HTML, JavaScript,
  formulaire, GraphQL, d'après `Content-Type`) ; un corps binaire (upload de fichier, image, ...)
  est retransmis fidèlement en direct mais jamais enregistré dans le scénario généré — même
  limite que le corps multipart du convertisseur HAR, pas une nouvelle.
- **Authentification/cookies capturés** : mêmes valeurs de session réelles que pour le HAR,
  potentiellement expirées au moment du tir — mais capturées et rejouées dans la foulée, sans le
  délai d'un export/conversion manuel, ce qui les rend souvent *plus* susceptibles d'être encore
  valides qu'un HAR exporté puis converti plus tard (vérifié ci-dessous).

Vérifié par un vrai tir de bout en bout contre `Tempest.SampleTarget` : proxy démarré, une vraie
session login/catalogue/checkout envoyée à travers lui (statuts 200 confirmés à travers le proxy,
identiques à ceux de la cible directe), arrêté via `/__tempest-recorder/stop`, scénario généré (4
requêtes enregistrées, 4 étapes retenues). Rejoué immédiatement via `tempest run` contre la même
cible : les 4 étapes à 0 % d'échec, y compris `checkout` — le jeton capturé était encore valide,
contrairement au HAR de la section précédente où l'export manuel avait laissé le temps au jeton
d'expirer. **Clôt entièrement la phase 5.**

## Contrat de plugin

Premier chantier de la phase 6 : k6 n'a pas porté seul ses dizaines de protocoles, il a ouvert
`xk6` et laissé la communauté le faire. Porter seul SQL, Kafka, MQTT, AMQP et le reste serait un
puits sans fond — le modèle d'extension doit exister **avant** les protocoles qu'il doit
accueillir, pas après.

Le contrat lui-même n'est pas nouveau : `IWorkflow`/`IVirtualUserContext`/`StepScope`
(`Tempest.Domain`) sont agnostiques du protocole depuis toujours — `DynamicCheckoutWorkflow` et
les scénarios scriptés en sont déjà la preuve. Ce qui manquait, c'est un moyen pour l'hôte de
**charger** un `IWorkflow` compilé dans une assembly indépendante de ce dépôt, sans
`ProjectReference` — `PluginWorkflowLoader` (`Tempest.Scenarios`) comble ce trou :

```bash
tempest run mon-plugin.dll --plugin-type MonNamespace.MonWorkflow --target-url http://localhost:5299 --rps 20 --duration 30s
```

`WorkflowFileLoader` reconnaît maintenant l'extension `.dll` en plus de `.yaml`/`.json`
(déclaratif) et `.csx`/`.cs` (scripté) : il charge l'assembly (`Assembly.LoadFrom`), résout le
type à instancier — `--plugin-type` s'il est renseigné (nom complet ou simple), sinon le seul
type public implémentant `IWorkflow` si l'assembly n'en expose qu'un — puis l'instancie via son
constructeur public sans paramètre. Même flag disponible par scénario en mode scénarios
concurrents (`ScenarioOptions.PluginWorkflowType`, section `Tempest:Scenarios`).

Limites volontaires de cette première version :
- **Aucune configuration injectée** dans le type instancié — pas de section `appsettings.json`
  liée automatiquement, contrairement à `DynamicCheckoutWorkflowOptions` pour les scénarios
  intégrés. Un plugin gère son propre réglage (variable d'environnement, fichier dédié...), voir
  `samples/Tempest.SamplePlugin`.
- **Pas de résolution NuGet** : le chemin donné à `tempest run`/`--scenario-file` doit déjà
  exister sur le disque (assembly compilée localement ou publiée puis téléchargée à la main).
  Résoudre un plugin par identifiant de paquet reste un chantier séparé, plus grand (roadmap
  phase 6, bullet suivant).
- **Mode distribué non pris en charge**, même limite que pour un scénario scripté :
  `WorkerCoordinator` ne sait construire qu'un `DeclarativeWorkflow` à partir du contenu propagé
  aux workers.

`samples/Tempest.SamplePlugin` est la preuve réelle du contrat, pas un exemple théorique : un
projet de bibliothèque .NET ordinaire, **jamais référencé par `Tempest.Host`/`Tempest.Cli`/
`Tempest.Scenarios`**, dont la seule dépendance est `Tempest.Domain` (`ProjectReference` ici
uniquement parce que le dépôt reste privé — un vrai plugin tiers utiliserait le paquet NuGet). Il
implémente un `IWorkflow` minimal qui appelle `IVirtualUserContext.HttpClient` exactement comme
`DynamicCheckoutWorkflow`. Vérifié par deux vrais tirs contre `Tempest.SampleTarget`, l'assembly
compilée séparément puis chargée par son chemin : sélection automatique du seul type disponible,
puis sélection explicite via `--plugin-type` — les deux à 0 % d'échec.

### Résolution NuGet

Deuxième moitié du bullet « chargement dynamique/résolution NuGet » de la phase 6 : plutôt que
d'exiger un chemin de `.dll` déjà présent sur le disque, `--plugin-package` résout un plugin par
identifiant de paquet, comme n'importe quelle dépendance NuGet ordinaire :

```bash
tempest run --plugin-package MonEntreprise.TempestPlugins.Sql --plugin-package-version 1.4.0 --plugin-source https://mon-flux-prive/index.json --target-url http://localhost:5299 --rps 20 --duration 30s
```

`NuGetPluginResolver` (`Tempest.Scenarios`, client officiel `NuGet.Protocol`) interroge
`--plugin-source` (répétable, nuget.org seul si omis) dans l'ordre — la première source qui
connaît le paquet gagne —, télécharge le `.nupkg` dans un cache local persistant entre les tirs,
puis extrait la bibliothèque du groupe `lib/<tfm>` le plus proche de `net10.0` (`FrameworkReducer`,
le même algorithme que NuGet/MSBuild eux-mêmes) avant de la remettre à `PluginWorkflowLoader`,
exactement comme pour une `.dll` locale. Sans `--plugin-package-version`, la dernière version
stable est résolue — mais **une version explicite déjà en cache ne redéclenche aucun trafic
réseau**, une version publiée étant immuable, contrairement à « dernière version stable » qui doit
toujours revalider auprès de la source.

Limite assumée : aucune résolution de dépendances transitives du paquet — seule sa propre
bibliothèque est extraite. Un plugin qui dépend d'un paquet tiers au-delà de `Tempest.Domain` doit
être publié en assembly fusionnée, ou accepter que le chargement de type échoue si une référence ne
se résout pas.

Vérifié par un vrai tir : `Tempest.SamplePlugin` empaqueté via `dotnet pack` dans un dossier local
(un flux NuGet à part entière, celui d'un miroir d'entreprise hors ligne — pas une approximation de
nuget.org), résolu par `--plugin-package Tempest.SamplePlugin --plugin-source <dossier>` contre
`Tempest.SampleTarget` réellement démarré : 0 % d'échec, confirmé une deuxième fois avec
`--plugin-package-version` explicite pour vérifier le chemin de cache.

## Protocoles de référence

Troisième bullet de la phase 6 : des extensions écrites contre le contrat de plugin, pour le
valider en conditions réelles plutôt que dans l'abstrait. `Tempest.SamplePlugin` prouvait le
mécanisme de chargement ; ces extensions prouvent qu'un protocole *différent* de HTTP tient dans
le même contrat, sans rien changer au cœur.

### SQL

`extensions/Tempest.Extensions.Sql` interroge une vraie base SQLite plutôt que
`IVirtualUserContext.HttpClient` — la roadmap avait explicitement écarté SQL des [jeux de
données](#jeux-de-données) (phase 2) faute de scope pour un chantier séparé ; celui-ci le referme,
sous un angle différent (protocole de charge, pas source de paramètres).

```bash
dotnet publish extensions/Tempest.Extensions.Sql -o publish/sql-plugin
TEMPEST_SQL_PLUGIN_CONNECTION_STRING="Data Source=/chemin/vers/ma-base.db" tempest run publish/sql-plugin/Tempest.Extensions.Sql.dll --target-url http://localhost:1 --rps 20 --duration 30s
```

SQLite plutôt qu'un serveur SQL (PostgreSQL, SQL Server...) : base embarquée, aucune
infrastructure supplémentaire à démarrer pour vérifier ce chantier de bout en bout — cohérent avec
la discipline de scope du reste du projet. `SetUpAsync` sème un nombre configurable de produits de
référence (graine fixe, comme `DynamicCheckoutWorkflow`) et active le mode WAL, indispensable dès
que plusieurs utilisateurs virtuels partagent le même fichier en même temps. Chaque itération
exécute deux étapes réelles : un `SELECT` paramétré et un `INSERT`, chacune chronométrée par
`StepScope` comme n'importe quelle étape HTTP.

**`--target-url` reste exigé par la CLI mais n'est d'aucun usage pour ce protocole** — même
convention que `grpc-echo`/`websocket-echo`, qui dérivent leur propre cible d'une configuration
séparée plutôt que du client HTTP partagé. La configuration du plugin (chemin de la base, nombre de
lignes de référence) passe par des variables d'environnement, pas par `appsettings.json` :
`PluginWorkflowLoader` n'injecte aucune configuration dans le type qu'il instancie, voir
[Contrat de plugin](#contrat-de-plugin).

**Trouvaille réelle, documentée plutôt que corrigée dans le cœur** : un plugin chargé par
`Assembly.LoadFrom` doit être **publié** (`dotnet publish`), pas seulement compilé —
`dotnet build` seul ne copie pas les dépendances NuGet transitives à côté de l'assembly, que
`PluginWorkflowLoader` ne résout alors plus (`Microsoft.Data.Sqlite` introuvable au chargement).
Publier suffit pour une dépendance gérée, mais pas pour sa bibliothèque **native** : `SQLitePCLRaw`
la cherche par défaut à côté de l'assembly *hôte* (`Tempest.Cli`, qui ne l'a jamais référencée), pas
à côté du plugin. `SqlWorkflow` enregistre son propre `NativeLibrary.SetDllImportResolver` pour la
chercher à côté de lui-même — la solution est dans le plugin, pas dans le contrat : un protocole
tiers reste responsable de ses propres dépendances natives, exactement l'esprit de la phase 6
(« s'ajoute sans toucher au cœur »).

Vérifié par un vrai tir : plugin publié, exécuté contre une base SQLite fraîche — 30 itérations,
0 % d'échec sur les deux étapes (`SELECT`/`INSERT`), lignes effectivement persistées (vérifié aussi
par des tests unitaires qui interrogent directement le fichier après coup).

### SSE

`extensions/Tempest.Extensions.Sse` valide le contrat sous un angle différent de SQL : plutôt
qu'un protocole *différent* de HTTP, un **usage** différent d'`IVirtualUserContext.HttpClient` — une
réponse en flux continu (`text/event-stream`) lue événement par événement au fil de l'eau, plutôt
que l'aller-retour requête/réponse unique de tout le reste du dépôt.

```bash
dotnet build extensions/Tempest.Extensions.Sse
tempest run extensions/Tempest.Extensions.Sse/bin/Debug/net10.0/Tempest.Extensions.Sse.dll --target-url http://localhost:5281 --rps 20 --duration 30s
```

Contrairement au plugin SQL, celui-ci **utilise** `--target-url` normalement : le client HTTP
partagé pointe déjà vers la cible, seul le chemin relatif (`TEMPEST_SSE_PLUGIN_PATH`, défaut
`/api/events/stream`) et le nombre d'événements attendu (`TEMPEST_SSE_PLUGIN_EVENT_COUNT`, défaut
20) se configurent par variable d'environnement, comme `Tempest.SamplePlugin`. Chaque itération
exécute deux étapes réelles : `SSE connect` (ouverture de la réponse en tête seule via
`HttpCompletionOption.ResponseHeadersRead`, vérification du `Content-Type`) et `SSE receive events`
(lecture ligne à ligne jusqu'à la fin du flux, comptage des événements portant au moins une ligne
`data:`). Un flux qui ne se termine jamais est borné par un délai par itération
(`TEMPEST_SSE_PLUGIN_TIMEOUT_SECONDS`, défaut 10s) plutôt que de bloquer indéfiniment l'utilisateur
virtuel.

Conséquence directe de rester au-dessus de HTTP plutôt que d'un protocole distinct : contrairement
à SQL, cette extension ne dépend d'aucun paquet NuGet au-delà de `Tempest.Domain` — `HttpClient` et
la lecture de flux viennent du BCL. Un simple `dotnet build` suffit, `dotnet publish` n'est pas
nécessaire ici, confirmant que l'exigence de publication découverte pour SQL tient à une dépendance
réellement externe, pas au contrat de plugin lui-même.

Vérifié par deux vrais tirs contre un nouvel point d'écoute de `Tempest.SampleTarget`
(`GET /api/events/stream`, nombre d'événements piloté par la requête) : sélection par défaut sans
aucune variable d'environnement, puis avec `--plugin-type` explicite et un nombre d'événements/délai
personnalisés — les deux à 0 % d'échec sur `SSE connect` et `SSE receive events`.

### MQTT

`extensions/Tempest.Extensions.Mqtt` revient à un protocole réellement différent de HTTP, comme
SQL, mais orienté publication/abonnement plutôt que requête/réponse : chaque itération s'abonne à
un sujet qui lui est propre (`{préfixe}/{utilisateur}/{itération}`), y publie un message, puis
attend sa propre réception — le round-trip complet jusqu'au courtier et retour, pas un simple
accusé de publication.

```bash
dotnet publish extensions/Tempest.Extensions.Mqtt -o publish/mqtt-plugin
TEMPEST_MQTT_PLUGIN_PORT=1883 tempest run publish/mqtt-plugin/Tempest.Extensions.Mqtt.dll --target-url http://localhost:1 --rps 20 --duration 30s
```

Sujet propre à chaque itération plutôt que partagé : sans cela, un utilisateur virtuel pourrait
recevoir le message publié par un autre, rendant le round-trip mesuré non attribuable à la bonne
itération. Deux étapes réelles par itération : `MQTT connect` (ouverture de la connexion TCP et
poignée de main MQTT) et `MQTT publish/receive` (abonnement, publication, attente bornée par
`TEMPEST_MQTT_PLUGIN_TIMEOUT_SECONDS` de la réception du même message).

Client MQTTnet uniquement (`MQTTnet`, pas `MQTTnet.Server`) : aucun courtier n'est porté par ce
plugin, contrairement à `Tempest.SampleTarget` qui en héberge un embarqué (`MQTTnet.Server`, sur un
port dédié distinct du port MQTT conventionnel 1883) pour que la vérification de bout en bout ne
dépende d'aucune infrastructure externe — même logique que SQLite pour le protocole de référence
SQL, transposée à un courtier plutôt qu'un serveur de base de données.

**Confirmation réelle plutôt que nouvelle trouvaille** : comme le plugin SQL, celui-ci doit être
**publié** (`dotnet publish`), pas seulement compilé — vrai même ici où la seule dépendance ajoutée
(`MQTTnet`) est entièrement gérée, sans composant natif. Un `dotnet build` seul charge le type sans
erreur (`PluginWorkflowLoader` résout la réflexion sans toucher à MQTTnet), mais `ExecuteAsync`
échoue dès le premier accès à un type MQTTnet, faute de trouver `MQTTnet.dll` à côté de l'assembly.
Confirmé en isolant le problème via un harnais direct (mêmes appels, sans passer par
`Assembly.LoadFrom`) avant de le reproduire puis de le corriger par publication — la limite tient
bien au chargement dynamique d'un plugin avec dépendance externe, pas à un aléa propre à SQLite.

Vérifié par deux vrais tirs contre le courtier MQTT embarqué de `Tempest.SampleTarget` : sélection
par défaut, puis `--plugin-type` explicite avec un préfixe de sujet personnalisé — les deux à 0 %
d'échec sur `MQTT connect` et `MQTT publish/receive`.

### GraphQL

`extensions/Tempest.Extensions.GraphQl` clôt les quatre protocoles de référence de la phase 6.
Comme SSE, il reste au-dessus de HTTP plutôt que d'en changer, mais valide un autre aspect du
contrat : un point d'entrée unique (toujours `POST {chemin}`, toujours le même) où le succès ou
l'échec se lit dans le corps JSON (champ `errors`), jamais dans le code de statut — qui reste 200
même quand une mutation échoue côté métier. Toute autre étape HTTP du dépôt
(`DynamicCheckoutWorkflow`, `Tempest.SamplePlugin`...) utilise au contraire le code de statut comme
seul signal.

```bash
dotnet build extensions/Tempest.Extensions.GraphQl
tempest run extensions/Tempest.Extensions.GraphQl/bin/Debug/net10.0/Tempest.Extensions.GraphQl.dll --target-url http://localhost:5281 --rps 20 --duration 30s
```

Deux étapes réelles par itération, mêmes deux natures d'opération que SQL sous revêtement HTTP :
`GraphQL query` (liste le catalogue, vérifie qu'elle n'est jamais vide) et `GraphQL mutation`
(passe une commande pour un identifiant tiré dans `[1, TEMPEST_GRAPHQL_PLUGIN_PRODUCT_ID_MAX]`,
20 par défaut). Ni variables GraphQL ni alias : les valeurs sont inlinées dans la chaîne de requête,
la cible de référence n'en a pas besoin pour prouver le contrat.

`Tempest.SampleTarget` héberge un schéma GraphQL réel (`GraphQL`, moteur GraphQL.NET — pas une
simulation par correspondance de chaîne) exposé à la main sur `POST /graphql`, dans le même esprit
que REST/WebSocket : `products` en lecture, `placeOrder` en écriture, qui échoue avec une entrée
`errors` plutôt qu'un code de statut différent de 200 pour un identifiant de produit inconnu —
exactement le comportement que ce protocole de référence existe pour vérifier. Aucune dépendance
NuGet au-delà de `Tempest.Domain` côté plugin (`System.Text.Json` et `HttpClient` suffisent) : comme
SSE, un simple `dotnet build` suffit, sans avoir besoin de publier.

Vérifié par deux vrais tirs contre le vrai schéma GraphQL de `Tempest.SampleTarget` : sélection par
défaut, puis `--plugin-type` explicite avec une plage d'identifiants réduite — les deux à 0 %
d'échec sur `GraphQL query` et `GraphQL mutation`.

## Guide d'écriture d'extension

Dernier bullet de la phase 6 : sans documentation, un modèle de plugin reste théorique. Les quatre
protocoles de référence ci-dessus (SQL, SSE, MQTT, GraphQL) et `samples/Tempest.SamplePlugin` sont
déjà des preuves réelles du contrat — ce guide en extrait la recette pour écrire la cinquième
extension, pas encore écrite par ce dépôt.

### Quand une extension plutôt qu'un scénario scripté

Un scénario [scripté](#scénarios-scriptés-roslyn) (`.csx`/`.cs`) ou [déclaratif](#configuration-déclarative)
(`.yaml`/`.json`) suffit tant que le trafic reste HTTP à travers `IVirtualUserContext.HttpClient` —
c'est le cas le plus courant, et il ne demande aucune compilation séparée. Une extension devient
nécessaire dans deux cas, pas plus : le protocole n'est **pas** HTTP (SQL, MQTT — une bibliothèque
cliente tierce remplace le client HTTP partagé), ou le scénario doit être distribué comme un
artefact compilé indépendant de ce dépôt (un paquet NuGet interne, par exemple), plutôt que comme du
code source lisible. SSE et GraphQL restent au-dessus de HTTP : ils existent comme extensions
uniquement pour prouver qu'un *usage* différent du client partagé tient aussi dans le contrat, pas
parce que HTTP y était impossible autrement.

### Le contrat minimal

Tout tient dans `Tempest.Domain.Execution.IWorkflow` — trois membres obligatoires, quatre membres
par défaut (C# 8+, à ne surcharger que si le besoin existe réellement) :

```csharp
public interface IWorkflow
{
    string Name { get; }

    IReadOnlyDictionary<string, string> Tags => new Dictionary<string, string>();

    void RegisterSteps(StepRegistry registry);

    void RegisterMetrics(CustomMetricRegistry registry) { }

    ValueTask SetUpAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken);

    ValueTask TearDownAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
```

L'ordre d'appel ne varie jamais, et chaque phase a sa propre discipline :

1. **`RegisterSteps`** — une seule fois, à froid, avant le premier utilisateur virtuel. C'est le
   **seul** endroit où un `StepId` peut être obtenu (`registry.Register("nom de l'étape")`) ; le
   registre est scellé (`Seal()`) juste après par le moteur, et toute tentative d'enregistrement
   tardif lève `InvalidOperationException`. Le nom d'étape apparaît tel quel dans le rapport.
2. **`RegisterMetrics`** — une seule fois, juste après, pour une [métrique personnalisée](#métriques-personnalisées)
   éventuelle. À ignorer si l'extension n'en publie aucune.
3. **`SetUpAsync`** — une seule fois, à froid : semer des données de référence, ouvrir une
   connexion partagée. Jamais chronométré, jamais compté dans le rapport.
4. **`ExecuteAsync`** — à **chaque itération**, le chemin chaud. La discipline du dépôt entier
   s'applique ici sans exception : pas de LINQ, pas de fermeture capturante, pas de concaténation
   de chaînes, pas d'allocation évitable — ce code tourne potentiellement des milliers de fois par
   seconde.
5. **`TearDownAsync`** — une seule fois, à la fin du tir.

`IVirtualUserContext` (reçu en paramètre d'`ExecuteAsync`, jamais construit par l'extension) expose
`HttpClient` (déjà pointé sur `--target-url`), `VirtualUserId`/`IterationNumber`, `State` (un slot
libre pour une donnée par utilisateur virtuel, un jeton d'authentification par exemple) et surtout
`BeginStep(StepId) : StepScope` — l'unique façon d'obtenir un `StepScope`.

`StepScope` est une `struct`, **délibérément pas `IDisposable`** : un `using` oublié ne doit jamais
faire passer une requête pour un succès silencieux. Chaque chemin de code doit appeler exactement
une des méthodes suivantes avant de sortir de la méthode :

| Méthode | Quand |
|---|---|
| `scope.Success(statusCode?, bytesReceived?)` | Succès, hors HTTP (SQL, MQTT...) |
| `scope.CompleteHttp(statusCode, bytesReceived?)` | Réponse HTTP reçue — classe automatiquement 2xx en `Success`, le reste (y compris 3xx) en `HttpError` |
| `scope.Fail(RequestOutcome, statusCode?, bytesReceived?)` | Échec explicite : `AssertionFailed` (une vérification métier ne passe pas — contenu inattendu, `errors` GraphQL...), `ConnectionError`, `Timeout` |

### Étape par étape : premier plugin

```bash
dotnet new classlib -n MonPlugin -o MonPlugin
```

Le `.csproj` n'a besoin que d'une référence à `Tempest.Domain` (`ProjectReference` tant que ce dépôt
reste privé ; paquet NuGet `Tempest.Domain` pour une extension tierce réelle une fois le dépôt
public) :

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Tempest.Domain\Tempest.Domain.csproj" />
  </ItemGroup>
</Project>
```

Puis le workflow lui-même — la forme la plus simple possible, une étape HTTP unique à travers le
client partagé (exactement `samples/Tempest.SamplePlugin`, à quelques noms près) :

```csharp
using Tempest.Domain.Execution;
using Tempest.Domain.Metrics;

namespace MonPlugin;

public sealed class MonWorkflow : IWorkflow
{
    private StepId _browseStep;

    public string Name => "mon-plugin";

    public void RegisterSteps(StepRegistry registry) =>
        _browseStep = registry.Register("GET /api/catalog/products (mon plugin)");

    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(_browseStep);
        HttpResponseMessage response;
        try
        {
            response = await context.HttpClient.GetAsync("/api/catalog/products", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            scope.Fail(RequestOutcome.ConnectionError);
            return;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scope.Fail(RequestOutcome.Timeout);
            return;
        }

        using (response)
        {
            scope.CompleteHttp((int)response.StatusCode, response.Content.Headers.ContentLength.GetValueOrDefault());
        }
    }
}
```

Le couple `catch (HttpRequestException)`/`catch (TaskCanceledException) when
(!cancellationToken.IsCancellationRequested)` revient dans les quatre protocoles de référence :
la garde sur le second `catch` est ce qui distingue un **timeout de l'étape elle-même** d'une
**annulation globale du tir** (Ctrl+C, fin de la durée configurée) — sans elle, l'arrêt normal d'un
tir se rapporterait comme un timeout sur la dernière itération de chaque utilisateur virtuel.

Une extension gère sa propre configuration par variable d'environnement (chemin, identifiants,
délais...) — `PluginWorkflowLoader` n'injecte rien dans le type qu'il instancie, voir [Contrat de
plugin](#contrat-de-plugin). Convention reprise des quatre protocoles de référence :

```csharp
private readonly string _path = Environment.GetEnvironmentVariable("MON_PLUGIN_PATH") is { Length: > 0 } configured
    ? configured
    : "/api/catalog/products";
```

### Compiler et charger

```bash
dotnet build MonPlugin
tempest run MonPlugin/bin/Debug/net10.0/MonPlugin.dll --plugin-type MonPlugin.MonWorkflow --target-url http://localhost:5281 --rps 20 --duration 30s
```

`--plugin-type` est **optionnel** si l'assembly n'expose qu'un seul type public implémentant
`IWorkflow` — sinon `PluginWorkflowLoader` refuse de deviner et liste les candidats trouvés. Le
type résolu doit avoir un **constructeur public sans paramètre** ; sans ça, le chargement échoue
avec un message explicite plutôt qu'une exception de réflexion brute.

Un simple `dotnet build` suffit **uniquement si la seule dépendance est `Tempest.Domain`** — le cas
de SSE et GraphQL ci-dessus, et de l'exemple plus haut. Dès qu'une dépendance NuGet supplémentaire
entre en jeu (le cas de SQL et MQTT), il faut **publier** (`dotnet publish`) : `Assembly.LoadFrom`
charge l'assembly du plugin depuis son propre dossier, et `dotnet build` seul ne copie pas les
dépendances NuGet transitives à côté d'elle — seul `dotnet publish` le fait. Une dépendance
**native** (comme `SQLitePCLRaw` pour SQL) va plus loin : elle doit en plus être cherchée par le
plugin lui-même via `NativeLibrary.SetDllImportResolver`, le résolveur par défaut cherchant à côté
de l'hôte (`Tempest.Cli`) plutôt qu'à côté du plugin — voir `SqlWorkflow` pour le patron exact.

### Distribuer via NuGet

Une fois le plugin validé en local, `dotnet pack` puis [résolution NuGet](#résolution-nuget) évite
d'avoir à distribuer un chemin de fichier :

```bash
dotnet pack MonPlugin -o ./local-feed
tempest run --plugin-package MonPlugin --plugin-source ./local-feed --target-url http://localhost:5281 --rps 20 --duration 30s
```

Limite assumée : **aucune dépendance transitive du paquet n'est résolue**, seule la bibliothèque du
plugin lui-même est extraite. Une extension qui dépend d'autre chose que `Tempest.Domain` doit
publier une assembly qui embarque déjà ses dépendances, ou accepter que le chargement échoue.

### Tester une extension

Discipline du dépôt entier, sans exception pour les extensions : contre un **vrai** double
(serveur Kestrel in-process, broker embarqué...), jamais un mock qui court-circuite le protocole
réellement testé. `tests/Tempest.UnitTests/TestDoubles/SseTestServer.cs`,
`MqttTestBroker.cs` et `GraphQlTestServer.cs` sont les patrons à réutiliser selon la forme du
protocole (HTTP in-process, broker TCP local, serveur GraphQL in-process).

Si `ExecuteAsync` échoue silencieusement une fois chargée par `tempest run` (`VirtualUserWorker`
avale toute exception de scénario sans jamais la journaliser, y compris avec
`Logging__LogLevel__Default=Debug`), le diagnostic le plus rapide reste une petite application
console jetable avec une **`ProjectReference`** vers l'extension (pas `Assembly.LoadFrom`), qui
appelle `workflow.ExecuteAsync(context, ct)` directement dans un `try`/`catch` — cela isole en une
minute si le bug est dans la logique du workflow ou dans le chemin de chargement du plugin.

### Limites actuelles

- **Aucune configuration injectée** dans le type instancié — variable d'environnement ou fichier
  dédié, jamais de section `appsettings.json` liée automatiquement.
- **Aucune résolution de dépendances transitives** pour un plugin résolu par paquet NuGet.
- **Mode distribué non pris en charge** — comme pour un scénario scripté, `WorkerCoordinator` ne
  sait construire qu'un `DeclarativeWorkflow` à partir du contenu propagé aux workers.
- **Dépendances natives** : à la charge du plugin lui-même (`NativeLibrary.SetDllImportResolver`),
  jamais du contrat.

### Les quatre protocoles de référence, comme exemples travaillés

| Protocole | Facette validée |
|---|---|
| [SQL](#sql) | Un protocole réellement différent de HTTP, avec une dépendance native à résoudre soi-même |
| [SSE](#sse) | Un usage différent du client HTTP partagé (flux continu), zéro dépendance NuGet supplémentaire |
| [MQTT](#mqtt) | Un protocole différent orienté publication/abonnement, dépendance managée sans composant natif |
| [GraphQL](#graphql) | Un autre usage HTTP où succès/échec se lit dans le corps de la réponse, pas dans le code de statut |

Vérifié en suivant ce guide à la lettre, depuis un dossier vide : un plugin minimal
(`dotnet new classlib`, la même forme que l'exemple ci-dessus) construit par un simple `dotnet build`
puis chargé par `tempest run` contre `Tempest.SampleTarget` réellement démarré — sélection
automatique du seul type disponible, puis `--plugin-type` explicite — les deux à 0 % d'échec.
Dossier jetable, jamais commité.

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

### Groupes et étiquettes

Troisième bullet de la [roadmap phase 2](ROADMAP.md#phase-2--des-scénarios-quon-peut-réellement-écrire) :
une hiérarchie d'étapes (`endpoint`) et des métadonnées de tir (`région`, `version`) dans le
rapport. Les deux couvrent des besoins distincts et n'ont volontairement pas la même portée.

**Groupe** — une étape peut porter un `group`, préfixé à son nom pour former le nom
effectivement enregistré (`QualifiedName`) :

```yaml
name: checkout-flow
steps:
  - name: login
    group: checkout
    method: POST
    path: /api/auth/login
  - name: pay
    group: checkout
    method: POST
    path: /api/checkout
```

Ce scénario produit deux lignes `checkout/login` et `checkout/pay` dans le rapport — la même
`StepId`/`StepScope` que n'importe quelle étape, donc les mêmes `/metrics` Prometheus et les
mêmes seuils via `--threshold`, sans aucun changement dans `Tempest.Application`/
`Tempest.Infrastructure`. Deux étapes de même nom dans deux groupes différents (`checkout/pay`,
`refund/pay`) restent deux lignes distinctes : la collision est vérifiée sur le nom qualifié, pas
sur le nom seul.

Le rapport affiche ce nom qualifié tel quel, sans tenter d'en déduire une arborescence visuelle
(indentation, sous-total par groupe) : un nom d'étape reste une chaîne libre, et interpréter un
`/` comme séparateur de groupe à l'affichage romprait pour toute étape dont le nom en contient un
sans intention de groupe — un cas réel, pas hypothétique, rencontré pendant le développement de
cette fonctionnalité (le test d'échappement HTML utilise justement un nom malicieux contenant
`</script>`, lui-même porteur d'un `/`). Le regroupement reste donc une convention de nommage
visible dans la colonne existante, pas une syntaxe interprétée.

**Étiquettes** — une métadonnée de tir dans son ensemble, pas d'une étape précise, portée par
`ScenarioDefinition.Tags` :

```yaml
name: checkout-flow
tags:
  region: eu-west
  version: v2
steps:
  - name: login
    method: POST
    path: /api/auth/login
```

Reportées telles quelles dans l'en-tête du rapport (texte et HTML), jamais dans l'agrégation des
métriques : une étiquette classe un rapport, elle ne découpe pas ses lignes. C'est une différence
assumée avec un système de tags par requête façon k6, qui exigerait de faire de la valeur d'une
étiquette une clé d'agrégation à part entière — un changement bien plus profond (jusque dans
`StepAccumulator`/`MetricResult`, aujourd'hui une structure non managée volontairement sans
référence, voir son commentaire) pour un besoin réel mais différent : classer deux tirs du même
scénario contre deux cibles, pas ventiler un seul tir par région choisie par requête.

Scénario **scripté** : `IWorkflow.Tags` est une propriété d'interface à défaut vide — un script
qui en a besoin la surcharge directement dans sa classe, sans mécanisme supplémentaire.

Limite assumée : en mode distribué (maître/workers), les étiquettes ne sont pas encore
propagées jusqu'au rapport fusionné — seul le mode autonome (`tempest run` sans rôle) les affiche
aujourd'hui. À traiter si le besoin se présente.

Vérifié par un vrai tir contre `Tempest.SampleTarget` : un scénario avec `login`/`pay` groupés
sous `checkout` et une étape `browse` sans groupe affiche `checkout/login`, `checkout/pay` et
`browse` dans la même table, et l'en-tête du rapport (texte et HTML) affiche
`etiquettes : region=eu-west, version=v2`.

### Métriques personnalisées

Dernier bullet réellement nouveau de la [roadmap phase 2](ROADMAP.md#phase-2--des-scénarios-quon-peut-réellement-écrire) :
un compteur, une jauge, un taux ou une tendance métier, alimentés depuis une réponse de scénario
et agrégés comme les métriques natives — même vocabulaire que les `Counter`/`Gauge`/`Rate`/`Trend`
de k6. Contrairement aux checks et aux groupes/étiquettes, cette fonctionnalité ne pouvait pas se
contenter de réutiliser `StepId`/`StepAccumulator` tels quels : une métrique personnalisée porte
une valeur métier arbitraire (un montant, une taille de panier), pas une durée de requête, donc
une seconde chaîne d'agrégation parallèle était réellement nécessaire — canal borné dédié
(`ChannelCustomMetricSink`), accumulateur (`CustomMetricAccumulator`) et agrégateur
(`CustomMetricsAggregator`), sur le même principe qu'un seul consommateur en arrière-plan que la
chaîne native.

```yaml
steps:
  - name: checkout
    method: POST
    path: /api/checkout
    metrics:
      - name: orders_total
        kind: counter
      - name: order_value
        kind: trend
        jsonPath: $.total
      - name: active_carts
        kind: gauge
        jsonPath: $.cartSize
      - name: checkout_success_rate
        kind: rate
        jsonPath: $.orderId
```

Même vocabulaire d'expression que la [corrélation dynamique](#corrélation-dynamique-regexxpathjsonpath)
et les [checks](#checks) — `regex`, `xpath` ou `jsonPath` — avec une exception : un `counter` sans
expression compte simplement les passages sur l'étape (valeur implicite 1 à chaque exécution), le
cas le plus courant ne devrait pas exiger d'extraire quoi que ce soit d'une réponse. `gauge` et
`trend` exigent une expression numérique. `rate` évalue une condition comme un check (trouvé, ou
identique à `expected` si fourni) et enregistre 1 ou 0. Une expression manquée ou non numérique
n'enregistre simplement rien cette fois-ci — ce n'est jamais un échec de la requête HTTP dont la
métrique dérive, exactement comme un check.

Le nom d'une métrique vit dans son propre espace, distinct des étapes et des checks : une même
métrique peut légitimement apparaître dans plusieurs étapes (un compteur métier alimenté à deux
endroits différents), à condition de garder le même type partout où elle apparaît — un type
incohérent est rejeté au chargement.

Rendue dans le rapport (texte et HTML) sous une section dédiée, et dans Prometheus sous quatre
instruments (`tempest.custom.counter`, `.gauge`, `.rate`, `.trend`), étiquetés par `metric` (et
par `stat` pour la tendance : `min`/`mean`/`max`). Limites assumées pour ce premier tour, dans le
même esprit que les précédentes : pas de centiles pour la tendance (`LatencyHistogram` est bâti
pour une durée non négative bornée, pas pour une valeur métier de plage arbitraire — voir son
commentaire), pas de fenêtre glissante (une seule photographie cumulée), et pas de fusion
inter-workers en mode distribué.

Scénario **scripté** : aucun changement nécessaire — `CustomMetricRegistry`/`CustomMetricId` sont
déjà dans les imports par défaut (`Tempest.Domain.Metrics`), et `IWorkflow.RegisterMetrics` a un
défaut vide qu'un script surcharge s'il en a besoin, exactement comme il enregistre déjà ses
propres étapes.

Vérifié par un vrai tir contre `Tempest.SampleTarget` : `orders_total` (compteur) au nombre exact
d'itérations, `order_value`/`active_carts` reflétant le montant réel de la commande retournée par
la cible, `checkout_success_rate` à 100 % — confirmés à la fois dans le rapport texte et dans
`/metrics`.

### Temps de réflexion et rythme

Dernier bullet de la [roadmap phase 2](ROADMAP.md#phase-2--des-scénarios-quon-peut-réellement-écrire) :
une pause après une étape, avant la suivante — le `sleep()` de k6 ou le `pause()` de Gatling, sans
lequel un parcours utilisateur simulé enchaîne ses requêtes plus vite qu'aucun humain ne le ferait
jamais.

```yaml
steps:
  - name: browse
    method: GET
    path: /api/catalog/products
    thinkTime: 1s          # pause fixe

  - name: checkout
    method: POST
    path: /api/checkout
    thinkTime: 500ms       # borne basse d'une pause aleatoire...
    thinkTimeMax: 3s       # ...et sa borne haute, tiree uniformement a chaque iteration
```

`thinkTime` seul fixe une durée exacte ; ajouter `thinkTimeMax` en fait une plage, tirée uniformément
à chaque itération (`ThinkTimeDefinition.Sample`) — un parcours réel ne s'arrête jamais identique
deux fois de suite. Les deux acceptent le même format que `--duration` en ligne de commande
(`500ms`, `1s`, `2m`, `1h`, ou un nombre nu interprété en secondes).

La pause n'est **jamais** mesurée comme latence de requête : elle a lieu après que l'étape a
publié sa propre mesure (`scope.Complete()`), donc en dehors de tout ce que `LoadTestReport`
rapporte pour cette étape. Aucun changement dans le moteur n'était nécessaire — un utilisateur
virtuel qui dort dans `Task.Delay` ne fait que retarder le prochain jeton qu'il prendra dans le
canal, exactement comme le ferait une réponse HTTP lente : le modèle ouvert de
`TargetRpsLoadEngine` absorbe cela nativement en dette d'ordonnancement si le débit cible dépasse
ce que les utilisateurs virtuels configurés peuvent tenir compte tenu de leurs pauses, sans jamais
ralentir le rythme d'émission des jetons eux-mêmes.

Scénario **scripté** : sans effet et sans besoin d'API dédiée — une pause s'écrit directement via
`await Task.Delay(...)` dans le script, ce que Roslyn permettait déjà avant ce chantier.

Vérifié par de vrais tirs contre `Tempest.SampleTarget` : avec un seul utilisateur virtuel, une
pause fixe de 500 ms et un débit cible de 20 req/s (irréaliste pour un seul utilisateur virtuel
avec cette pause), le débit effectif tombe à ~2 itérations/s — exactement `1 / (pause + latence)`
— et la dette d'ordonnancement grimpe en conséquence, pendant que la latence brute de l'étape HTTP
elle-même reste inchangée (~100 ms de p99), confirmant que la pause n'est jamais comptée dans la
mesure de la requête. Le même scénario sans `thinkTime` tient les 20 req/s cible avec une dette
négligeable. Une pause en plage (100–300 ms, 4 utilisateurs virtuels) montre un p50/p95
d'itération cohérent avec la plage configurée, sans affecter la latence brute rapportée pour
l'étape HTTP.

Avec ce chantier, le contenu de la [roadmap phase 2](ROADMAP.md#phase-2--des-scénarios-quon-peut-réellement-écrire)
est entièrement traité.

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

### Reprise sur perte d'un worker

Jusqu'ici, l'enregistrement (`POST /master/register`) était le seul signal de vie qu'un worker
donnait au maître — une fois le tir lancé, plus rien. Un worker qui meurt en cours de route
(process tué, conteneur évincé, coupure réseau) laissait donc le maître attendre indéfiniment
un rapport qui ne viendrait jamais : `MasterCoordinator.WaitForReportsAsync` n'avait aucun
timeout, et son unique appelant le sollicitait avec `CancellationToken.None`.

**Heartbeat continu, pas un enregistrement ponctuel.** Chaque worker signale maintenant qu'il
est vivant en continu (`POST /master/heartbeat`, `WorkerLivenessHostedService`), toutes les
`Worker.HeartbeatIntervalSeconds` (5 s par défaut), jusqu'à l'arrêt du process — plus seulement
une fois, à l'enregistrement. Passé `Master.WorkerDeadAfterSeconds` (20 s par défaut, soit 4
heartbeats manqués) sans signal d'un worker dispatché n'ayant pas encore rapporté,
`MasterCoordinator.MarkDeadIfStale` le déclare perdu — ce qui suffit à faire avancer
`WaitForReportsAsync` (le compte "rentré" inclut désormais les workers déclarés morts, pas
seulement les rapports effectivement reçus) sans attendre le worker manquant. Un heartbeat
tardif annule un faux positif tant que l'attente n'a pas déjà rendu la main.

**Fusion partielle honnête, pas un rapport silencieusement incomplet.** `ClusterReportAggregator.Merge`
tolérait déjà un sous-ensemble de rapports (il ne lève que sur une liste vide) — c'est la
couche d'orchestration qui exigeait jusqu'ici que *tous* les workers dispatchés rapportent.
Le rapport final expose maintenant `LostWorkers` : la liste des workers dispatchés qui n'ont
pas rapporté, rendue en bandeau d'avertissement par `ToTable()`/`ToHtml()`, sur le même modèle
que l'avertissement déjà existant pour les mesures perdues. Si *tous* les workers dispatchés
sont perdus, le maître refuse de fusionner une liste vide (ce serait un rapport fabriqué à
partir de rien) et échoue proprement plutôt que de planter sur une exception non gérée.

**Filet de sécurité optionnel pour le cas résiduel non couvert par le heartbeat** : un worker
dont le *process* reste vivant (donc continue de heartbeat normalement) mais dont le *tir
local* est bloqué (deadlock dans un workflow, par exemple) n'est pas détecté par ce mécanisme.
`Master.ReportTimeoutSeconds` (`null` par défaut, donc désactivé) plafonne alors l'attente de
manière absolue, quel que soit l'état des heartbeats — à renseigner explicitement si ce risque
est réel pour le scénario exécuté. Limite assumée plutôt que résolue ici : le détecter
proprement demanderait que le heartbeat porte un signal de progression du tir, pas seulement
« le process répond ».

Vérifié par un vrai tir distribué (1 maître, 2 workers, process réels — pas de simulation,
`Master.WorkerDeadAfterSeconds` réduit à 6 s pour resserrer le test) : un des deux workers tué
(`kill -9`) en cours de tir est déclaré perdu ~6 s après son dernier heartbeat ; le rapport
final contient `lostWorkers: ["http://localhost:5301"]` et les statistiques réelles du seul
worker survivant (60 itérations, percentiles réels, ni vides ni fabriqués) — le maître ne reste
jamais bloqué. Un second tir témoin, sans tuer aucun worker, confirme l'absence de faux positif
(aucun worker déclaré perdu, rapport complet aux deux workers).

### TLS sur le control plane

`ClusterSharedSecret` protège l'**authentification** du control plane (`/master/register`,
`/master/report`, `/master/heartbeat`, `/worker/prepare`, `/worker/start`) mais rien ne
protégeait jusqu'ici sa **confidentialité** : un rapport de tir, un scénario propagé, ou le
secret partagé lui-même circulaient en clair sur le réseau.

**Un seul certificat auto-signé partagé par les trois rôles, épinglé par empreinte — pas une
PKI complète.** Même philosophie que le secret partagé : simple plutôt qu'une infrastructure
lourde. Le même certificat (paire clé publique/privée) est installé sur le maître et sur chaque
worker ; chacun le présente via Kestrel (configuration standard ASP.NET Core, aucun code
supplémentaire) et chacun valide le certificat présenté par ses pairs contre une seule
empreinte configurée — symétrique, une seule valeur de configuration, comme
`ClusterSharedSecret` :

```json
"Tempest": { "ClusterCertificateThumbprint": "9DAB455A2D1D91CA1D077E52AF6C46449E037242" }
```

Côté serveur, rien à coder : ASP.NET Core sert déjà du HTTPS par pure configuration —

```bash
ASPNETCORE_URLS=https://+:5299
Kestrel__Certificates__Default__Path=/chemin/vers/cluster.pfx
Kestrel__Certificates__Default__Password=...
```

Côté client, `ClusterCertificatePinning.CreateHandler` pose un
`ServerCertificateCustomValidationCallback` sur le client HTTP nommé partagé par
`WorkerLivenessHostedService`, `MasterOrchestrationHostedService` et
`WorkerCoordinator.SubmitReportAsync` — les trois seuls points d'appel HTTP entre maître et
workers — qui compare l'empreinte du certificat présenté à celle configurée
(`CryptographicOperations`-style, insensible à la casse et aux séparateurs `:`), plutôt que la
chaîne de confiance par défaut, inadaptée à un certificat auto-signé. `null` par défaut : la
validation standard du système s'applique alors, sans effet en HTTP, et reste utilisable si un
opérateur préfère un certificat signé par une vraie CA plutôt que le certificat partagé.

Générer un certificat de test (PowerShell) :

```powershell
$cert = New-SelfSignedCertificate -DnsName "tempest-cluster" -CertStoreLocation "Cert:\CurrentUser\My" -KeyExportPolicy Exportable
Export-PfxCertificate -Cert $cert -FilePath cluster.pfx -Password (ConvertTo-SecureString -String "..." -Force -AsPlainText)
$cert.Thumbprint
```

**Limites assumées, pas résolues ici** : un seul certificat partagé plutôt qu'une PKI avec un
certificat par nœud (une vraie infrastructure de certificats, via `cert-manager` par exemple,
trouvera naturellement sa place dans le chantier Kubernetes suivant) ; pas de révocation ; une
rotation du certificat exige de reconfigurer l'empreinte à la main sur les trois rôles.
`docker-compose.yml` reste volontairement en HTTP — monter un certificat partagé dans trois
conteneurs ajouterait de la complexité pour une démo locale sans enjeu de confidentialité réel ;
la configuration TLS ci-dessus s'ajoute à la main pour qui veut l'essayer.

**Un vrai bug trouvé par la vérification de bout en bout, pas supposé** : `WorkerCoordinator.SubmitReportAsync`
(`POST /master/report`) utilisait un client HTTP différent des deux autres points d'appel,
oublié lors du câblage initial — les appels d'enregistrement et de sondage passaient bien en
HTTPS avec l'épinglage, mais l'envoi du rapport final échouait en
`RemoteCertificateNameMismatch`. Trouvé en observant un tir réel qui ne se terminait jamais,
pas en relisant le code.

Vérifié par un vrai tir (1 maître, 2 workers, certificat auto-signé partagé, empreinte
configurée sur les trois rôles) : 224 itérations fusionnées, **0 % d'échec**, seuils respectés,
code de sortie 0 — enregistrement, heartbeat, préparation/départ, sondage live et rapport final
circulent tous en HTTPS avec l'empreinte validée. Contre-épreuve : un worker configuré avec une
empreinte volontairement fausse échoue la poignée de main TLS dès la première tentative
d'enregistrement (`AuthenticationException`, `SSL connection could not be established`),
preuve que l'épinglage est réellement appliqué, pas un no-op silencieux. Tir témoin sans TLS
(comme avant ce chantier) : mêmes résultats, aucune régression sur le mode HTTP existant.

### Opérateur Kubernetes

`docker-compose.yml` déploie le mode distribué à la main — au-delà de quelques dizaines de
workers, ça ne tient plus. L'opérateur Kubernetes introduit une ressource personnalisée
`TestRun` (`tempest.dev/v1alpha1`) : décrire un tir (cible, profil, nombre de workers) suffit,
l'opérateur crée les ressources Kubernetes qui le portent et les détruit une fois le tir
terminé.

**Construit avec [KubeOps](https://github.com/dotnet/dotnet-operator-sdk)** (SDK .NET dédié)
plutôt qu'une boucle watch/reconcile écrite à la main — CRD généré depuis des classes C#
annotées (`V1TestRun`), RBAC déclaratif via `[EntityRbac]`, boucle de réconciliation fournie par
le framework. Le contrôleur (`TestRunController`) reste volontairement fin : il délègue la
construction des objets désirés à `TestRunResources`, une classe statique pure et testable sans
cluster (même esprit que `ClusterCertificatePinning`).

**Les workers sont un `StatefulSet` derrière un service headless, pas un `Deployment`** : le
maître adresse chaque worker individuellement (`/worker/prepare`, `/worker/start`), exactement
comme chaque conteneur `worker1`/`worker2` a un nom DNS stable dans `docker-compose.yml`. Chaque
pod calcule sa propre `Worker__SelfUrl` à partir de son nom (Downward API + expansion native
`$(POD_NAME)` de Kubernetes) — aucun changement de code côté `Tempest.Host`.

**Le maître est un `Job` (`restartPolicy: Never`, `backoffLimit: 0`), pas un `Deployment`** :
`MasterOrchestrationHostedService` positionne déjà `Environment.ExitCode` selon le succès/échec
des seuils quand `Tempest__ExitAfterRun` est actif — la condition `Complete`/`Failed` du Job
reflète honnêtement ce résultat sans qu'il soit besoin de parser le rapport de tir. Une fois le
Job terminé, le contrôleur réduit le `StatefulSet` des workers à 0 réplique (patch, pas
suppression) — c'est le nettoyage automatique promis par la ressource `TestRun` ; le
`StatefulSet` reste inspectable mais ne consomme plus de pods.

**Aucune finalisation personnalisée** : chaque ressource fille porte une `OwnerReference` vers
la `TestRun` — la supprimer déclenche le garbage collection natif de Kubernetes (Job,
StatefulSet, Services), sans code de nettoyage à écrire.

Le secret partagé de cluster est référencé par nom (`clusterSharedSecretRef`, un `Secret`
existant + une clé), jamais recopié en clair dans la ressource `TestRun` :

```yaml
apiVersion: tempest.dev/v1alpha1
kind: TestRun
metadata:
  name: testrun-demo
spec:
  image: tempest-host:local
  targetBaseUrl: http://sampletarget:5281
  workerReplicas: 2
  clusterSharedSecretRef:
    name: testrun-demo-secret
    key: shared-secret
  profile:
    - { fromRps: 0, toRps: 10, durationSeconds: 10 }
    - { fromRps: 10, toRps: 10, durationSeconds: 15 }
    - { fromRps: 10, toRps: 0, durationSeconds: 5 }
```

Essayer localement (Docker Desktop, Kubernetes activé dans ses réglages) :

```bash
docker build -f src/Tempest.Host/Dockerfile -t tempest-host:local .
docker build -f src/Tempest.Operator/Dockerfile -t tempest-operator:local .
kubectl apply -k deploy/operator
kubectl apply -f deploy/samples/testrun-demo.yaml
kubectl get testrun testrun-demo -w
```

Vérifié sur un vrai cluster (Kubernetes de Docker Desktop, pas de simulation) : l'opérateur
déployé (CRD + RBAC + `Deployment`, générés par `dotnet kubeops generate operator`) crée bien,
à l'application de la `TestRun`, le service headless, le `StatefulSet` des workers (2 pods,
`-0`/`-1`, adressables individuellement par leur nom DNS stable), le service et le `Job` du
maître — le maître enregistre les deux workers, les fait tourner, sonde leur rapport en direct
(`/worker/report/raw`, visible dans les logs), puis fusionne un rapport final de 224 itérations,
**0 % d'échec, tous les seuils respectés** ; `TestRun.status.phase` est passé par
`Pending → Running → Succeeded`, et le `StatefulSet` des workers est retombé à **0 réplique**
automatiquement une fois le `Job` `Complete`. Contre-épreuve obtenue en conditions réelles (une
première tentative où la cible n'écoutait pas encore sur le port attendu) : le statut a fini à
`Failed` proprement (`Job` en `BackoffLimitExceeded`, workers réduits à 0 réplique quand même),
sans jamais rester bloqué — la détection d'échec du chantier précédent (seuils non respectés)
se propage correctement jusqu'au statut de la ressource Kubernetes. `kubectl delete -f
deploy/samples/testrun-demo.yaml` sur les deux tirs (échoué et réussi) a bien fait disparaître
`Job`, `StatefulSet` et les deux `Service` via le garbage collection natif — aucune ressource
orpheline.

**Limites assumées, pas résolues ici** : les manifestes générés par l'outil CLI KubeOps
(`dotnet kubeops generate operator`) utilisent un `ClusterRole`/`ClusterRoleBinding` — l'opérateur
surveille les `TestRun` sur tout le cluster plutôt que dans un seul namespace ; restreindre la
portée demanderait de configurer explicitement le champ de surveillance côté runtime, non fait
ici. Pas de scénario personnalisé via `ConfigMap` (seuls les workflows déjà nommés dans
`TempestHostOptions` sont sélectionnables via `spec.workflow`). Pas d'automatisation
TLS/`cert-manager` : `spec` ne câble pas `Tempest__ClusterCertificateThumbprint` — HTTP en clair
à l'intérieur du cluster, même choix assumé que `docker-compose.yml` aujourd'hui. Pas de
publication d'image sur un registre (GHCR) : build et chargement locaux uniquement, comme pour
`docker-compose.yml`. `TestRun.status` ne porte pas le contenu du rapport de tir — accès par
`kubectl port-forward` sur le service du maître, comme aujourd'hui.

### Autoscaling

L'opérateur dimensionne `workerReplicas` une fois, à la création de la `TestRun` — pour un
profil qui varie fortement (une rampe de 10 à 500 req/s, par exemple), ça oblige soit à
sur-provisionner pour le pic dès le départ, soit à sous-dimensionner et à laisser la dette
d'ordonnancement grimper. `spec.autoscaling` calcule le nombre de workers requis **palier par
palier** à partir du débit cible du profil, déjà connu en entier à l'avance — pas d'un HPA/KEDA
réactif à des métriques observées en direct : le plan est **prévisionnel**, produit une seule
fois à partir de `spec.profile`, jamais mesuré.

**Renseigné, `spec.autoscaling` prime sur `workerReplicas` (ignoré).** Trois champs :
`maxRequestsPerSecondPerWorker` (capacité déclarée d'un seul worker — une hypothèse de
l'opérateur du cluster, jamais une mesure), `minWorkerReplicas`/`maxWorkerReplicas` (plancher et
garde-fou), `scaleAheadSeconds` (avance avec laquelle le `StatefulSet` est ajusté avant un palier
plus exigeant, pour laisser le temps au pod de démarrer et de s'auto-enregistrer — best-effort,
sans garantie si le pod met plus longtemps que prévu).

```yaml
spec:
  autoscaling:
    maxRequestsPerSecondPerWorker: 5
    minWorkerReplicas: 1
    maxWorkerReplicas: 6
    scaleAheadSeconds: 5
  profile:
    - { fromRps: 0, toRps: 5, durationSeconds: 20 }   # 1 worker
    - { fromRps: 5, toRps: 20, durationSeconds: 15 }  # 4 workers
    - { fromRps: 20, toRps: 5, durationSeconds: 10 }  # encore 4 (pic a 20)
    - { fromRps: 5, toRps: 0, durationSeconds: 5 }    # retombe a 1
```

**Ce que ce chantier a dû rouvrir pour que "live" soit honnête, pas seulement un
dimensionnement statique au démarrage** : le mode distribué existant divise le profil **une
seule fois**, à `/worker/prepare`, par le nombre de workers alors enregistrés — figé pour tout
le tir. Autoscaling live demande deux choses que ce protocole ne permettait pas :

- **Un worker qui rejoint en cours de route** : `MasterOrchestrationHostedService.ExecuteAdaptiveAsync`
  suit le plan de paliers (`Master__StagePlannedWorkers`, posé par l'opérateur) plutôt que
  d'attendre un nombre fixe de workers une seule fois. Un nouveau worker enregistré entre deux
  paliers reçoit, au palier suivant, **une seule** préparation couvrant tous les paliers
  restants — jamais de re-préparation d'un worker déjà lancé (`WorkerCoordinator.Prepare` ne le
  permet pas). Chaque palier de cette préparation est divisé par le compte *prévu* pour ce
  palier (le plan), pas par le nombre de workers réellement actifs à cet instant — c'est ce qui
  permet à des workers dispatchés à des moments différents de contribuer le bon débit combiné une
  fois tous arrivés.
- **Un worker retiré proprement, pas juste tué** : quand le contrôleur réduit le `StatefulSet`
  entre deux paliers moins exigeants, Kubernetes envoie SIGTERM au pod retiré —
  `WorkerCoordinator` annule maintenant le jeton passé à `TargetRpsLoadEngine.RunAsync` sur
  `ApplicationStopping` plutôt que de laisser le process mourir en silence : le tir local
  s'arrête, un rapport **partiel mais réel** est quand même soumis (même chemin que
  `RunAndReportAsync` en fin normale). Un worker qui ne finit pas cet arrêt propre avant
  `terminationGracePeriodSeconds` tombe dans le filet de sécurité déjà existant
  (`MarkDeadIfStale`/`LostWorkers`, chantier [Reprise sur perte d'un
  worker](#reprise-sur-perte-dun-worker)) — réutilisé tel quel, pas réinventé.

Le chemin figé existant (`spec.autoscaling` absent) n'exécute **aucune** des lignes ci-dessus :
`MasterOptions.StagePlannedWorkers` reste `null`, `ExecuteAsync` continue exactement comme avant.

Vérifié sur le vrai cluster Docker Desktop, pas de simulation. **Non-régression** : la démo
existante sans `autoscaling` (`testrun-demo.yaml`) rejouée à l'identique — 2 workers fixes,
`Pending → Running → Succeeded`, 0 % d'échec, scale-to-zero final — comportement inchangé.
**Scale-up réel** avec `testrun-autoscaling-demo.yaml` (5 paliers, capacité 5 req/s/worker,
besoins 1 → 4 → 4 → 4 → 1 workers) : le `StatefulSet` démarre à 1 réplique, grossit à 4 avant le
palier exigeant, comme prévu — mais **pas toujours en un seul geste** : sur un tir, les trois
nouveaux workers ont tous rejoint avant le palier suivant ; sur un autre, un seul pod a mis plus
longtemps à démarrer et n'a rejoint qu'au palier d'après, confirmant en conditions réelles la
limite documentée (`scaleAheadSeconds` best-effort, pas une garantie) sans jamais faire échouer
le tir. **Scale-down réel** : le `StatefulSet` redescend de 4 à 1 *pendant* le tir (pas seulement
au `Complete` final) ; les pods retirés (ordinaux les plus hauts, comportement natif du
`StatefulSet`) reçoivent SIGTERM et soumettent bien un rapport partiel avant `SIGKILL`
(confirmé par l'absence de `lostWorkers` et un rapport final cohérent) — le nouveau hook
`ApplicationStopping` fonctionne. **Contre-épreuve** : un `kubectl delete pod --grace-period=0`
sur un worker actif a été absorbé de la même façon (rapport partiel soumis, rien perdu) — plus
robuste qu'attendu, la même mécanique de coupure propre s'applique à un retrait imprévu, pas
seulement à celui piloté par le contrôleur ; le mécanisme de perte réelle (`MarkDeadIfStale`)
reste couvert par un test dédié à son nouveau paramètre `candidates`, en plus de la preuve déjà
apportée par [Reprise sur perte d'un worker](#reprise-sur-perte-dun-worker) (processus nu tué
sans aucune grâce).

**Deux vrais bugs trouvés en vérifiant, pas en supposant que ça marchait.** (1) Une résolution
DNS transitoire d'un worker de `StatefulSet` tout juste créé (CoreDNS pas encore propagé)
provoquait une exception non gérée dans `MasterOrchestrationHostedService` — le
`BackgroundService` s'arrêtait alors *sans jamais positionner `Environment.ExitCode`*, si bien
que le `Job` rapportait `Complete` (sortie 0) et `TestRun.status.phase` passait à tort à
`Succeeded` sur un tir qui n'avait jamais eu lieu. Reproduit sur le chemin figé **et** le chemin
adaptatif (même fonction `PrepareAsync`, inchangée) — corrigé une fois pour les deux par une
mince enveloppe try/catch autour de `ExecuteAsync` qui positionne l'échec correctement plutôt que
de crasher en silence ; le correctif lui-même vérifié en laissant la même panne DNS se reproduire
organiquement une deuxième fois et observer le nouveau statut `Failed` correct. (2)
`MasterOptions.Validate()` exigeait `ExpectedWorkers ≥ 1` sans condition, alors que l'opérateur
ne l'émet jamais pour une `TestRun` avec `autoscaling` — le maître plantait au démarrage avant
même d'atteindre `ExecuteAdaptiveAsync`. Corrigé en ignorant cette exigence quand un plan de
paliers est présent.

**Limites assumées, pas résolues ici** : prévisionnel, pas réactif à une métrique observée en
direct (latence, taux d'erreur). `scaleAheadSeconds` est du best-effort — un pod plus lent que
prévu à démarrer laisse le palier tourner temporairement sous-dimensionné, non corrigé
rétroactivement. Le débit déjà figé chez un worker en cours de tir n'est jamais rééquilibré : un
palier futur dont le compte change ne modifie que le nombre de workers qui rejoignent ou
partent. Opérateur Kubernetes uniquement — `docker-compose`/mode autonome n'ont aucun moyen de
créer ou détruire des workers.

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
- [x] **Étape 31** — [Groupes et étiquettes](#groupes-et-étiquettes) : `HttpStepDefinition.Group` (préfixé au nom pour former `QualifiedName`, le nom effectivement enregistré — `checkout/pay` reste une `StepId` comme une autre, collision vérifiée sur le nom qualifié) et `ScenarioDefinition.Tags` (métadonnée de tir, reportée via `IWorkflow.Tags` jusqu'à l'en-tête du rapport texte/HTML, jamais dans l'agrégation). Troisième bullet de la roadmap phase 2. Rendu délibérément plat : le rapport affiche le nom qualifié tel quel sans tenter d'en déduire une arborescence visuelle — une première version qui découpait l'affichage sur le dernier `/` cassait tout nom d'étape contenant un `/` sans intention de groupe (démontré par un test d'échappement HTML existant, dont le nom malicieux contient `</script>`), corrigée avant de committer. Limite documentée : étiquettes non propagées au rapport fusionné en mode distribué. Vérifié par un vrai tir : `checkout/login`, `checkout/pay` et `browse` (sans groupe) dans la même table, en-tête `etiquettes : region=eu-west, version=v2` présent en texte et en HTML
- [x] **Étape 32** — [Métriques personnalisées](#métriques-personnalisées) : `CustomMetricKind` (compteur/jauge/taux/tendance), `CustomMetricRegistry`/`CustomMetricId` (`Tempest.Domain.Metrics`, même discipline que `StepRegistry`/`StepId`) et `MetricRule` (`Tempest.Domain.Declarative`, même vocabulaire Regex/XPath/JsonPath que l'extraction et les checks), section `metrics` par étape du format déclaratif. Première fonctionnalité de la phase 2 à ne pas pouvoir réutiliser `StepId`/`StepAccumulator` tels quels (une valeur métier arbitraire n'est pas une durée de requête) : chaîne d'agrégation parallèle complète (`ChannelCustomMetricSink`, `CustomMetricAccumulator`, `CustomMetricsAggregator`), même discipline « canal borné, consommateur unique » que la chaîne native, `VirtualUserContext`/`TargetRpsLoadEngine`/`MetricsProcessor` étendus avec des paramètres additifs par défaut (aucun site d'appel existant cassé). Rendue dans le rapport texte/HTML et dans Prometheus (`tempest.custom.counter`/`.gauge`/`.rate`/`.trend`). Limites documentées : pas de centiles pour la tendance, pas de fenêtre glissante, pas de fusion inter-workers en mode distribué. Vérifié par un vrai tir contre `Tempest.SampleTarget` : compteur, tendance, jauge et taux tous corrects dans le rapport texte et dans `/metrics`
- [x] **Étape 33** — [Temps de réflexion et rythme](#temps-de-réflexion-et-rythme) : `ThinkTimeDefinition` (`Tempest.Domain.Declarative`, durée fixe ou plage tirée uniformément), propriété `HttpStepDefinition.ThinkTime`, `thinkTime`/`thinkTimeMax` par étape du format déclaratif — parsés au même format que `--duration` en CLI (`500ms`, `1s`, `2m`, `1h`). Dernier bullet de la roadmap phase 2 : aucun changement dans le moteur, une pause n'est qu'un `Task.Delay` après que l'étape a publié sa mesure, jamais comptée comme latence de requête — le modèle ouvert de `TargetRpsLoadEngine` l'absorbe nativement en dette d'ordonnancement, exactement comme une réponse HTTP lente. Sans effet sur les scénarios scriptés, qui pouvaient déjà faire une pause via `Task.Delay` directement. Vérifié par de vrais tirs contre `Tempest.SampleTarget` : une pause fixe de 500 ms avec un seul utilisateur virtuel fait tomber le débit effectif à ~2 itérations/s pour un débit cible de 20/s (dette d'ordonnancement en conséquence), latence brute de l'étape HTTP inchangée dans les deux cas — et une plage 100–300 ms sur 4 utilisateurs virtuels montre un p50/p95 d'itération cohérent avec la plage configurée. **Clôt entièrement le contenu de la roadmap phase 2**
- [x] **Étape 34** — [Modèle fermé](#modèle-fermé) : `ClosedModelScheduler` (`Tempest.Application.Execution`, implémente `ILoadScheduler` comme `CoordinatedRateLimiter`), `--vus <n>` en CLI (`TempestHostOptions.ClosedModelDuration`), `LoadTestReport.ClosedModel` avec mise en garde explicite dans `ToTable`/`ToHtml`. Premier bullet de la roadmap phase 3. `AddTempestEngine` accepte désormais un `LoadProfile?` nul, pour laisser un `ILoadScheduler` personnalisé déjà enregistré prendre la place du `CoordinatedRateLimiter` par défaut — le seam d'extension que sa propre documentation promettait depuis le début. Aucune notion de concurrence dédiée à ce nouvel ordonnanceur : elle vient du nombre de travailleurs déjà créés par le moteur (`LoadTestOptions.MaxVirtualUsers`), la contre-pression du canal de jetons suffisant à faire émerger le modèle fermé. Limite documentée : mode distribué non pris en charge pour ce modèle. Vérifié par un vrai tir (`--vus 10 --duration 5s`) : effectif exact, avertissement présent en texte et en JSON, modèle ouvert inchangé en régression
- [x] **Étape 35** — [Montée d'utilisateurs](#montée-dutilisateurs) : `VirtualUserStage`/`VirtualUserProfile` (`Tempest.Domain.Load`, même rampe linéaire que `LoadStage`/`LoadProfile` mais sur un effectif plutôt qu'un débit), `RampingVirtualUserPool` (`Tempest.Application.Execution`) qui remplace la création statique de travailleurs de `TargetRpsLoadEngine` quand `LoadTestOptions.RampProfile` est renseigné, `--vus-from`/`--vus-to` en CLI (`TempestHostOptions.RampVus`). Deuxième bullet « exécuteurs multiples » de la roadmap phase 3 (montée/descente d'utilisateurs ; itérations partagées et par utilisateur restent à faire). Chaque travailleur reçoit son propre jeton d'annulation lié à celui du tir, ce qui permet d'en arrêter un individuellement sans fermer le canal de jetons partagé par les autres — l'émission des jetons elle-même reste un `ClosedModelScheduler` inchangé, configuré sur la durée totale du profil. `TempestHostOptions.IsClosedModel` couvre désormais aussi ce mode (même mise en garde de rapport, aucun échéancier théorique à comparer). Limite documentée : mode distribué non pris en charge, comme pour l'effectif fixe. Vérifié par un vrai tir (`--vus-from 0 --vus-to 20 --duration 8s`) : débit croissant au fil de la rampe, avertissement présent en texte et en JSON, modèles ouvert et fermé à effectif fixe inchangés en régression
- [x] **Étape 36** — [Itérations partagées et itérations par utilisateur](#itérations-partagées-et-itérations-par-utilisateur) : `IterationCountScheduler` (`Tempest.Application.Execution`, implémente `ILoadScheduler` en s'arrêtant sur un nombre fixe de jetons plutôt qu'une durée), `--iterations <n>` (itérations partagées, `TempestHostOptions.SharedIterations`) et `--vus <n> --iterations-per-vu <k>` (itérations par utilisateur, `TempestHostOptions.IterationsPerVirtualUser`) en CLI. **Clôt entièrement le bullet « exécuteurs multiples » et la roadmap phase 3.** `VirtualUserWorker` accepte désormais un quota personnel optionnel au-delà duquel il s'arrête de lui-même sans fermer la file partagée — combiné à un `IterationCountScheduler` dimensionné à effectif × quota, cet auto-arrêt garantit par construction que chaque utilisateur virtuel en fait exactement sa part, prouvé par un test qui trace `VirtualUserId` par itération et vérifie que les 8 utilisateurs virtuels en ont fait exactement 15 chacun, ni plus ni moins. `TempestHostOptions.IsClosedModel` couvre désormais aussi ces deux modes. Limite documentée : mode distribué non pris en charge, comme pour le reste du modèle fermé. Vérifié par de vrais tirs (`--iterations 300 --max-vus 20` puis `--vus 10 --iterations-per-vu 20`) : 300 puis 200 itérations exactement, avertissement présent en texte et en JSON, modèle ouvert inchangé en régression
- [x] **Étape 37** — [Scénarios concurrents](#scénarios-concurrents) : `Tempest:Scenarios` (`TempestHostOptions.Scenarios`/`ScenarioOptions`), `MultiScenarioRunner`/`MultiScenarioHost`/`MultiScenarioLoadTestHostedService` (`Tempest.Host`), `ScenarioRunSpec` (`Tempest.Application.Execution`), `ScenarioReport`/`MultiScenarioReport` (`Tempest.Domain.Metrics`). **Clôt le bullet « scénarios concurrents » de la phase 3, ne laissant plus que le bridage.** Chaque scénario construit sa propre chaîne complète à la main plutôt que via le conteneur d'injection de dépendances (qui n'enregistre qu'un singleton de chaque type) — c'est cet isolement complet, pas un préfixe de nom, qui garantit que deux scénarios déclarant la même étape ne voient jamais leurs mesures fusionnées, prouvé par un test qui fait tourner deux scénarios partageant un nom d'étape et vérifie que chacun garde son propre compte. `LoadTestReport.ToHtml` refactorisé (extraction de `AppendHtmlShellStart`/`AppendBodyContent`) pour permettre un document HTML unique avec une section par scénario, sans changement de sortie pour le tir simple (569 tests existants toujours verts après coup). Limites documentées : mode distribué non pris en charge, `/report/live` et `/metrics` non alimentés. Vérifié par un vrai tir à deux scénarios (modèle ouvert + modèle fermé, étape de même nom `browse` dans les deux) contre `Tempest.SampleTarget` : 100 puis 943 itérations indépendantes, jamais fusionnées, étiquettes et seuils propres à chacune, tir simple inchangé en régression
- [x] **Étape 38** — [Bridage](#bridage) : `RateCappedScheduler` (`Tempest.Application.Execution`, décorateur d'`ILoadScheduler`), `TempestHostOptions.MaxRequestsPerSecond`/`ScenarioOptions.MaxRequestsPerSecond`, `--max-rps` en CLI. **Clôt entièrement la phase 3 de la roadmap.** Le décorateur enveloppe le `ChannelWriter` remis à l'ordonnanceur choisi plutôt que son `Run` lui-même, ce qui le fait composer identiquement avec les quatre ordonnanceurs existants sans en modifier aucun ; le retard qu'il impose porte sur la transmission, jamais sur `ExecutionToken.ScheduledTicks` déjà fixé par l'ordonnanceur enveloppé, donc il se mesure côté rapport exactement comme une dette d'ordonnancement plutôt que d'être masqué. `--max-rps` n'est mutuellement exclusif avec rien (overlay, pas un cinquième modèle) et compose avec `Tempest:Scenarios`, où `ScenarioOptions.MaxRequestsPerSecond` retombe sur le plafond global si absent — même convention que `TargetBaseUrl`. Vérifié par de vrais tirs contre `Tempest.SampleTarget` : `--rps 100 --duration 5s --max-rps 20` ramené à 20 RPS exactement (dette ~20s, cohérente avec le retard imposé) ; `--vus 10 --duration 5s --max-rps 15` (176 RPS naturels avec ces 10 utilisateurs virtuels) ramené à 15 RPS exactement ; scénarios concurrents avec plafond propre (5 RPS) et repli sur le plafond global (8 RPS) tous deux respectés
- [x] **Étape 39** — [Série temporelle](#série-temporelle) : `TimeSeriesSample`/`LoadTestReport.TimeSeries` (`Tempest.Domain.Metrics`), `TimeSeriesRecorder` (`Tempest.Application.Metrics`), `ActiveVirtualUserGauge` (`Tempest.Application.Execution`), `TempestHostOptions.TimeSeriesIntervalSeconds`. **Premier bullet de la phase 4 (« un rapport au niveau de Gatling »).** Le relevé tourne en parallèle du moteur sur un jeton d'annulation distinct de celui de l'hôte — sans quoi il ne s'arrêterait jamais après un tir à durée ou itérations fixes, `stoppingToken` ne se déclenchant qu'à l'arrêt de l'hôte. `ActiveVirtualUserGauge` est la première mesure de concurrence *réelle* dans le temps, par opposition au plafond configuré : chaque `VirtualUserWorker` l'incrémente/décrémente à l'entrée et à la sortie de sa boucle de consommation, quelle que soit la raison de la sortie. Un tir plus court que l'intervalle de relevé garde toujours au moins un point. Limites documentées : non alimentée pour les scénarios concurrents (`MultiScenarioRunner`), rendue en table pour l'instant, pas encore en courbe. Vérifié par un vrai tir (`--vus-from 2 --vus-to 20 --duration 10s`) : effectif actif croissant fidèle à la rampe (3 → 7 → 10 → 14 → 18), débit croissant en conséquence, dans le rapport texte et JSON
- [x] **Étape 40** — [Distribution des temps de réponse](#distribution-des-temps-de-réponse), [Courbe de dette d'ordonnancement superposée](#courbe-de-dette-dordonnancement-superposée) et [Tableau de bord temps réel](#tableau-de-bord-temps-réel) : `LatencyHistogram.UpperBoundOf` rendu public, `StepStatistics.ResponseHistogram` (paniers bruts, une entrée par étape), rendu en barres SVG groupées par octave dans `LoadTestReport.ToHtml` ; le même `ToHtml` superpose désormais débit et dette d'ordonnancement (`LoadTestReport.TimeSeries`) sur un graphe en ligne SVG, chacun à l'échelle de son propre maximum ; `/report/live.html` (`TempestHostOptions.LiveDashboardRefreshSeconds`) sert ce même rendu sur la fenêtre glissante avec une balise `<meta http-equiv="refresh">`, qui le recharge seul pendant le tir. **Clôt entièrement les trois bullets restants de la phase 4 et la phase 4 elle-même.** Le regroupement par octave n'invente aucune résolution : c'est le découpage natif de `LatencyHistogram` (`PRECISION_BITS`), pas une approximation ajoutée pour l'affichage. `<meta http-equiv="refresh">` plutôt que SSE/`EventSource` (aucun des deux n'existait déjà dans `Tempest.Host`) : un rechargement de page entier reste largement suffisant pour un tableau de bord d'opérateur, sans nouvelle infrastructure temps réel à maintenir. Limite documentée : comme `/report/live`, `/report/live.html` n'est pas alimenté pour les scénarios concurrents. Vérifié par de vrais tirs contre `Tempest.SampleTarget` : un tir bridé (`--rps 300 --max-rps 20`) produit un histogramme par étape cohérent avec les centiles déjà publiés et une dette d'ordonnancement visible sur la courbe superposée (rapport HTML et JSON) ; `/report/live.html` interrogé deux fois pendant un tir en cours montre un débit croissant (49 puis 99 it/s) avec la balise de rechargement présente, `/report.html` cumulé restant sans cette balise
- [x] **Étape 41** — [Convertisseur HAR](#convertisseur-har) : nouveau projet `tools/Tempest.HarConvert`, sans aucune dépendance à `Tempest.Domain`/`Tempest.Scenarios` (il ne fait qu'émettre du texte C#). **Premier bullet de la phase 5, conformément à la décision structurante de la roadmap phase 2 : sortie en scénario scripté (`.csx`), jamais en YAML/JSON.** Filtrage des actifs statiques par extension (bruit majoritaire d'un HAR de chargement de page complet) et sélection de l'hôte cible par fréquence plutôt que par ordre d'apparition — un bug réel de la première version, trouvé en vérifiant un vrai HAR reconstitué d'un aller-retour réel contre `Tempest.SampleTarget` : un appel tiers sans extension reconnue dans son chemin précédait le premier appel à la cible et devenait par erreur l'« hôte de base », faisant passer la cible elle-même pour un hôte secondaire à ignorer. Corrigé avant de documenter cette section, avec un test de régression dédié. Limites documentées en tête du fichier généré : authentification/cookies capturés à revoir manuellement (valeurs de session probablement expirées), corps multipart non pris en charge. Vérifié par un vrai tir : le scénario généré à partir d'un HAR mêlant trafic réel, actif statique et hôte secondaire compile via Roslyn et s'exécute contre `Tempest.SampleTarget` (`tempest run ... --rps 5 --duration 5s`), login et catalogue à 0 % d'échec, checkout à 100 % d'échec — le jeton capturé avait expiré au moment du tir, exactement la mise en garde documentée, pas une anomalie
- [x] **Étape 42** — [Convertisseur OpenAPI](#convertisseur-openapi) : nouveau projet `tools/Tempest.OpenApiConvert`, même absence totale de dépendance que `Tempest.HarConvert`. **Deuxième bullet de la phase 5**, même sortie scriptée (`.csx`). Différence de nature avec le HAR, assumée dans la doc : une spécification ne décrit que la forme d'une API, jamais des données réelles, donc la sortie est un **squelette** (mot de la roadmap elle-même), pas un scénario directement jouable. Un step par opération, résolution de `$ref` locales vers `components/schemas` avec garde anti-cycle pour générer un corps JSON d'exemple, placeholders dérivés du type pour les paramètres de chemin/requête requis et les en-têtes, aucune traduction de schéma d'authentification (même raison que pour le HAR). Comptés plutôt que silencieux : chemins sans méthode HTTP prise en charge, opérations à corps non-JSON. Vérifié par deux vrais tirs contre `Tempest.SampleTarget`, à partir d'une spécification décrivant fidèlement ses trois routes réelles : le squelette non modifié donne `login`/`listProducts` à 0 % d'échec et `checkout` à 100 % (placeholder d'authentification, limite documentée) ; le même squelette complété à la main (jeton et identifiant de produit lus dans les réponses précédentes, exactement ce qu'un humain ajouterait) donne les 3 étapes à 0 % d'échec
- [x] **Étape 43** — [Convertisseur Postman](#convertisseur-postman) : nouveau projet `tools/Tempest.PostmanConvert`, même absence totale de dépendance. **Troisième et dernier bullet de la phase 5, qui clôt entièrement la phase** (hors proxy enregistreur, conditionné à un vrai public). Même nature de squelette que l'OpenAPI, pour la même raison (une collection décrit des requêtes construites à la main, pas des données réelles). Dossiers imbriqués parcourus récursivement, nom d'étape qualifié par ses dossiers parents ; variables de collection (`{{nom}}`) substituées dans l'URL/en-têtes/corps, y compris quand elles résolvent l'hôte lui-même (l'hôte reste de toute façon ignoré à l'exécution, fixé par `--target-url`) ; variable non résolue comptée plutôt que silencieuse. Limites documentées : pas d'environnement Postman séparé lu (variables de collection seules), corps `formdata` non pris en charge, aucun schéma d'authentification traduit. Trouvaille réelle en vérifiant une vraie conversion, documentée plutôt que corrigée en silence : un placeholder substitué dans un corps JSON sans guillemets (convention Postman pour injecter un nombre, `"productId":{{id}}`) casse la syntaxe JSON — une variable Postman n'a pas de schéma pour deviner son type, contrairement à l'OpenAPI. Vérifié par deux vrais tirs contre `Tempest.SampleTarget`, à partir d'une collection décrivant fidèlement ses trois routes réelles avec un dossier et une variable `{{baseUrl}}` : squelette non modifié à 100 % d'échec sur `Checkout` (placeholders documentés) ; complété à la main (même principe que l'OpenAPI), les 3 étapes à 0 % d'échec
- [x] **Étape 44** — [Proxy enregistreur](#proxy-enregistreur) : nouveau projet `tools/Tempest.RecorderProxy`, `ProjectReference` vers `Tempest.HarConvert` (seul convertisseur à en dépendre — reutilise `HarConverter.Convert` tel quel, une capture en direct alimentant la même forme `HarEntry` qu'un export HAR). **Dernier bullet de la phase 5, qui la clôt entièrement cette fois.** Scope volontairement réduit face au *recorder* de Gatling : reverse proxy à cible unique, HTTP seul, pas d'interception TLS — cohérent avec le modèle `--target-url` unique de `tempest run`, évite tout le chantier certificat/confiance d'un vrai MITM pour une fonctionnalité encore conditionnée à un vrai public. Arrêt propre via Ctrl+C ou `POST /__tempest-recorder/stop` (pilotage scripté), qui déclenche la génération du `.csx`. Limites documentées : corps binaire retransmis mais jamais capturé (même nature que le multipart du HAR). Vérifié par un vrai tir de bout en bout : proxy démarré contre `Tempest.SampleTarget`, vraie session login/catalogue/checkout envoyée à travers lui (statuts 200 confirmés, identiques à la cible directe), arrêté via l'endpoint de contrôle, scénario généré puis rejoué immédiatement via `tempest run` — les 4 étapes à 0 % d'échec, y compris `checkout` : capturé et rejoué sans le délai d'export/conversion manuel du HAR, le jeton était encore valide, contrairement à la section précédente
- [x] **Étape 45** — [Contrat de plugin](#contrat-de-plugin) : `PluginWorkflowLoader` (`Tempest.Scenarios`) charge un `IWorkflow` depuis une assembly `.dll` compilée indépendamment de ce dépôt (`Assembly.LoadFrom`, résolution du type par `--plugin-type` ou candidat unique, constructeur public sans paramètre requis). **Premier bullet de la phase 6** : le contrat lui-même (`IWorkflow`/`IVirtualUserContext`/`StepScope`) existait déjà, agnostique du protocole — ce qui manquait était un moyen de le charger sans `ProjectReference`. `WorkflowFileLoader` reconnaît désormais `.dll` en plus de `.yaml`/`.json`/`.csx`/`.cs`, même flag `PluginWorkflowType` disponible par scénario en mode concurrent. Limites documentées : aucune configuration injectée dans le type instancié (un plugin gère la sienne), pas de résolution NuGet dans cette version (chemin de fichier déjà présent sur le disque uniquement), mode distribué non pris en charge (même limite que le scripté). `samples/Tempest.SamplePlugin` est la preuve réelle du découplage : projet compilé séparément, jamais référencé par `Tempest.Host`/`Tempest.Cli`/`Tempest.Scenarios`, seule dépendance `Tempest.Domain`. Vérifié par deux vrais tirs contre `Tempest.SampleTarget`, l'assembly compilée puis chargée par son chemin : sélection automatique du seul type disponible, puis sélection explicite via `--plugin-type` — les deux à 0 % d'échec
- [x] **Étape 46** — [Résolution NuGet](#résolution-nuget) : `NuGetPluginResolver` (`Tempest.Scenarios`, `NuGet.Protocol`) résout un plugin par identifiant de paquet plutôt que par chemin de fichier déjà sur le disque — deuxième moitié du bullet « chargement dynamique/résolution NuGet » de la phase 6. `--plugin-package`/`--plugin-package-version`/`--plugin-source` (répétable), même flags disponibles par scénario en mode concurrent. Télécharge le `.nupkg` depuis la première source qui connaît le paquet, extrait le groupe `lib/<tfm>` le plus proche de `net10.0` (`FrameworkReducer`), cache local persistant entre les tirs — une version explicite déjà en cache ne redéclenche aucun trafic réseau, contrairement à « dernière version stable » qui revalide toujours. Limite documentée : aucune résolution de dépendances transitives du paquet, seule sa propre bibliothèque est extraite. Vérifié par un vrai tir : `Tempest.SamplePlugin` empaqueté via `dotnet pack` dans un dossier local (flux NuGet à part entière), résolu puis exécuté contre `Tempest.SampleTarget` — 0 % d'échec, confirmé une deuxième fois avec une version explicite pour vérifier le chemin de cache
- [x] **Étape 47** — [Protocoles de référence — SQL](#sql) : `extensions/Tempest.Extensions.Sql` interroge une vraie base SQLite plutôt que le client HTTP partagé — première extension protocole de la phase 6, referme le SQL explicitement écarté des jeux de données en phase 2 (même mot, angle différent : protocole de charge, pas source de paramètres). Deux étapes réelles par itération (`SELECT` paramétré, `INSERT`), mode WAL pour la concurrence entre utilisateurs virtuels, graine fixe pour la reproductibilité. Trouvaille réelle documentée plutôt que corrigée dans le cœur : un plugin chargé par `Assembly.LoadFrom` doit être publié (`dotnet publish`), pas seulement compilé, pour que ses dépendances NuGet transitives soient résolues ; sa bibliothèque *native* (`e_sqlite3`) doit en plus être cherchée par le plugin lui-même via `NativeLibrary.SetDllImportResolver`, `SQLitePCLRaw` la cherchant par défaut à côté de l'hôte (`Tempest.Cli`) plutôt qu'à côté du plugin. Vérifié par un vrai tir : plugin publié contre une base SQLite fraîche, 30 itérations, 0 % d'échec sur les deux étapes, lignes persistées confirmées
- [x] **Étape 48** — [Protocoles de référence — SSE](#sse) : `extensions/Tempest.Extensions.Sse` valide le contrat sous un angle différent de SQL — pas un protocole différent de HTTP, mais un usage différent d'`IVirtualUserContext.HttpClient` : une réponse en flux continu lue événement par événement, plutôt que l'aller-retour requête/réponse unique de tout le reste du dépôt. Deux étapes réelles par itération (`SSE connect` via `HttpCompletionOption.ResponseHeadersRead` + vérification du `Content-Type`, `SSE receive events` via lecture ligne à ligne bornée par un délai par itération). Contrairement à SQL, utilise `--target-url` normalement (le client HTTP partagé pointe déjà vers la cible) et ne dépend d'aucun paquet NuGet au-delà de `Tempest.Domain` — confirmation par contraste que l'exigence de `dotnet publish` découverte pour SQL tenait à sa dépendance externe, pas au contrat de plugin lui-même : un simple `dotnet build` suffit ici. Nouveau point d'écoute `GET /api/events/stream` sur `Tempest.SampleTarget`, nombre d'événements piloté par la requête elle-même. Vérifié par deux vrais tirs : sélection par défaut sans configuration, puis `--plugin-type` explicite avec nombre d'événements et délai personnalisés — les deux à 0 % d'échec
- [x] **Étape 49** — [Protocoles de référence — MQTT](#mqtt) : `extensions/Tempest.Extensions.Mqtt` revient à un protocole réellement différent de HTTP comme SQL, mais orienté publication/abonnement — chaque itération s'abonne à un sujet qui lui est propre (`{préfixe}/{utilisateur}/{itération}`, pour ne jamais recevoir le message d'un autre utilisateur virtuel), y publie un message, puis attend sa propre réception : le round-trip complet, pas un simple accusé de publication. Deux étapes réelles (`MQTT connect`, `MQTT publish/receive` borné par un délai par itération). `Tempest.SampleTarget` héberge désormais son propre courtier MQTT embarqué (`MQTTnet.Server`, port dédié) pour que la vérification reste sans infrastructure externe, même logique que SQLite pour SQL. Confirmation réelle plutôt que nouvelle trouvaille : comme SQL, ce plugin doit être publié (`dotnet publish`), pas seulement compilé — vrai même ici où la seule dépendance ajoutée (`MQTTnet`) est entièrement gérée, sans composant natif ; confirmé en isolant le problème via un harnais direct avant de le reproduire puis de le corriger par publication. Vérifié par deux vrais tirs contre le courtier embarqué : sélection par défaut, puis `--plugin-type` explicite avec préfixe de sujet personnalisé — les deux à 0 % d'échec
- [x] **Étape 50** — [Protocoles de référence — GraphQL](#graphql) : `extensions/Tempest.Extensions.GraphQl` clôt les quatre protocoles de référence de la phase 6. Comme SSE, reste au-dessus de HTTP mais valide un autre aspect du contrat : succès/échec se lit dans le corps JSON (`errors`), jamais dans le code de statut qui reste 200 même pour une mutation en échec métier — contrairement à toute autre étape HTTP du dépôt, qui utilise le code de statut comme seul signal. Deux étapes réelles, mêmes natures d'opération que SQL sous revêtement HTTP (`GraphQL query` en lecture, `GraphQL mutation` en écriture pour un identifiant tiré dans une plage configurable). `Tempest.SampleTarget` héberge un schéma GraphQL réel (`GraphQL`, moteur GraphQL.NET) exposé à la main sur `POST /graphql`, dont la mutation échoue avec une entrée `errors` plutôt qu'un code de statut différent de 200 pour un identifiant inconnu. Aucune dépendance NuGet au-delà de `Tempest.Domain` côté plugin : un simple `dotnet build` suffit, comme SSE. **Clôt entièrement le bullet « protocoles de référence » de la phase 6.** Vérifié par deux vrais tirs contre le vrai schéma GraphQL de `Tempest.SampleTarget` : sélection par défaut, puis `--plugin-type` explicite avec une plage d'identifiants réduite — les deux à 0 % d'échec
- [x] **Étape 51** — [Guide d'écriture d'extension](#guide-décriture-dextension) : dernier bullet de la phase 6, purement documentaire — le contrat lui-même n'a pas changé. Quand écrire une extension plutôt qu'un scénario scripté, le contrat minimal (`IWorkflow` en détail, ordre d'appel des cinq méthodes, discipline du chemin chaud), un premier plugin pas à pas, le choix `dotnet build`/`dotnet publish` selon la nature de la dépendance ajoutée, la distribution par paquet NuGet, la discipline de test (vrai double, jamais un mock) et un tableau récapitulatif des quatre protocoles de référence comme exemples travaillés. **Clôt entièrement la phase 6.** Vérifié en suivant le guide à la lettre depuis un dossier vide, sans rien copier depuis ce dépôt : un plugin minimal construit par un simple `dotnet build` puis chargé par `tempest run` contre `Tempest.SampleTarget` réellement démarré — sélection automatique du seul type disponible, puis `--plugin-type` explicite — les deux à 0 % d'échec. Dossier jetable, jamais commité

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

Ce différenciateur est maintenant démontré, pas seulement affirmé : **[benchmark/](benchmark/README.md)**
fait tourner Tempest, k6, Gatling et NBomber contre la même cible saturée avec le même scénario, et
publie les résultats bruts dans [benchmark/results/RESULTS.md](benchmark/results/RESULTS.md).

## Licence

[Apache License 2.0](LICENSE) — la même que k6 et Gatling.
