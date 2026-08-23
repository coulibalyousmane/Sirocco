# Architecture et décisions

## Architecture

Clean Architecture, dépendances dirigées vers l'intérieur :

```
Sirocco.Domain          ← aucune dépendance
   ↑          ↑
Sirocco.Application   Sirocco.Scenarios
   ↑
Sirocco.Infrastructure
   ↑
Sirocco.Host
```

| Projet | Rôle |
|---|---|
| `Sirocco.Domain` | Contrats et objets-valeurs purs : `MetricResult`, `IWorkflow`, `LoadProfile`, `SiroccoClock` |
| `Sirocco.Application` | Orchestration : `ILoadScheduler` / `CoordinatedRateLimiter`, `TargetRpsLoadEngine`, `VirtualUserWorker`, `VirtualUserContext` |
| `Sirocco.Infrastructure` | `MetricsProcessor` (consommateur du channel), `SiroccoMeter`, câblage OpenTelemetry |
| `Sirocco.Scenarios` | Parcours métier concrets (données Bogus, appels HTTP, assertions) |
| `Sirocco.Host` | Point d'entrée ASP.NET Core, endpoints `/metrics`, `/report`, `/report/live` |
| `samples/Sirocco.SampleTarget` | Cible HTTP de démonstration : latence simulée, capacité finie, jetons qui expirent |

### Flux d'exécution

1. `CoordinatedRateLimiter` déroule l'échéancier issu du `LoadProfile` et émet des jetons horodatés.
2. `TargetRpsLoadEngine` distribue ces jetons aux `VirtualUserWorker` disponibles.
3. Le `IWorkflow` exécute ses étapes via le `HttpClient` partagé et déclare leur issue.
4. Les `MetricResult` partent en non bloquant vers le `MetricsProcessor`, qui agrège et publie.

### Câblage par injection de dépendances

```csharp
services.AddHttpClient();
services.AddSingleton<IWorkflow, DynamicCheckoutWorkflow>();

services.AddSiroccoEngine(
    LoadProfile.Create()
        .RampTo(5_000, TimeSpan.FromSeconds(30))
        .Sustain(TimeSpan.FromMinutes(5))
        .Build(),
    new LoadTestOptions { MaxVirtualUsers = 1_024 });
```

Tous les enregistrements passent par `TryAdd` : déclarer son propre `ILoadScheduler` ou
`IMetricSink` avant l'appel suffit à le substituer, sans renoncer au reste du câblage.

## Conventions de code

Le [`.editorconfig`](https://github.com/coulibalyousmane/Sirocco/blob/main/.editorconfig) à la racine fait autorité et est appliqué à la compilation
(`EnforceCodeStyleInBuild` + `TreatWarningsAsErrors`) :

- constantes en `UPPER_CASE`, champs non publics en `_camelCase` — en `severity = warning`,
  donc **bloquants au build** ;
- types explicites lorsque le type est apparent, `var` seulement quand il ne l'est pas ;
- constructeurs primaires, `namespace` file-scoped, accolades Allman, UTF-8 BOM sans
  newline finale ;
- aucune valeur magique : codes de statut, seuils et capacités sont des constantes nommées.

```bash
dotnet format Sirocco.sln --verify-no-changes --severity info
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
- **Sirocco n'a aucune dépendance à un exportateur.** Il alimente les instruments
  `System.Diagnostics.Metrics` de la BCL ; OpenTelemetry, Prometheus ou un simple `MeterListener`
  viennent les écouter.
- **Le panier de `DynamicCheckoutWorkflow` ne coordonne rien entre deux processus.** Il se
  construit à partir de la réponse *réelle* de l'étape `browse`, jamais d'un pool de produits
  pré-généré côté client. Deux projets indépendants (scénario, cible) qui s'accorderaient sur
  des identifiants à l'avance seraient fragiles au moindre changement de l'un des deux.
- **Un singleton enregistré mais jamais résolu ne se construit jamais.** `MetricsAggregator` et
  `SiroccoMeter` en ont chacun fait les frais lors du premier tir réel (détails ci-dessous) :
  un conteneur d'injection de dépendances ne garantit ni un ordre de construction entre
  singletons indépendants, ni qu'un service sans consommateur direct soit jamais instancié.

