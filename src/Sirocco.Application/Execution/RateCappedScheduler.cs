using System.Threading.Channels;
using Sirocco.Application.Timing;
using Sirocco.Domain.Timing;

namespace Sirocco.Application.Execution;

/// <summary>
/// Decorateur qui plafonne le debit reel d'un <see cref="ILoadScheduler"/> quelconque a un
/// nombre maximal de jetons par seconde — le « bridage » : un plafond de debit global,
/// independant du profil ou du modele de charge sous-jacent.
/// <para>
/// Meme principe que <see cref="CoordinatedRateLimiter"/> : comparer le nombre de jetons deja
/// transmis au nombre qui devrait l'etre a cet instant selon le plafond (une integrale, jamais
/// un delai par jeton, pour ne pas laisser la cadence deriver), et retarder la transmission tant
/// que l'ecart n'est pas resorbe.
/// </para>
/// <para>
/// Le retard porte sur la <b>transmission</b> au canal reel, jamais sur
/// <see cref="ExecutionToken.ScheduledTicks"/> deja fixe par l'ordonnanceur enveloppe : il se
/// manifeste donc, cote rapport, exactement comme une dette d'ordonnancement — le comportement
/// voulu, pas un effet de bord a corriger. Sirocco existe pour montrer ce genre d'ecart, pas pour
/// le masquer.
/// </para>
/// <para>
/// S'applique en enveloppant le <see cref="ChannelWriter{T}"/> remis a l'ordonnanceur enveloppe,
/// jamais son <see cref="ILoadScheduler.Run"/> lui-meme : ce dernier ignore totalement qu'il est
/// bride, ce qui permet a ce decorateur de s'appliquer identiquement aux quatre ordonnanceurs
/// existants (modele ouvert, ferme, montee d'utilisateurs, iterations) sans toucher a aucun
/// d'eux.
/// </para>
/// </summary>
public sealed class RateCappedScheduler : ILoadScheduler
{
    /// <summary>Marge de rotation active par defaut, en millisecondes.</summary>
    public const double DEFAULT_SPIN_THRESHOLD_MILLISECONDS = 2d;

    private readonly ILoadScheduler _inner;
    private readonly double _maxTokensPerSecond;
    private readonly long _spinThresholdTicks;

    /// <summary>Cree un decorateur plafonnant <paramref name="inner"/> a <paramref name="maxTokensPerSecond"/> jetons par seconde.</summary>
    /// <param name="inner">Ordonnanceur dont le debit reel est plafonne.</param>
    /// <param name="maxTokensPerSecond">Plafond de debit, en jetons par seconde. Doit etre strictement positif.</param>
    /// <param name="spinThreshold">Marge finale traitee en rotation active, voir <see cref="PrecisionWait"/>.</param>
    public RateCappedScheduler(ILoadScheduler inner, double maxTokensPerSecond, TimeSpan? spinThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxTokensPerSecond, 0d);

        TimeSpan threshold = spinThreshold ?? TimeSpan.FromMilliseconds(DEFAULT_SPIN_THRESHOLD_MILLISECONDS);
        ArgumentOutOfRangeException.ThrowIfLessThan(threshold, TimeSpan.Zero);

        _inner = inner;
        _maxTokensPerSecond = maxTokensPerSecond;
        _spinThresholdTicks = SiroccoClock.FromTimeSpan(threshold);
    }

    /// <inheritdoc />
    public long TokensPlanned => _inner.TokensPlanned;

    /// <inheritdoc />
    public long TokensIssued => _inner.TokensIssued;

    /// <inheritdoc />
    public long StartTicks => _inner.StartTicks;

    /// <inheritdoc />
    public void Run(ChannelWriter<ExecutionToken> tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        long startTicks = SiroccoClock.Now;
        CappingChannelWriter capped = new(tokens, _maxTokensPerSecond, startTicks, _spinThresholdTicks);
        _inner.Run(capped, cancellationToken);
    }

    /// <summary>
    /// Enveloppe le canal reel : ne laisse passer un jeton que lorsque l'integrale du plafond
    /// l'autorise, sinon retarde la transmission via <see cref="PrecisionWait"/> plutot que de le
    /// perdre — le blocage est assume, comme partout ailleurs sur ce thread dedie.
    /// </summary>
    private sealed class CappingChannelWriter(
        ChannelWriter<ExecutionToken> inner,
        double maxTokensPerSecond,
        long startTicks,
        long spinThresholdTicks) : ChannelWriter<ExecutionToken>
    {
        private long _forwarded;

        public override bool TryWrite(ExecutionToken item)
        {
            if (SiroccoClock.Now < DueTicks())
            {
                return false;
            }

            if (!inner.TryWrite(item))
            {
                return false;
            }

            Interlocked.Increment(ref _forwarded);
            return true;
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
        {
            PrecisionWait.Until(DueTicks(), spinThresholdTicks, cancellationToken);
            return inner.WaitToWriteAsync(cancellationToken);
        }

        private long DueTicks() =>
            startTicks + SiroccoClock.FromSeconds(Interlocked.Read(ref _forwarded) / maxTokensPerSecond);
    }
}