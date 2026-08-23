namespace Sirocco.Application.Metrics;

/// <summary>Reglages de la fenetre glissante de l'agregateur.</summary>
public sealed class MetricsAggregatorOptions
{
    /// <summary>Duree par defaut de la fenetre glissante, en secondes.</summary>
    public const int DEFAULT_WINDOW_SECONDS = 10;

    /// <summary>Nombre de paniers temporels par defaut dans la fenetre.</summary>
    public const int DEFAULT_WINDOW_BUCKET_COUNT = 10;

    /// <summary>
    /// Duree totale couverte par la fenetre glissante.
    /// <para>
    /// Trop courte, les centiles hauts deviennent instables faute d'echantillons ; trop
    /// longue, elle reagit avec retard a une degradation. Dix secondes est un compromis
    /// aligne sur l'intervalle de collecte usuel de Prometheus.
    /// </para>
    /// </summary>
    public TimeSpan WindowDuration { get; init; } = TimeSpan.FromSeconds(DEFAULT_WINDOW_SECONDS);

    /// <summary>
    /// Nombre de paniers temporels composant la fenetre. La fenetre avance par pas d'un
    /// panier : plus il y en a, plus le glissement est fluide, au prix de la memoire.
    /// </summary>
    public int WindowBucketCount { get; init; } = DEFAULT_WINDOW_BUCKET_COUNT;

    /// <summary>Duree d'un panier temporel.</summary>
    public TimeSpan BucketDuration => WindowDuration / WindowBucketCount;

    /// <summary>Valide la coherence des reglages.</summary>
    /// <exception cref="ArgumentException">Un reglage est hors domaine.</exception>
    public void Validate()
    {
        if (WindowDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException("WindowDuration doit etre strictement positive.", nameof(WindowDuration));
        }

        if (WindowBucketCount < 1)
        {
            throw new ArgumentException("WindowBucketCount doit valoir au moins 1.", nameof(WindowBucketCount));
        }
    }
}