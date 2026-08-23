# Installation

## Démarrage

```bash
dotnet test Sirocco.sln -c Release
```

Étalonnage de l'injecteur (scénario à vide, ni réseau ni agrégation) :

```bash
dotnet run --project tools/Sirocco.Probe -c Release
```

Premier tir réel, contre la cible de démonstration — deux processus, deux terminaux :

```bash
dotnet run --project samples/Sirocco.SampleTarget -c Release   # écoute sur :5299
dotnet run --project src/Sirocco.Host -c Release                # tire, écoute sur :5280
```

Pendant et après le tir : `http://localhost:5280/report/live` (fenêtre glissante),
`.../report` (cumulé), `.../report.html` (le même rapport cumulé, en page HTML autonome —
lisible directement dans un navigateur, sans serveur ni JSON à interpréter), `.../report/live.html`
(la fenêtre glissante en page HTML, qui se recharge seule pendant le tir — voir
[Tableau de bord temps réel](../rapports/mesure.md#tableau-de-bord-temps-réel)), `.../metrics` (Prometheus).

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


## Installation

`Sirocco.Cli` s'empaquette comme un [outil global
dotnet](https://learn.microsoft.com/dotnet/core/tools/global-tools) (`PackAsTool`, commande
`sirocco`, distincte du nom de paquet `Sirocco.Cli`) :

```bash
dotnet pack src/Sirocco.Cli -c Release
dotnet tool install --global --add-source src/Sirocco.Cli/bin/Release Sirocco.Cli
sirocco run --target-url http://localhost:5299 --rps 50 --duration 30s
```

Pas encore publié sur nuget.org — le dépôt reste privé (bullet suivant de la phase 1) — donc
l'installation se fait aujourd'hui depuis une source locale (`--add-source`) ou un flux privé.
Sans empaquetage, `sirocco` reste utilisable via `dotnet run --project src/Sirocco.Cli --`.

Vérifié par un vrai `dotnet tool install --global`, `sirocco` réellement sur le `PATH`, puis un
tir depuis un répertoire sans rapport avec le dépôt (`/tmp`), contre `Sirocco.SampleTarget`, avec
`--report-json` écrit sur disque.

## Binaires autonomes

`Sirocco.Cli` se publie aussi en binaire autonome (*self-contained*, fichier unique) pour Windows,
Linux et macOS (x64 et arm64) — aucun SDK ni runtime .NET requis sur la machine qui l'exécute :

```bash
dotnet publish src/Sirocco.Cli -c Release -r win-x64 \
  -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
# -> src/Sirocco.Cli/bin/Release/net10.0/win-x64/publish/sirocco.exe (linux-x64/osx-x64/osx-arm64 : "sirocco")
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

Vérifié par de vrais tirs : le `sirocco.exe` win-x64 publié, exécuté directement (sans `dotnet
run`), contre `Sirocco.SampleTarget`, rapport JSON écrit sur disque, code de sortie 0 — et les
trois autres RID compilés sans erreur (non exécutables depuis cette machine Windows). Le nettoyage
des fichiers hérités de `Sirocco.Host` (`appsettings*.json`, `web.config`) a été vérifié : sans
lui, un `sirocco run` sans `--target-url` aurait silencieusement trouvé la cible de démonstration
de l'hôte au lieu d'échouer avec le message d'erreur attendu.

**Limite restante** : dépôt encore privé — dernier bullet de la
[phase 1 de ROADMAP.md](https://github.com/coulibalyousmane/Sirocco/blob/main/ROADMAP.md#phase-1--rendre-sirocco-installable).


## Paquets NuGet

Le moteur se consomme aussi comme une bibliothèque C#, à la NBomber : `Sirocco.Domain`,
`Sirocco.Application`, `Sirocco.Infrastructure` et `Sirocco.Scenarios` s'installent séparément,
chacun avec son propre rôle.

| Paquet | Contenu | Dépend de |
|---|---|---|
| `Sirocco.Domain` | Contrats (`IWorkflow`, `IVirtualUserContext`), profils de charge, rapports. Zéro dépendance NuGet. | — |
| `Sirocco.Application` | Le moteur (`TargetRpsLoadEngine`, `AddSiroccoEngine`) — déroule un `IWorkflow` à un débit cible. | `Sirocco.Domain` |
| `Sirocco.Infrastructure` | La chaîne de mesure (`AddSiroccoMetrics`, export OpenTelemetry) — transforme les mesures en `LoadTestReport`. | `Sirocco.Domain`, `Sirocco.Application` |
| `Sirocco.Scenarios` | Scénarios de référence (`DynamicCheckoutWorkflow`, gRPC, WebSocket) et le format déclaratif. | `Sirocco.Domain` |

```csharp
// Ecrire et lancer un scenario depuis un projet xUnit, sans cloner ce depot :
HostApplicationBuilder builder = Host.CreateApplicationBuilder();
builder.Services.AddSingleton(new HttpClient { BaseAddress = new Uri("https://votre-cible") });
builder.Services.AddSingleton<IWorkflow>(new DynamicCheckoutWorkflow());

LoadProfile profile = new([LoadStage.Ramp(fromRps: 0, toRps: 50, TimeSpan.FromSeconds(30))]);
builder.Services.AddSiroccoEngine(profile, new LoadTestOptions { MaxVirtualUsers = 100 });
builder.Services.AddSiroccoMetrics();

using IHost host = builder.Build();
await host.StartAsync();

TargetRpsLoadEngine engine = host.Services.GetRequiredService<TargetRpsLoadEngine>();
MetricsProcessor metricsProcessor = host.Services.GetRequiredService<MetricsProcessor>();
metricsProcessor.Start();
LoadTestSummary summary = await engine.RunAsync(CancellationToken.None);
await metricsProcessor.StopAsync();

LoadTestReport report = metricsProcessor.Aggregator.Snapshot(StatisticsScope.Cumulative);
```

Le bullet initial de ROADMAP.md ne citait que `Sirocco.Domain` et `Sirocco.Scenarios` — élargi à
`Sirocco.Application` et `Sirocco.Infrastructure` : sans eux, un projet externe pouvait écrire un
scénario mais pas le lancer, ce qui aurait manqué la parité avec NBomber explicitement visée.

Vérifié par un vrai tir depuis un projet xUnit **entièrement externe** (aucun `ProjectReference`
vers ce dépôt, uniquement les quatre `.nupkg` via une source NuGet locale) : `DynamicCheckoutWorkflow`
(`Sirocco.Scenarios`) exécuté à travers `AddSiroccoEngine`/`AddSiroccoMetrics`, contre
`Sirocco.SampleTarget`, avec un rapport contenant bien l'étape `checkout`.

**Limite restante** : pas encore publiés sur nuget.org (le dépôt reste privé). Les quatre paquets
s'installent aujourd'hui depuis une source locale (`dotnet pack` puis `--add-source`) ou un flux
privé, comme `Sirocco.Cli`.

