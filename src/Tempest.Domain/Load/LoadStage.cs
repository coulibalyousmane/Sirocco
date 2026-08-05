namespace Tempest.Domain.Load;

/// <summary>
/// Palier d'un profil de charge : le debit evolue lineairement de
/// <see cref="FromRps"/> vers <see cref="ToRps"/> pendant <see cref="Duration"/>.
/// </summary>
public readonly record struct LoadStage
{
    private LoadStage(double fromRps, double toRps, TimeSpan duration)
    {
        FromRps = fromRps;
        ToRps = toRps;
        Duration = duration;
    }

    /// <summary>Debit cible au debut du palier, en requetes par seconde.</summary>
    public double FromRps { get; }

    /// <summary>Debit cible a la fin du palier, en requetes par seconde.</summary>
    public double ToRps { get; }

    /// <summary>Duree du palier.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Duree du palier en secondes.</summary>
    public double DurationSeconds => Duration.TotalSeconds;

    /// <summary>Indique si le debit est constant sur toute la duree du palier.</summary>
    public bool IsFlat => FromRps.Equals(ToRps);

    /// <summary>Palier a debit constant.</summary>
    public static LoadStage Constant(double rps, TimeSpan duration) => Create(rps, rps, duration);

    /// <summary>Palier a debit lineairement croissant ou decroissant.</summary>
    public static LoadStage Ramp(double fromRps, double toRps, TimeSpan duration) =>
        Create(fromRps, toRps, duration);

    private static LoadStage Create(double fromRps, double toRps, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromRps);
        ArgumentOutOfRangeException.ThrowIfNegative(toRps);

        if (double.IsNaN(fromRps) || double.IsNaN(toRps) ||
            double.IsInfinity(fromRps) || double.IsInfinity(toRps))
        {
            throw new ArgumentOutOfRangeException(nameof(fromRps), "Le debit doit etre un nombre fini.");
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "La duree d'un palier doit etre strictement positive.");
        }

        return new LoadStage(fromRps, toRps, duration);
    }

    /// <summary>Debit instantane a <paramref name="elapsedSeconds"/> secondes du debut du palier.</summary>
    public double RpsAt(double elapsedSeconds)
    {
        double clamped = Math.Clamp(elapsedSeconds, 0d, DurationSeconds);
        return IsFlat
            ? FromRps
            : FromRps + ((ToRps - FromRps) * (clamped / DurationSeconds));
    }

    /// <summary>
    /// Nombre theorique de requetes emises depuis le debut du palier : integrale du debit,
    /// soit l'aire du trapeze delimite par la rampe.
    /// </summary>
    public double RequestsUpTo(double elapsedSeconds)
    {
        double t = Math.Clamp(elapsedSeconds, 0d, DurationSeconds);
        return IsFlat
            ? FromRps * t
            : (FromRps * t) + ((ToRps - FromRps) * t * t / (2d * DurationSeconds));
    }

    /// <summary>Nombre theorique total de requetes du palier.</summary>
    public double TotalRequests => (FromRps + ToRps) / 2d * DurationSeconds;

    /// <summary>
    /// Reciproque de <see cref="RequestsUpTo"/> : instant, depuis le debut du palier,
    /// auquel la <paramref name="requests"/>-ieme requete doit partir.
    /// <para>
    /// Palier plat : <c>t = r / rps</c>. Rampe : on resout <c>k.t^2 + a.t - r = 0</c> avec
    /// <c>a = FromRps</c> et <c>k = (ToRps - FromRps) / 2d</c>. On retient la forme
    /// <c>t = 2r / (a + sqrt(a^2 + 4kr))</c> plutot que la formule quadratique classique :
    /// elle evite l'annulation catastrophique quand <c>a</c> est grand devant <c>kr</c>,
    /// et reste valable pour une rampe descendante (<c>k &lt; 0</c>).
    /// </para>
    /// </summary>
    public double SecondsForRequests(double requests)
    {
        double total = TotalRequests;
        if (total <= 0d)
        {
            // Palier a debit nul : aucune requete n'y est jamais programmee.
            return 0d;
        }

        double r = Math.Clamp(requests, 0d, total);

        if (IsFlat)
        {
            return Math.Clamp(r / FromRps, 0d, DurationSeconds);
        }

        double a = FromRps;
        double k = (ToRps - FromRps) / (2d * DurationSeconds);
        double discriminant = (a * a) + (4d * k * r);
        double denominator = a + Math.Sqrt(Math.Max(discriminant, 0d));

        // a == 0 et r == 0 : la toute premiere requete d'une rampe partant de zero.
        return denominator <= 0d
            ? 0d
            : Math.Clamp(2d * r / denominator, 0d, DurationSeconds);
    }
}