using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Sirocco.Domain.Timing;

/// <summary>
/// Horloge monotone du moteur. Toutes les durees manipulees sur le chemin critique
/// sont exprimees en "ticks" <see cref="Stopwatch"/> (entiers 64 bits), jamais en
/// <see cref="TimeSpan"/> ni en <see cref="DateTime"/> : pas de conversion, pas de
/// dependance a l'heure murale, pas de saut si l'horloge systeme est ajustee.
/// </summary>
public static class SiroccoClock
{
    private const double MILLISECONDS_PER_SECOND = 1_000d;
    private const double MICROSECONDS_PER_SECOND = 1_000_000d;

    /// <summary>Nombre de ticks par seconde de l'horloge haute resolution.</summary>
    public static readonly long Frequency = Stopwatch.Frequency;

    private static readonly double _ticksToMilliseconds = MILLISECONDS_PER_SECOND / Stopwatch.Frequency;
    private static readonly double _ticksToMicroseconds = MICROSECONDS_PER_SECOND / Stopwatch.Frequency;
    private static readonly double _ticksToSeconds = 1d / Stopwatch.Frequency;
    private static readonly double _stopwatchToTimeSpanTicks = TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency;

    /// <summary>Instant courant, en ticks monotones.</summary>
    public static long Now
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Stopwatch.GetTimestamp();
    }

    /// <summary>Convertit une duree en ticks vers des millisecondes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToMilliseconds(long ticks) => ticks * _ticksToMilliseconds;

    /// <summary>Convertit une duree en ticks vers des microsecondes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToMicroseconds(long ticks) => ticks * _ticksToMicroseconds;

    /// <summary>Convertit une duree en ticks vers des secondes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToSeconds(long ticks) => ticks * _ticksToSeconds;

    /// <summary>Convertit une duree en ticks vers un <see cref="TimeSpan"/> (hors chemin critique).</summary>
    public static TimeSpan ToTimeSpan(long ticks) => TimeSpan.FromTicks((long)(ticks * _stopwatchToTimeSpanTicks));

    /// <summary>Convertit un <see cref="TimeSpan"/> en ticks monotones (hors chemin critique).</summary>
    public static long FromTimeSpan(TimeSpan value) => (long)(value.Ticks / _stopwatchToTimeSpanTicks);

    /// <summary>Convertit un nombre de secondes en ticks monotones.</summary>
    public static long FromSeconds(double seconds) => (long)(seconds * Frequency);
}