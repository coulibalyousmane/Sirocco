using Tempest.Host.Configuration;

namespace Tempest.Host.Distributed;

/// <summary>
/// Ordre de preparation envoye par le maitre a un worker (<c>POST /worker/prepare</c>).
/// <para>
/// Ne transporte que <see cref="Workflow"/>/<see cref="ScenarioFile"/> — pas les sections de
/// configuration specifiques a un scenario (<c>WebSocketEcho</c>, <c>GrpcEcho</c>,
/// <c>DynamicCheckout</c>) : limite volontaire de cette premiere version, un worker construit
/// son scenario avec les reglages par defaut. <see cref="Tempest.Scenarios"/> ecrits a la main
/// avec des options non standard restent, pour l'instant, a tirer en mode autonome.
/// </para>
/// </summary>
public sealed class WorkerPrepareRequest
{
    /// <summary>Paliers de charge deja reduits par le maitre (division par le nombre de workers).</summary>
    public required List<LoadStageOptions> Profile { get; init; }

    /// <summary>Scenario code en dur a jouer ; voir <see cref="TempestHostOptions.Workflow"/>.</summary>
    public required string Workflow { get; init; }

    /// <summary>Fichier de scenario declaratif, si le maitre en utilise un.</summary>
    public string? ScenarioFile { get; init; }

    /// <summary>Adresse de base de la cible a tester.</summary>
    public required string TargetBaseUrl { get; init; }

    /// <summary>Plafond d'utilisateurs virtuels pour ce worker (deja reduit par le maitre).</summary>
    public required int MaxVirtualUsers { get; init; }
}