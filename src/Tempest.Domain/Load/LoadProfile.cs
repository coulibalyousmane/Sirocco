using System.Collections.ObjectModel;

namespace Tempest.Domain.Load;

/// <summary>
/// Profil de charge complet : suite ordonnee de paliers decrivant l'evolution du debit
/// cible dans le temps.
/// <para>
/// Le moteur n'interroge jamais le debit instantane pour decider quand tirer : il
/// s'appuie sur <see cref="PlannedRequestsUpTo(double)"/>, l'integrale du debit.
/// Raisonner en "nombre cumule de requetes dues" plutot qu'en "delai entre deux tirs"
/// est ce qui empeche la derive de s'accumuler et permet de detecter le retard exact
/// de l'injecteur — la base du traitement du <i>coordinated omission</i>.
/// </para>
/// </summary>
public sealed class LoadProfile
{
    private readonly LoadStage[] _stages;

    // Bornes cumulees, precalculees : evite de reparcourir toute la liste a chaque appel.
    private readonly double[] _stageStartSeconds;
    private readonly double[] _stageStartRequests;
    private readonly double _firstEmittingSeconds;

    /// <summary>Cree un profil a partir d'une suite de paliers.</summary>
    /// <exception cref="ArgumentException">La suite est vide.</exception>
    public LoadProfile(IEnumerable<LoadStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);

        _stages = [.. stages];
        if (_stages.Length == 0)
        {
            throw new ArgumentException("Un profil de charge doit contenir au moins un palier.", nameof(stages));
        }

        _stageStartSeconds = new double[_stages.Length + 1];
        _stageStartRequests = new double[_stages.Length + 1];

        for (int i = 0; i < _stages.Length; i++)
        {
            _stageStartSeconds[i + 1] = _stageStartSeconds[i] + _stages[i].DurationSeconds;
            _stageStartRequests[i + 1] = _stageStartRequests[i] + _stages[i].TotalRequests;
        }

