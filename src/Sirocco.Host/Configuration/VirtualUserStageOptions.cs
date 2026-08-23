namespace Sirocco.Host.Configuration;

/// <summary>Un palier du profil de montee d'utilisateurs, tel que decrit dans <c>appsettings.json</c>.</summary>
public sealed class VirtualUserStageOptions
{
    /// <summary>Effectif concurrent au debut du palier.</summary>
    public required int FromVus { get; init; }

    /// <summary>Effectif concurrent a la fin du palier.</summary>
    public required int ToVus { get; init; }

    /// <summary>Duree du palier, en secondes.</summary>
    public required double DurationSeconds { get; init; }
}