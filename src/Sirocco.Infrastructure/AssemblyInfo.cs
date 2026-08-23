using System.Runtime.CompilerServices;

// Autorise les tests a verifier directement le cablage interne (services d'activation,
// implementations concretes) plutot que de le deduire indirectement.
[assembly: InternalsVisibleTo("Sirocco.UnitTests")]

// Le maitre du mode distribue reutilise MeterActivationHostedService : comme le mode autonome,
// rien d'autre ne demande SiroccoMeter dans son graphe de services, mais le maitre n'a pas de
// MetricsAggregator local pour passer par AddSiroccoMetrics — il cable son propre SiroccoMeter
// a la main (voir Sirocco.Host/Program.cs) et n'a besoin que du declencheur d'activation.
[assembly: InternalsVisibleTo("Sirocco.Host")]