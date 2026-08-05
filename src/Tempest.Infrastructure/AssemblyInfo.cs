using System.Runtime.CompilerServices;

// Autorise les tests a verifier directement le cablage interne (services d'activation,
// implementations concretes) plutot que de le deduire indirectement.
[assembly: InternalsVisibleTo("Tempest.UnitTests")]