        Stages = new ReadOnlyCollection<LoadStage>(_stages);
        _firstEmittingSeconds = FindFirstEmittingSecond();
    }

    /// <summary>Paliers du profil, dans l'ordre d'execution.</summary>
    public IReadOnlyList<LoadStage> Stages { get; }

    /// <summary>Duree totale du test.</summary>
    public TimeSpan TotalDuration => TimeSpan.FromSeconds(TotalDurationSeconds);

    /// <summary>Duree totale du test, en secondes.</summary>
    public double TotalDurationSeconds => _stageStartSeconds[^1];

    /// <summary>Nombre theorique total de requetes sur l'ensemble du test.</summary>
    public double TotalPlannedRequests => _stageStartRequests[^1];

    /// <summary>Nombre entier de requetes que le profil planifie au total.</summary>
    public long PlannedRequestCount => (long)Math.Floor(TotalPlannedRequests);

    /// <summary>Debit maximal atteint par le profil.</summary>
    public double PeakRps
    {
        get
        {
            double peak = 0d;
            foreach (LoadStage stage in _stages)
            {
                peak = Math.Max(peak, Math.Max(stage.FromRps, stage.ToRps));
            }

            return peak;
        }
    }

    /// <summary>Profil a debit constant.</summary>
    public static LoadProfile Constant(double rps, TimeSpan duration) =>
        new([LoadStage.Constant(rps, duration)]);

    /// <summary>Profil en trois temps : montee en charge, plateau, descente.</summary>
    public static LoadProfile RampUpSustainDown(double peakRps, TimeSpan rampUp, TimeSpan sustain, TimeSpan rampDown) =>
        new([
            LoadStage.Ramp(0d, peakRps, rampUp),
            LoadStage.Constant(peakRps, sustain),
            LoadStage.Ramp(peakRps, 0d, rampDown),
        ]);

    /// <summary>Point d'entree fluide pour composer un profil palier par palier.</summary>
    public static LoadProfileBuilder Create() => new();

    /// <summary>Debit cible instantane a <paramref name="elapsed"/> du debut du test.</summary>
    public double RpsAt(TimeSpan elapsed) => RpsAt(elapsed.TotalSeconds);

    /// <summary>Debit cible instantane a <paramref name="elapsedSeconds"/> secondes du debut du test.</summary>
    public double RpsAt(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0d)
        {
            return _stages[0].FromRps;
        }

        if (elapsedSeconds >= TotalDurationSeconds)
        {
            return 0d;
        }

        int index = LastIndexNotAfter(_stageStartSeconds, elapsedSeconds);
        return _stages[index].RpsAt(elapsedSeconds - _stageStartSeconds[index]);
    }

    /// <summary>
    /// Nombre theorique de requetes qui auraient du etre emises depuis le debut du test.
    /// Fonction monotone croissante : c'est l'echeancier que le regulateur de debit
    /// tente de rattraper.
    /// </summary>
    public double PlannedRequestsUpTo(TimeSpan elapsed) => PlannedRequestsUpTo(elapsed.TotalSeconds);

    /// <inheritdoc cref="PlannedRequestsUpTo(TimeSpan)" />
    public double PlannedRequestsUpTo(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0d)
        {
            return 0d;
        }

        if (elapsedSeconds >= TotalDurationSeconds)
        {
            return TotalPlannedRequests;
        }

        int index = LastIndexNotAfter(_stageStartSeconds, elapsedSeconds);
        return _stageStartRequests[index] + _stages[index].RequestsUpTo(elapsedSeconds - _stageStartSeconds[index]);
    }

    /// <summary>
    /// Reciproque de <see cref="PlannedRequestsUpTo(double)"/> : instant theorique de depart
    /// de la requete d'index <paramref name="requestIndex"/> (base 0), en secondes depuis le
    /// debut du test.
    /// <para>
    /// C'est cette valeur — et non l'instant ou le moteur se reveille — qui est gravee dans
    /// <c>MetricResult.ScheduledTicks</c>. Sans elle, une rafale de 50 jetons emise en un seul
    /// reveil partagerait le meme horodatage et masquerait le retard reel de l'injecteur.
    /// </para>
    /// </summary>
    public double ScheduledSecondsFor(long requestIndex) => ScheduledSecondsFor((double)requestIndex);

    /// <inheritdoc cref="ScheduledSecondsFor(long)" />
    public double ScheduledSecondsFor(double requestIndex)
    {
        if (requestIndex <= 0d)
        {
            return _firstEmittingSeconds;
        }

        if (requestIndex >= TotalPlannedRequests)
        {
            return TotalDurationSeconds;
        }

        int index = LastIndexNotAfter(_stageStartRequests, requestIndex);
        return _stageStartSeconds[index] + _stages[index].SecondsForRequests(requestIndex - _stageStartRequests[index]);
    }

    /// <summary>Indique si le test est termine a l'instant donne.</summary>
    public bool IsCompleted(TimeSpan elapsed) => elapsed.TotalSeconds >= TotalDurationSeconds;

    /// <summary>
    /// Index du dernier palier dont la borne de debut ne depasse pas <paramref name="value"/>.
    /// <para>
    /// Le parcours descendant est volontaire : quand un palier a debit nul partage sa borne
    /// avec le suivant, c'est le palier <b>le plus tardif</b> qui gagne, donc celui qui emet
    /// reellement. Le nombre de paliers se compte sur les doigts d'une main, une recherche
    /// dichotomique serait plus lente que ce parcours lineaire.
    /// </para>
    /// </summary>
    private int LastIndexNotAfter(double[] stageStartBounds, double value)
    {
        for (int i = _stages.Length - 1; i >= 0; i--)
        {
            if (value >= stageStartBounds[i])
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// Instant ou le profil commence reellement a emettre. Un profil peut demarrer par un
    /// palier a debit nul ("attendre, puis frapper") : la requete d'index 0 part alors au
    /// debut du premier palier productif, pas a l'instant zero.
    /// </summary>
    private double FindFirstEmittingSecond()
    {
        for (int i = 0; i < _stages.Length; i++)
        {
            if (_stages[i].TotalRequests > 0d)
            {
                return _stageStartSeconds[i];
            }
        }

        return TotalDurationSeconds;
    }
}