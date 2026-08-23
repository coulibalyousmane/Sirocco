namespace Sirocco.Domain.Metrics;

/// <summary>
/// Rapport d'un scenario au sein d'un tir a scenarios concurrents : son nom, ses statistiques
/// propres (<see cref="Report"/>, avec ses etiquettes et sa mise en garde modele ferme a lui) et
/// le verdict de ses propres seuils — jamais ceux d'un autre scenario du meme tir.
/// </summary>
public sealed record ScenarioReport
{
    /// <summary>Nom du scenario, tel que declare dans <c>Sirocco:Scenarios</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Statistiques de ce scenario, isolees des autres scenarios du meme tir.</summary>
    public required LoadTestReport Report { get; init; }

    /// <summary>Verdict des seuils propres a ce scenario.</summary>
    public required ThresholdReport Thresholds { get; init; }
}