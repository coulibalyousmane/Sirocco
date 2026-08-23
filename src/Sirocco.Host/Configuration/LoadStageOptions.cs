namespace Sirocco.Host.Configuration;

/// <summary>Un palier du profil de charge, tel que decrit dans <c>appsettings.json</c>.</summary>
public sealed class LoadStageOptions
{
    /// <summary>Debit cible au debut du palier, en requetes par seconde.</summary>
    public required double FromRps { get; init; }

    /// <summary>Debit cible a la fin du palier, en requetes par seconde.</summary>
    public required double ToRps { get; init; }

    /// <summary>Duree du palier, en secondes.</summary>
    public required double DurationSeconds { get; init; }
}