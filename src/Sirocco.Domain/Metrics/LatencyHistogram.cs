using System.Numerics;

namespace Sirocco.Domain.Metrics;

/// <summary>
/// Histogramme de latences a resolution logarithmique, dans l'esprit de HdrHistogram.
/// <para>
/// <b>Pourquoi pas une simple liste de valeurs.</b> Conserver chaque mesure pour trier a la fin
/// coute 8 octets par requete : a 100 000 RPS pendant dix minutes, cela fait 480 Mo et un tri
/// de 60 millions d'elements. Ici l'empreinte est <b>constante</b> (3 072 paniers, 24 Ko) et
/// l'enregistrement se fait en temps constant, sans allocation ni tri.
/// </para>
/// <para>
/// <b>Le decoupage.</b> Sous 128 microsecondes, un panier par microseconde : exact. Au-dela,
/// chaque octave (puissance de deux) est decoupee en 128 paniers de largeur egale. L'erreur
/// relative reste donc bornee par 1/128, soit moins de 0,8 % — trois fois mieux que ce que
/// demande un SLO exprime a la milliseconde pres.
/// </para>
/// <para>
/// Cette classe n'est <b>pas</b> sure vis-a-vis des threads : elle est concue pour un
/// consommateur unique. La synchronisation appartient a l'agregateur qui la detient.
/// </para>
/// </summary>
public sealed class LatencyHistogram
{
    /// <summary>Nombre de bits de precision : 2^7 = 128 paniers par octave.</summary>
    public const int PRECISION_BITS = 7;

    /// <summary>Erreur relative maximale sur une valeur rapportee.</summary>
    public const double RELATIVE_ERROR = 1d / SUB_BUCKET_COUNT;

    private const int SUB_BUCKET_COUNT = 1 << PRECISION_BITS;
    private const int SUB_BUCKET_MASK = SUB_BUCKET_COUNT - 1;

    /// <summary>Octave la plus haute suivie : 2^29 microseconde, soit environ 9 minutes.</summary>
    private const int MAX_OCTAVE = 29;

    private const int GROUP_COUNT = MAX_OCTAVE - PRECISION_BITS + 2;
    private const int BUCKET_COUNT = GROUP_COUNT * SUB_BUCKET_COUNT;

    /// <summary>
    /// Borne haute du dernier panier, en microsecondes (environ 17,9 minutes). Au-dela, la
    /// mesure est rangee dans ce dernier panier : elle reste comptee et son maximum reel
    /// reste connu, mais sa position dans les centiles est plafonnee.
    /// </summary>
    public const long MAX_TRACKABLE_MICROSECONDS = ((long)(SUB_BUCKET_COUNT * 2) << (MAX_OCTAVE - PRECISION_BITS)) - 1L;

    private static readonly double[] _percentileRanks = [0.50d, 0.75d, 0.90d, 0.95d, 0.99d, 0.999d];

    private readonly long[] _counts = new long[BUCKET_COUNT];

    private long _totalCount;
    private long _sumMicroseconds;
    private long _minMicroseconds = long.MaxValue;
    private long _maxMicroseconds;

    /// <summary>Nombre de paniers, donc de cellules du tableau interne.</summary>
    public static int BucketCount => BUCKET_COUNT;

    /// <summary>Nombre de mesures enregistrees.</summary>
    public long Count => _totalCount;

    /// <summary>Indique qu'aucune mesure n'a ete enregistree.</summary>
    public bool IsEmpty => _totalCount == 0;

    /// <summary>Enregistre une mesure. Temps constant, aucune allocation.</summary>
    /// <param name="microseconds">Duree mesuree ; une valeur negative est ramenee a zero.</param>
    public void Record(long microseconds)
    {
        long value = microseconds > 0L ? microseconds : 0L;

        _counts[IndexOf(value)]++;
        _totalCount++;
        _sumMicroseconds += value;

        if (value < _minMicroseconds)
        {
            _minMicroseconds = value;
        }

        if (value > _maxMicroseconds)
        {
            _maxMicroseconds = value;
        }
    }

    /// <summary>
    /// Fusionne un autre histogramme dans celui-ci.
    /// <para>
    /// C'est cette operation qui rend possible d'exposer a la fois des percentiles cumules et
    /// des percentiles glissants sans dupliquer l'enregistrement : une fenetre n'est qu'une
    /// somme de paniers temporels.
    /// </para>
    /// </summary>
    public void Add(LatencyHistogram other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other._totalCount == 0L)
        {
            return;
        }

        for (int i = 0; i < _counts.Length; i++)
        {
            _counts[i] += other._counts[i];
        }

