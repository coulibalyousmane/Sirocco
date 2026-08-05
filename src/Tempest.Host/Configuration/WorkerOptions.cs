namespace Tempest.Host.Configuration;

/// <summary>
/// Reglages du role <see cref="TempestHostOptions.ROLE_WORKER"/>. Section <c>Worker</c>.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>Adresse du maitre a laquelle s'enregistrer.</summary>
    public required string MasterUrl { get; init; }

    /// <summary>
    /// Adresse a laquelle ce worker est joignable par le maitre. Ne peut pas etre devinee de
    /// l'interieur d'un process (pare-feu, conteneur, IP publique vs privee) : doit etre
    /// renseignee explicitement.
    /// </summary>
    public required string SelfUrl { get; init; }

    /// <summary>Valide la coherence des reglages.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MasterUrl))
        {
            throw new ArgumentException("MasterUrl ne peut pas etre vide.", nameof(MasterUrl));
        }

        if (string.IsNullOrWhiteSpace(SelfUrl))
        {
            throw new ArgumentException("SelfUrl ne peut pas etre vide.", nameof(SelfUrl));
        }
    }
}