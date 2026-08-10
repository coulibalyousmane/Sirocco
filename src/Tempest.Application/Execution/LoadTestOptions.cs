namespace Tempest.Application.Execution;

/// <summary>
/// Parametres de <b>l'injecteur</b> : combien d'iterations peuvent tourner de front, quelle
/// profondeur de file, quand renoncer a un jeton trop vieux.
/// <para>
/// Ce que le tir doit produire — la courbe de charge — n'est deliberement pas ici : cela
/// appartient au <see cref="Domain.Load.LoadProfile"/> detenu par l'<see cref="ILoadScheduler"/>.
/// Un meme reglage d'injecteur sert ainsi a tous les profils, et inversement.
/// </para>
/// </summary>
public sealed class LoadTestOptions
{
    /// <summary>Valeur par defaut du plafond d'utilisateurs virtuels.</summary>
    public const int DEFAULT_MAX_VIRTUAL_USERS = 512;

    /// <summary>Profondeur minimale de la file de jetons.</summary>
    public const int MINIMUM_TOKEN_QUEUE_CAPACITY = 64;

    /// <summary>
    /// Nombre d'utilisateurs virtuels : un plafond en modele <i>ouvert</i>, un effectif exact en
    /// modele <i>ferme</i>.
    /// <para>
    /// Modele ouvert (<see cref="CoordinatedRateLimiter"/>) : le debit vise ne depend pas de la
    /// latence de la cible, mais il faut bien une borne, sinon une cible qui ralentit ferait
    /// exploser la memoire de l'injecteur. Quand le plafond est atteint, les jetons s'accumulent
    /// et la dette d'ordonnancement grimpe — c'est precisement le signal de saturation qu'on veut
    /// voir.
    /// </para>
    /// <para>
    /// Modele ferme (<see cref="ClosedModelScheduler"/>) : il n'existe pas de plafond distinct de
    /// l'effectif — le nombre de travailleurs crees par le moteur <b>est</b> le nombre
    /// d'utilisateurs virtuels simultanes du tir.
    /// </para>
    /// </summary>
    public int MaxVirtualUsers { get; init; } = DEFAULT_MAX_VIRTUAL_USERS;

    /// <summary>
    /// Capacite de la file de jetons. Par defaut, deux fois le nombre d'utilisateurs virtuels :
    /// assez pour absorber une rafale, assez peu pour que le retard se voie vite.
    /// </summary>
    public int? TokenQueueCapacity { get; init; }

    /// <summary>
    /// Au-dela de ce retard, un jeton est abandonne plutot qu'execute.
    /// <para>
    /// <see langword="null"/> (defaut) : rien n'est abandonne, l'injecteur rattrape son retard.
    /// Une valeur finie evite l'effet boule de neige ou l'injecteur, deja en retard, continue
    /// d'emettre des requetes obsoletes. Les abandons sont mesures comme
    /// <see cref="Domain.Metrics.RequestOutcome.Dropped"/> et comptent dans les percentiles.
    /// </para>
    /// </summary>
    public TimeSpan? MaxSchedulingDelay { get; init; }

    /// <summary>Capacite effective de la file de jetons.</summary>
    public int EffectiveTokenQueueCapacity =>
        TokenQueueCapacity ?? Math.Max(MaxVirtualUsers * 2, MINIMUM_TOKEN_QUEUE_CAPACITY);

    /// <summary>Valide la coherence des parametres.</summary>
    /// <exception cref="ArgumentException">Un parametre est hors domaine.</exception>
    public void Validate()
    {
        if (MaxVirtualUsers < 1)
        {
            throw new ArgumentException("MaxVirtualUsers doit valoir au moins 1.", nameof(MaxVirtualUsers));
        }

        if (TokenQueueCapacity is { } capacity && capacity < 1)
        {
            throw new ArgumentException("TokenQueueCapacity doit valoir au moins 1.", nameof(TokenQueueCapacity));
        }

        if (MaxSchedulingDelay is { } delay && delay <= TimeSpan.Zero)
        {
            throw new ArgumentException("MaxSchedulingDelay doit etre strictement positif.", nameof(MaxSchedulingDelay));
        }
    }
}