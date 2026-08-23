# Sirocco.Infrastructure

Chaîne de mesure du moteur de test de charge [Sirocco](https://github.com/coulibalyousmane/Sirocco).
Transforme les mesures brutes de `Sirocco.Application` en `LoadTestReport` (percentiles,
dette d'ordonnancement, taux d'erreur), et les expose via OpenTelemetry.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Sirocco.Infrastructure.DependencyInjection;
using Sirocco.Application.Metrics;
using Sirocco.Domain.Metrics;

// Apres AddSiroccoEngine (Sirocco.Application) :
services.AddSiroccoMetrics();

// ... apres le tir :
MetricsProcessor metricsProcessor = provider.GetRequiredService<MetricsProcessor>();
metricsProcessor.Start();
// ... engine.RunAsync(...) ...
await metricsProcessor.StopAsync();

LoadTestReport report = metricsProcessor.Aggregator.Snapshot(StatisticsScope.Cumulative);
Console.WriteLine(report.ToTable());
```

`AddSiroccoOpenTelemetry` branche les instruments sur un `Meter` OpenTelemetry — sans exportateur
Prometheus (dépendance ASP.NET Core que cette couche n'a aucune raison de connaître) ; ajoutez
l'exportateur de votre choix (OTLP, console...) au `MeterProviderBuilder` fourni. Documentation
complète dans le [dépôt](https://github.com/coulibalyousmane/Sirocco#readme).
