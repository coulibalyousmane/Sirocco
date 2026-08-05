namespace Tempest.Host.Configuration;

/// <summary>
/// Reglages du role <see cref="TempestHostOptions.ROLE_MASTER"/>. Section <c>Master</c>.
/// </summary>
public sealed class MasterOptions
{
    /// <summary>Duree par defaut de la fenetre d'enregistrement des workers, en secondes.</summary>
    public const int DEFAULT_REGISTRATION_TIMEOUT_SECONDS = 30;

    /// <summary>
    /// Nombre de workers attendus. Des que ce nombre s'est enregistre, le maitre distribue le
    /// tir sans attendre la fin de la fenetre d'enregistrement.
    /// </summary>
    public required int ExpectedWorkers { get; init; }

    /// <summary>
    /// Fenetre d'enregistrement : passe ce delai, le maitre distribue le tir aux workers deja
    /// enregistres (au moins un), plutot que d'attendre indefiniment un worker qui ne viendra
    /// jamais.
    /// </summary>
    public int RegistrationTimeoutSeconds { get; init; } = DEFAULT_REGISTRATION_TIMEOUT_SECONDS;

    /// <summary>Intervalle par defaut de sondage du tableau de bord distribue, en secondes.</summary>
    public const int DEFAULT_LIVE_POLL_INTERVAL_SECONDS = 2;

    /// <summary>
    /// Frequence a laquelle le maitre sonde <c>/worker/report/raw</c> sur chaque worker pour
    /// rafraichir <c>/report/live</c> pendant le tir. Ne remplace pas le rapport final — celui-la
    /// reste construit une seule fois, a partir des rapports pousses par les workers a la fin de
    /// leur tir local (voir <see cref="TempestHostOptions"/>), pas d'un sondage approximatif.
    /// </summary>
    public int LivePollIntervalSeconds { get; init; } = DEFAULT_LIVE_POLL_INTERVAL_SECONDS;

    /// <summary>Valide la coherence des reglages.</summary>
    public void Validate()
    {
        if (ExpectedWorkers < 1)
        {
            throw new ArgumentException("ExpectedWorkers doit valoir au moins 1.", nameof(ExpectedWorkers));
        }

        if (RegistrationTimeoutSeconds < 1)
        {
            throw new ArgumentException("RegistrationTimeoutSeconds doit valoir au moins 1.", nameof(RegistrationTimeoutSeconds));
        }

        if (LivePollIntervalSeconds < 1)
        {
            throw new ArgumentException("LivePollIntervalSeconds doit valoir au moins 1.", nameof(LivePollIntervalSeconds));
        }
    }
}