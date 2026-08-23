namespace Sirocco.Domain.Load;

/// <summary>
/// Palier d'un profil d'utilisateurs virtuels : l'effectif concurrent evolue lineairement de
/// <see cref="FromVus"/> vers <see cref="ToVus"/> pendant <see cref="Duration"/>.
/// <para>
/// Le pendant, pour le modele ferme, de <see cref="LoadStage"/> (modele ouvert) : la meme
/// rampe lineaire, mais appliquee a un effectif d'utilisateurs virtuels plutot qu'a un debit.
/// </para>
/// </summary>
public readonly record struct VirtualUserStage
{
    private VirtualUserStage(int fromVus, int toVus, TimeSpan duration)
    {
        FromVus = fromVus;
        ToVus = toVus;
        Duration = duration;
    }

    /// <summary>Effectif concurrent au debut du palier.</summary>
    public int FromVus { get; }

    /// <summary>Effectif concurrent a la fin du palier.</summary>
    public int ToVus { get; }

    /// <summary>Duree du palier.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Duree du palier en secondes.</summary>
    public double DurationSeconds => Duration.TotalSeconds;

    /// <summary>Indique si l'effectif est constant sur toute la duree du palier.</summary>
    public bool IsFlat => FromVus == ToVus;

    /// <summary>Palier a effectif constant.</summary>
    public static VirtualUserStage Constant(int vus, TimeSpan duration) => Create(vus, vus, duration);

    /// <summary>Palier a effectif lineairement croissant ou decroissant.</summary>
    public static VirtualUserStage Ramp(int fromVus, int toVus, TimeSpan duration) =>
        Create(fromVus, toVus, duration);

    private static VirtualUserStage Create(int fromVus, int toVus, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromVus);
        ArgumentOutOfRangeException.ThrowIfNegative(toVus);

        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "La duree d'un palier doit etre strictement positive.");
        }

        return new VirtualUserStage(fromVus, toVus, duration);
    }

    /// <summary>
    /// Effectif cible, interpole (non arrondi), a <paramref name="elapsedSeconds"/> secondes
    /// du debut du palier.
    /// </summary>
    public double VusAt(double elapsedSeconds)
    {
        double clamped = Math.Clamp(elapsedSeconds, 0d, DurationSeconds);
        return IsFlat
            ? FromVus
            : FromVus + ((ToVus - FromVus) * (clamped / DurationSeconds));
    }
}