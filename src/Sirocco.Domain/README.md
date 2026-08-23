# Sirocco.Domain

Contrats du moteur de test de charge [Sirocco](https://github.com/coulibalyousmane/Sirocco).
Zéro dépendance NuGet — écrire un scénario contre ce seul paquet suffit, sans dépendre du reste
du moteur.

```csharp
using Sirocco.Domain.Execution;
using Sirocco.Domain.Metrics;

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
```

Pour dérouler ce scénario à un débit donné et obtenir un rapport, voir les paquets
`Sirocco.Application` (le moteur) et `Sirocco.Infrastructure` (la chaîne de mesure).
`Sirocco.Scenarios` contient des scénarios de référence prêts à l'emploi. Documentation complète
dans le [dépôt](https://github.com/coulibalyousmane/Sirocco#readme).
