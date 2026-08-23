using Sirocco.Host.Configuration;
using Sirocco.Scenarios;
using Sirocco.Scenarios.Declarative;

namespace Sirocco.Host.Distributed;

/// <summary>
/// Ordre de preparation envoye par le maitre a un worker (<c>POST /worker/prepare</c>).
/// <para>
/// Transporte tout ce qu'il faut pour reconstruire, cote worker, exactement le meme scenario
/// que celui que le maitre aurait joue seul : le <b>contenu</b> du fichier de scenario
/// declaratif (<see cref="ScenarioContent"/>) — pas son chemin, qu'un worker distant n'a aucune
/// raison de partager avec le maitre — et les reglages de chaque scenario code en dur
/// (<see cref="WebSocketEchoOptions"/>, <see cref="GrpcEchoOptions"/>,
/// <see cref="DynamicCheckoutOptions"/>).
/// </para>
/// </summary>
public sealed class WorkerPrepareRequest
{
    /// <summary>Paliers de charge deja reduits par le maitre (division par le nombre de workers).</summary>
    public required List<LoadStageOptions> Profile { get; init; }

    /// <summary>Scenario code en dur a jouer ; voir <see cref="SiroccoHostOptions.Workflow"/>.</summary>
    public required string Workflow { get; init; }

    /// <summary>Contenu du fichier de scenario declaratif, si le maitre en utilise un.</summary>
    public string? ScenarioContent { get; init; }

    /// <summary>Format de <see cref="ScenarioContent"/> ; requis des que celui-ci est renseigne.</summary>
    public ScenarioFormat? ScenarioFormat { get; init; }

    /// <summary>Reglages de <c>WebSocketEchoWorkflow</c>, si c'est le scenario choisi.</summary>
    public WebSocketEchoWorkflowOptions? WebSocketEchoOptions { get; init; }

    /// <summary>Reglages de <c>GrpcEchoWorkflow</c>/<c>GrpcStreamEchoWorkflow</c>, si c'est le scenario choisi.</summary>
    public GrpcEchoWorkflowOptions? GrpcEchoOptions { get; init; }

    /// <summary>Reglages de <c>DynamicCheckoutWorkflow</c>, si c'est le scenario choisi.</summary>
    public DynamicCheckoutWorkflowOptions? DynamicCheckoutOptions { get; init; }

    /// <summary>Adresse de base de la cible a tester.</summary>
    public required string TargetBaseUrl { get; init; }

    /// <summary>Plafond d'utilisateurs virtuels pour ce worker (deja reduit par le maitre).</summary>
    public required int MaxVirtualUsers { get; init; }
}