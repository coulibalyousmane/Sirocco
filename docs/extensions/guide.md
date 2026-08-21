# Guide d'écriture d'extension

Dernier bullet de la phase 6 : sans documentation, un modèle de plugin reste théorique. Les quatre
protocoles de référence ci-dessus (SQL, SSE, MQTT, GraphQL) et `samples/Tempest.SamplePlugin` sont
déjà des preuves réelles du contrat — ce guide en extrait la recette pour écrire la cinquième
extension, pas encore écrite par ce dépôt.

## Quand une extension plutôt qu'un scénario scripté

Un scénario [scripté](../scenarios/scripte.md#scénarios-scriptés-roslyn) (`.csx`/`.cs`) ou [déclaratif](../scenarios/declaratif.md#configuration-déclarative)
(`.yaml`/`.json`) suffit tant que le trafic reste HTTP à travers `IVirtualUserContext.HttpClient` —
c'est le cas le plus courant, et il ne demande aucune compilation séparée. Une extension devient
nécessaire dans deux cas, pas plus : le protocole n'est **pas** HTTP (SQL, MQTT — une bibliothèque
cliente tierce remplace le client HTTP partagé), ou le scénario doit être distribué comme un
artefact compilé indépendant de ce dépôt (un paquet NuGet interne, par exemple), plutôt que comme du
code source lisible. SSE et GraphQL restent au-dessus de HTTP : ils existent comme extensions
uniquement pour prouver qu'un *usage* différent du client partagé tient aussi dans le contrat, pas
parce que HTTP y était impossible autrement.

## Le contrat minimal

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
2. **`RegisterMetrics`** — une seule fois, juste après, pour une [métrique personnalisée](../scenarios/donnees-assertions.md#métriques-personnalisées)
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

## Étape par étape : premier plugin

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
plugin](contrat.md#contrat-de-plugin). Convention reprise des quatre protocoles de référence :

```csharp
private readonly string _path = Environment.GetEnvironmentVariable("MON_PLUGIN_PATH") is { Length: > 0 } configured
    ? configured
    : "/api/catalog/products";
```

## Compiler et charger

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

## Distribuer via NuGet

Une fois le plugin validé en local, `dotnet pack` puis [résolution NuGet](contrat.md#résolution-nuget) évite
d'avoir à distribuer un chemin de fichier :

```bash
dotnet pack MonPlugin -o ./local-feed
tempest run --plugin-package MonPlugin --plugin-source ./local-feed --target-url http://localhost:5281 --rps 20 --duration 30s
```

Limite assumée : **aucune dépendance transitive du paquet n'est résolue**, seule la bibliothèque du
plugin lui-même est extraite. Une extension qui dépend d'autre chose que `Tempest.Domain` doit
publier une assembly qui embarque déjà ses dépendances, ou accepter que le chargement échoue.

## Tester une extension

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

## Limites actuelles

- **Aucune configuration injectée** dans le type instancié — variable d'environnement ou fichier
  dédié, jamais de section `appsettings.json` liée automatiquement.
- **Aucune résolution de dépendances transitives** pour un plugin résolu par paquet NuGet.
- **Mode distribué non pris en charge** — comme pour un scénario scripté, `WorkerCoordinator` ne
  sait construire qu'un `DeclarativeWorkflow` à partir du contenu propagé aux workers.
- **Dépendances natives** : à la charge du plugin lui-même (`NativeLibrary.SetDllImportResolver`),
  jamais du contrat.

## Les quatre protocoles de référence, comme exemples travaillés

| Protocole | Facette validée |
|---|---|
| [SQL](contrat.md#sql) | Un protocole réellement différent de HTTP, avec une dépendance native à résoudre soi-même |
| [SSE](contrat.md#sse) | Un usage différent du client HTTP partagé (flux continu), zéro dépendance NuGet supplémentaire |
| [MQTT](contrat.md#mqtt) | Un protocole différent orienté publication/abonnement, dépendance managée sans composant natif |
| [GraphQL](contrat.md#graphql) | Un autre usage HTTP où succès/échec se lit dans le corps de la réponse, pas dans le code de statut |

Vérifié en suivant ce guide à la lettre, depuis un dossier vide : un plugin minimal
(`dotnet new classlib`, la même forme que l'exemple ci-dessus) construit par un simple `dotnet build`
puis chargé par `tempest run` contre `Tempest.SampleTarget` réellement démarré — sélection
automatique du seul type disponible, puis `--plugin-type` explicite — les deux à 0 % d'échec.
Dossier jetable, jamais commité.

