# Scénarios scriptés (Roslyn)

Le format déclaratif ci-dessus ne sait pas exprimer de branchement ni de boucle — la limite
documentée depuis l'étape 6. Décision structurante de la [roadmap
phase 2](https://github.com/coulibalyousmane/Sirocco/blob/main/ROADMAP.md#phase-2--des-scénarios-quon-peut-réellement-écrire) : plutôt que d'enrichir
indéfiniment un langage de configuration, un scénario peut désormais être un vrai script C#,
compilé à la volée par Roslyn (`Microsoft.CodeAnalysis.CSharp.Scripting`).

Un fichier `.csx`/`.cs` doit se terminer par une expression qui produit un `IWorkflow` — le plus
souvent l'instanciation d'une classe déclarée juste au-dessus, exactement comme un scénario écrit
en dur dans `Sirocco.Scenarios` :

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
sirocco run scenario.csx --target-url http://localhost:5299 --rps 50 --duration 30s
```

`System`, `System.Collections.Generic`, `System.Net.Http`, `System.Threading(.Tasks)`,
`Sirocco.Domain.Data`, `Sirocco.Domain.Execution`, `Sirocco.Domain.Metrics` et
`Sirocco.Scenarios.Data` sont importés par défaut ; un script ajoute ses propres `using` pour le
reste (`System.Text.Json.Nodes`, `System.Net.Http.Json`...). Toutes les assemblies déjà chargées
dans le processus hôte sont visibles du script sans configuration : `Sirocco.Scenarios` pour
réutiliser `DynamicCheckoutWorkflow` comme base, ou charger un [jeu de données](donnees-assertions.md#jeux-de-données)
via `DataSetLoader.LoadFromFile(...)` dans `SetUpAsync`, par exemple.

`scenarios/scripted-checkout.csx` démontre deux choses que le déclaratif ne peut pas exprimer
aussi simplement : une boucle de nouvelle tentative bornée sur `checkout` (arrêt anticipé dès
qu'il ne s'agit plus d'une 503 temporaire) ; et un jeu de données
([`scenarios/users.csv`](https://github.com/coulibalyousmane/Sirocco/blob/main/scenarios/users.csv))
chargé dans `SetUpAsync`, un identifiant réel par utilisateur virtuel plutôt que `demo`/`demo` en
dur pour tout le monde.

[!code-csharp[](../../scenarios/scripted-checkout.csx)]
*`scenarios/scripted-checkout.csx` — exécuté par la CI*

**Un script s'exécute avec la confiance totale du processus** : rien n'est sandboxé, comme un
script k6 (JavaScript) ou NBomber (C# aussi) — propriété inhérente à la décision, pas un oubli.

Vérifié par de vrais tirs : `scripted-checkout.csx` exécuté via `sirocco run`, jeton mis en cache
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

