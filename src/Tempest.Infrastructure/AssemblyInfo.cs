using System.Runtime.CompilerServices;

// Autorise les tests a verifier directement le cablage interne (services d'activation,
// implementations concretes) plutot que de le deduire indirectement.
[assembly: InternalsVisibleTo("Tempest.UnitTests")]

// Le maitre du mode distribue reutilise MeterActivationHostedService : comme le mode autonome,
// rien d'autre ne demande TempestMeter dans son graphe de services, mais le maitre n'a pas de
// MetricsAggregator local pour passer par AddTempestMetrics — il cable son propre TempestMeter
// a la main (voir Tempest.Host/Program.cs) et n'a besoin que du declencheur d'activation.
[assembly: InternalsVisibleTo("Tempest.Host")]