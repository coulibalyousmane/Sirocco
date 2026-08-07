# Tempest.Application

Moteur d'injection de charge du moteur de test de charge
[Tempest](https://github.com/coulibalyousmane/Tempest). Déroule un `IWorkflow`
(`Tempest.Domain`) à un débit cible, en corrigeant le *coordinated omission* : chaque mesure
porte son instant de départ théorique, imposé par le profil de charge, pas l'instant où
l'utilisateur virtuel a réellement pu démarrer.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Tempest.Application.DependencyInjection;
using Tempest.Application.Execution;
using Tempest.Domain.Load;

IServiceCollection services = new ServiceCollection();
services.AddSingleton(new HttpClient { BaseAddress = new Uri("https://votre-cible") });
services.AddSingleton<Tempest.Domain.Execution.IWorkflow>(new PingWorkflow());

LoadProfile profile = new([LoadStage.Ramp(fromRps: 0, toRps: 50, TimeSpan.FromSeconds(30))]);
services.AddTempestEngine(profile, new LoadTestOptions { MaxVirtualUsers = 100 });

ServiceProvider provider = services.BuildServiceProvider();
TargetRpsLoadEngine engine = provider.GetRequiredService<TargetRpsLoadEngine>();
LoadTestSummary summary = await engine.RunAsync(CancellationToken.None);
```

`AddTempestEngine` ne câble ni le `HttpClient` ni le `IWorkflow` : ils dépendent de l'appelant.
Pour transformer les mesures en `LoadTestReport` (percentiles, dette d'ordonnancement, export
OpenTelemetry), ajoutez `Tempest.Infrastructure`. Documentation complète dans le
[dépôt](https://github.com/coulibalyousmane/Tempest#readme).
