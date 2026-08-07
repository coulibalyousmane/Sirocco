# Tempest.Infrastructure

Chaîne de mesure du moteur de test de charge [Tempest](https://github.com/coulibalyousmane/Tempest).
Transforme les mesures brutes de `Tempest.Application` en `LoadTestReport` (percentiles,
dette d'ordonnancement, taux d'erreur), et les expose via OpenTelemetry.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Tempest.Infrastructure.DependencyInjection;
using Tempest.Application.Metrics;
using Tempest.Domain.Metrics;

// Apres AddTempestEngine (Tempest.Application) :
services.AddTempestMetrics();

// ... apres le tir :
MetricsProcessor metricsProcessor = provider.GetRequiredService<MetricsProcessor>();
metricsProcessor.Start();
// ... engine.RunAsync(...) ...
await metricsProcessor.StopAsync();

LoadTestReport report = metricsProcessor.Aggregator.Snapshot(StatisticsScope.Cumulative);
Console.WriteLine(report.ToTable());
```

`AddTempestOpenTelemetry` branche les instruments sur un `Meter` OpenTelemetry — sans exportateur
Prometheus (dépendance ASP.NET Core que cette couche n'a aucune raison de connaître) ; ajoutez
l'exportateur de votre choix (OTLP, console...) au `MeterProviderBuilder` fourni. Documentation
complète dans le [dépôt](https://github.com/coulibalyousmane/Tempest#readme).
