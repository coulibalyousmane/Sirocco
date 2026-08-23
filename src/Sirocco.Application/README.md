# Sirocco.Application

Moteur d'injection de charge du moteur de test de charge
[Sirocco](https://github.com/coulibalyousmane/Sirocco). Déroule un `IWorkflow`
(`Sirocco.Domain`) à un débit cible, en corrigeant le *coordinated omission* : chaque mesure
porte son instant de départ théorique, imposé par le profil de charge, pas l'instant où
l'utilisateur virtuel a réellement pu démarrer.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Sirocco.Application.DependencyInjection;
using Sirocco.Application.Execution;
using Sirocco.Domain.Load;

IServiceCollection services = new ServiceCollection();
services.AddSingleton(new HttpClient { BaseAddress = new Uri("https://votre-cible") });
services.AddSingleton<Sirocco.Domain.Execution.IWorkflow>(new PingWorkflow());

LoadProfile profile = new([LoadStage.Ramp(fromRps: 0, toRps: 50, TimeSpan.FromSeconds(30))]);
services.AddSiroccoEngine(profile, new LoadTestOptions { MaxVirtualUsers = 100 });

ServiceProvider provider = services.BuildServiceProvider();
TargetRpsLoadEngine engine = provider.GetRequiredService<TargetRpsLoadEngine>();
LoadTestSummary summary = await engine.RunAsync(CancellationToken.None);
```

`AddSiroccoEngine` ne câble ni le `HttpClient` ni le `IWorkflow` : ils dépendent de l'appelant.
Pour transformer les mesures en `LoadTestReport` (percentiles, dette d'ordonnancement, export
OpenTelemetry), ajoutez `Sirocco.Infrastructure`. Documentation complète dans le
[dépôt](https://github.com/coulibalyousmane/Sirocco#readme).
