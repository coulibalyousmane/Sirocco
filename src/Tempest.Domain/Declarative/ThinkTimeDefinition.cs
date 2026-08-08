namespace Tempest.Domain.Declarative;

/// <summary>
/// Pause apres une etape, avant la suivante — un temps de reflexion utilisateur, pas une
/// requete : elle n'est jamais mesuree comme latence d'etape (voir
/// <see cref="Metrics.LoadTestReport"/>), seulement comme un delai entre deux mesures.
/// <para>
/// Une duree fixe si <see cref="Max"/> est absent, un tirage uniforme entre <see cref="Min"/> et
/// <see cref="Max"/> sinon (voir <see cref="Sample"/>) — le meme choix que <c>sleep(min, max)</c>
/// en k6 ou <c>pause(min, max)</c> en Gatling : un parcours utilisateur reel ne s'arrete jamais
/// exactement la meme duree deux fois de suite.
/// </para>
/// </summary>
public sealed record ThinkTimeDefinition
{
    /// <summary>Duree minimale de la pause (duree exacte si <see cref="Max"/> est absent).</summary>
    public required TimeSpan Min { get; init; }

    /// <summary>Duree maximale de la pause ; <see langword="null"/> pour une duree fixe.</summary>
    public TimeSpan? Max { get; init; }

    /// <summary>Valide la coherence des bornes.</summary>
    /// <param name="stepName">Nom de l'etape porteuse, pour un message d'erreur exploitable.</param>
    /// <exception cref="ArgumentException">Les bornes sont incoherentes.</exception>
    public void Validate(string stepName)
    {
        if (Min < TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"Le temps de reflexion de l'etape '{stepName}' ne peut pas etre negatif ({Min}).",
                nameof(stepName));
        }

        if (Max is { } max && max < Min)
        {
            throw new ArgumentException(
                $"Le temps de reflexion de l'etape '{stepName}' a un maximum ({max}) inferieur a son minimum ({Min}).",
                nameof(stepName));
        }
    }

    /// <summary>Tire une duree de pause : <see cref="Min"/> exactement, ou une valeur uniforme dans <c>[Min, Max]</c>.</summary>
    public TimeSpan Sample() => Max is { } max && max > Min
        ? Min + ((max - Min) * Random.Shared.NextDouble())
        : Min;
}