        _totalCount += other._totalCount;
        _sumMicroseconds += other._sumMicroseconds;
        _minMicroseconds = Math.Min(_minMicroseconds, other._minMicroseconds);
        _maxMicroseconds = Math.Max(_maxMicroseconds, other._maxMicroseconds);
    }

    /// <summary>
    /// Fusionne un etat brut exporte par un autre process (mode distribue Master/Workers).
    /// <para>
    /// Duplique volontairement le corps de <see cref="Add(LatencyHistogram)"/> plutot que de le
    /// reutiliser via <see cref="Export"/> : cette derniere alloue une copie du tableau de
    /// paniers, un cout acceptable une fois par tir distribue, mais que <see cref="Add(LatencyHistogram)"/>
    /// ne doit pas payer — elle tourne a chaque photographie de la fenetre glissante.
    /// </para>
    /// </summary>
    public void Add(HistogramSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.TotalCount == 0L)
        {
            return;
        }

        long[] buckets = snapshot.Buckets;
        for (int i = 0; i < _counts.Length; i++)
        {
            _counts[i] += buckets[i];
        }

        _totalCount += snapshot.TotalCount;
        _sumMicroseconds += snapshot.SumMicroseconds;
        _minMicroseconds = Math.Min(_minMicroseconds, snapshot.MinMicroseconds);
        _maxMicroseconds = Math.Max(_maxMicroseconds, snapshot.MaxMicroseconds);
    }

    /// <summary>
    /// Exporte l'etat brut : les paniers eux-memes, pas des centiles deja calcules — c'est ce
    /// qui rend une fusion ulterieure exacte (voir <see cref="HistogramSnapshot"/>).
    /// </summary>
    public HistogramSnapshot Export() => new(
        (long[])_counts.Clone(),
        _totalCount,
        _sumMicroseconds,
        _minMicroseconds,
        _maxMicroseconds);

    /// <summary>Vide l'histogramme pour le reutiliser, sans reallouer son tableau.</summary>
    public void Reset()
    {
        Array.Clear(_counts);
        _totalCount = 0L;
        _sumMicroseconds = 0L;
        _minMicroseconds = long.MaxValue;
        _maxMicroseconds = 0L;
    }

    /// <summary>
    /// Calcule tous les centiles en <b>un seul</b> parcours des paniers.
    /// <para>
    /// Les valeurs rapportees sont les bornes <i>hautes</i> des paniers : un centile n'est
    /// donc jamais sous-estime. Pour une verification de SLO, se tromper par exces est la
    /// seule erreur acceptable.
    /// </para>
    /// </summary>
    public LatencySnapshot Snapshot()
    {
        if (_totalCount == 0L)
        {
            return LatencySnapshot.Empty;
        }

        Span<long> targets = stackalloc long[_percentileRanks.Length];
        Span<long> values = stackalloc long[_percentileRanks.Length];

        for (int i = 0; i < targets.Length; i++)
        {
            targets[i] = Math.Max(1L, (long)Math.Ceiling(_percentileRanks[i] * _totalCount));
        }

        int next = 0;
        long cumulative = 0L;

        for (int index = 0; index < _counts.Length && next < targets.Length; index++)
        {
            if (_counts[index] == 0L)
            {
                continue;
            }

            cumulative += _counts[index];

            // Borne haute du panier, mais jamais au-dela du maximum reellement observe :
            // sans ce plafond, un p99,9 pourrait depasser le maximum du meme rapport.
            // Le plafonnement reste sur : le maximum majore toutes les valeurs mesurees,
            // donc le centile rapporte ne peut toujours pas sous-estimer le vrai.
            long reported = Math.Min(UpperBoundOf(index), _maxMicroseconds);

            while (next < targets.Length && cumulative >= targets[next])
            {
                values[next] = reported;
                next++;
            }
        }

        while (next < values.Length)
        {
            values[next] = _maxMicroseconds;
            next++;
        }

        return new LatencySnapshot(
            _totalCount,
            _minMicroseconds,
            _maxMicroseconds,
            _sumMicroseconds / (double)_totalCount,
            values[0],
            values[1],
            values[2],
            values[3],
            values[4],
            values[5]);
    }

    /// <summary>
    /// Panier auquel appartient une valeur.
    /// <para>
    /// Sous <see cref="SUB_BUCKET_COUNT"/>, l'index <i>est</i> la valeur : la zone basse est
    /// lineaire et exacte. Au-dela, l'octave donne le groupe et les bits de poids fort
    /// suivants donnent la position dans ce groupe.
    /// </para>
    /// </summary>
    private static int IndexOf(long microseconds)
    {
        if (microseconds < SUB_BUCKET_COUNT)
        {
            return (int)microseconds;
        }

        if (microseconds >= MAX_TRACKABLE_MICROSECONDS)
        {
            return BUCKET_COUNT - 1;
        }

        int octave = (sizeof(long) * 8) - 1 - BitOperations.LeadingZeroCount((ulong)microseconds);
        int shift = octave - PRECISION_BITS;
        int subBucket = (int)(microseconds >> shift) - SUB_BUCKET_COUNT;

        return ((shift + 1) << PRECISION_BITS) + subBucket;
    }

    /// <summary>
    /// Borne haute incluse du panier d'index donne — publique pour permettre a un rendu
    /// (histogramme du rapport) de savoir a quelle duree correspond chaque panier de
    /// <see cref="HistogramSnapshot.Buckets"/>, sans dupliquer ce calcul.
    /// </summary>
    public static long UpperBoundOf(int index)
    {
        if (index < SUB_BUCKET_COUNT)
        {
            return index;
        }

        int shift = (index >> PRECISION_BITS) - 1;
        int subBucket = index & SUB_BUCKET_MASK;
        long lowerBound = ((long)SUB_BUCKET_COUNT + subBucket) << shift;

        return lowerBound + (1L << shift) - 1L;
    }
}