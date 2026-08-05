using System.Runtime.CompilerServices;

// Autorise les tests a verifier les contrats HTTP (DTOs, contexte JSON) directement, plutot
// que de reconstruire une correspondance JSON fragile a base de chaines litterales.
[assembly: InternalsVisibleTo("Tempest.UnitTests")]