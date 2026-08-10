using System.Threading.Channels;
using Tempest.Application.Timing;
using Tempest.Domain.Load;
using Tempest.Domain.Timing;

namespace Tempest.Application.Execution;

/// <summary>
/// Ordonnanceur qui deroule l'echeancier d'un <see cref="LoadProfile"/> et emet un
/// <see cref="ExecutionToken"/> par requete planifiee.
/// <para>
/// <b>Le principe.</b> Un regulateur naif dort <c>1 / rps</c> entre deux tirs. Chaque sommeil
/// depasse legerement sa cible, et ces depassements s'additionnent : au bout de quelques
/// minutes le debit reel a derive sous la consigne, sans que rien ne le signale.
/// </para>
/// <para>
/// Ici, on ne raisonne jamais en intervalle mais en <b>position absolue dans l'echeancier</b> :
/// le jeton d'index <c>n</c> porte l'instant <c>ScheduledSecondsFor(n)</c>, calcule depuis le
/// debut du tir. Un reveil tardif se rattrape a la rafale suivante, la derive ne s'accumule pas,
/// et surtout l'ecart entre l'instant grave dans le jeton et l'instant de consommation
/// <b>est</b> la mesure du <i>coordinated omission</i>.
/// </para>
/// </summary>
public sealed class CoordinatedRateLimiter : ILoadScheduler
{
    /// <summary>Marge de rotation active par defaut, en millisecondes.</summary>
    public const double DEFAULT_SPIN_THRESHOLD_MILLISECONDS = 2d;

    private readonly LoadProfile _profile;
    private readonly long _spinThresholdTicks;

    private long _issued;
    private long _startTicks;

    /// <summary>Cree un ordonnanceur pour un profil donne.</summary>
    /// <param name="profile">Profil de charge a derouler.</param>
    /// <param name="spinThreshold">
    /// Marge finale traitee en rotation active. Augmenter ameliore la precision du tir au prix
    /// d'un coeur occupe ; diminuer laisse la gigue du timer systeme deteriorer la regularite.
    /// </param>
    public CoordinatedRateLimiter(LoadProfile profile, TimeSpan? spinThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        TimeSpan threshold = spinThreshold ?? TimeSpan.FromMilliseconds(DEFAULT_SPIN_THRESHOLD_MILLISECONDS);
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, TimeSpan.Zero);

        _profile = profile;
        _spinThresholdTicks = TempestClock.FromTimeSpan(threshold);
    }

    /// <inheritdoc />
    public long TokensPlanned => _profile.PlannedRequestCount;

    /// <inheritdoc />
    public long TokensIssued => Interlocked.Read(ref _issued);

    /// <inheritdoc />
    public long StartTicks => Interlocked.Read(ref _startTicks);

    /// <inheritdoc />
    public void Run(ChannelWriter<ExecutionToken> tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        long totalTokens = _profile.PlannedRequestCount;
        long startTicks = TempestClock.Now;
        Interlocked.Exchange(ref _startTicks, startTicks);

        long issued = 0L;

        while (issued < totalTokens && !cancellationToken.IsCancellationRequested)
        {
            // Instantane du temps ecoule : volontairement non rafraichi dans la boucle
            // interne, pour que chaque reveil emette un lot borne et rende la main.
            double elapsedSeconds = TempestClock.ToSeconds(TempestClock.Now - startTicks);

            while (issued < totalTokens)
            {
                double scheduledSeconds = _profile.ScheduledSecondsFor(issued);
                if (scheduledSeconds > elapsedSeconds)
                {
                    break;
                }

                ExecutionToken token = new(issued, startTicks + TempestClock.FromSeconds(scheduledSeconds));
                if (!BlockingTokenWriter.TryEmit(tokens, in token, cancellationToken))
                {
                    Interlocked.Exchange(ref _issued, issued);
                    return;
                }

                issued++;
            }

            Interlocked.Exchange(ref _issued, issued);

            if (issued >= totalTokens)
            {
                return;
            }

            // Rien a emettre : on dort jusqu'a l'echeance exacte du prochain jeton.
            long nextTicks = startTicks + TempestClock.FromSeconds(_profile.ScheduledSecondsFor(issued));
            PrecisionWait.Until(nextTicks, _spinThresholdTicks, cancellationToken);
        }

        Interlocked.Exchange(ref _issued, issued);
    }
}