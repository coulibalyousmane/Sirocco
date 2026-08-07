# Tempest.Domain

Contrats du moteur de test de charge [Tempest](https://github.com/coulibalyousmane/Tempest).
Zéro dépendance NuGet — écrire un scénario contre ce seul paquet suffit, sans dépendre du reste
du moteur.

```csharp
using Tempest.Domain.Execution;
using Tempest.Domain.Metrics;

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
`Tempest.Application` (le moteur) et `Tempest.Infrastructure` (la chaîne de mesure).
`Tempest.Scenarios` contient des scénarios de référence prêts à l'emploi. Documentation complète
dans le [dépôt](https://github.com/coulibalyousmane/Tempest#readme).
