using System.Runtime.CompilerServices;

// Le parseur d'arguments (CliOptions, CliDuration) reste interne : rien en dehors de Program.cs
// n'a besoin de le construire, sauf les tests, qui verifient directement le parsing plutot que
// de le deduire indirectement d'un lancement de processus complet.
[assembly: InternalsVisibleTo("Tempest.UnitTests")]