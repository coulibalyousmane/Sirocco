namespace Sirocco.Host.Configuration;

/// <summary>
/// Reglages du role <see cref="SiroccoHostOptions.ROLE_WORKER"/>. Section <c>Worker</c>.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>Valeur par defaut de <see cref="HeartbeatIntervalSeconds"/>.</summary>
    public const int DEFAULT_HEARTBEAT_INTERVAL_SECONDS = 5;

    /// <summary>Adresse du maitre a laquelle s'enregistrer.</summary>
    public required string MasterUrl { get; init; }

    /// <summary>
    /// Adresse a laquelle ce worker est joignable par le maitre. Ne peut pas etre devinee de
    /// l'interieur d'un process (pare-feu, conteneur, IP publique vs privee) : doit etre
    /// renseignee explicitement.
    /// </summary>
    public required string SelfUrl { get; init; }

    /// <summary>
    /// Frequence a laquelle ce worker signale au maitre qu'il est vivant
    /// (<c>POST /master/heartbeat</c>), une fois enregistre et jusqu'a l'arret du process — voir
    /// <see cref="Sirocco.Host.Distributed.WorkerLivenessHostedService"/>. C'est ce signal qui permet au
    /// maitre de detecter un worker perdu en cours de tir (<see cref="MasterOptions.WorkerDeadAfterSeconds"/>)
    /// plutot que d'attendre indefiniment son rapport final.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; init; } = DEFAULT_HEARTBEAT_INTERVAL_SECONDS;

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

        if (HeartbeatIntervalSeconds < 1)
        {
            throw new ArgumentException("HeartbeatIntervalSeconds doit valoir au moins 1.", nameof(HeartbeatIntervalSeconds));
        }
    }
}