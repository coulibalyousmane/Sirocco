using System.Threading.Channels;
using Sirocco.Application.Execution;
using Sirocco.Domain.Timing;

namespace Sirocco.UnitTests.Execution;

public sealed class ClosedModelSchedulerTests
{
    /// <summary>Deroule l'ordonnanceur dans une file assez large pour ne jamais le bloquer.</summary>
    private static List<ExecutionToken> Drain(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        Channel<ExecutionToken> channel = Channel.CreateUnbounded<ExecutionToken>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true,
        });

        new ClosedModelScheduler(duration).Run(channel.Writer, cancellationToken);
        channel.Writer.TryComplete();

        List<ExecutionToken> tokens = [];
        while (channel.Reader.TryRead(out var token))
        {
            tokens.Add(token);
        }

        return tokens;
    }

    [Fact]
    public void A_negative_or_zero_duration_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClosedModelScheduler(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClosedModelScheduler(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Issues_tokens_continuously_with_no_rate_limit_when_the_queue_never_fills()
    {
        var tokens = Drain(TimeSpan.FromMilliseconds(200));

        // Sans limite de debit ni file pleine, l'ordonnanceur emet aussi vite qu'il peut : des
        // centaines de jetons en 200ms est le comportement attendu, pas une anomalie.
        Assert.True(tokens.Count > 100, $"Trop peu de jetons pour une file sans limite : {tokens.Count}");
    }

    [Fact]
    public void Token_indices_are_dense_and_ordered()
    {
        var tokens = Drain(TimeSpan.FromMilliseconds(100));

        for (int i = 0; i < tokens.Count; i++)
        {
            Assert.Equal(i, tokens[i].IterationIndex);
        }
    }

    /// <summary>
    /// Le coeur de la distinction avec le modele ouvert : un jeton du modele ferme porte
    /// l'instant de sa propre emission, jamais un instant planifie a l'avance — il n'existe pas
    /// d'echeancier a comparer.
    /// </summary>
    [Fact]
    public void Scheduled_ticks_track_wall_clock_emission_not_a_theoretical_schedule()
    {
        long before = SiroccoClock.Now;
        var tokens = Drain(TimeSpan.FromMilliseconds(50));
        long after = SiroccoClock.Now;

        Assert.NotEmpty(tokens);
        Assert.All(tokens, token => Assert.InRange(token.ScheduledTicks, before, after));
    }

    [Fact]
    public void The_run_lasts_roughly_the_configured_duration()
    {
        long started = SiroccoClock.Now;
        Drain(TimeSpan.FromMilliseconds(200));
        double elapsed = SiroccoClock.ToMilliseconds(SiroccoClock.Now - started);

        Assert.True(elapsed >= 195d, $"Tir termine trop tot : {elapsed:F1} ms.");
        Assert.True(elapsed < 3_000d, $"Tir anormalement long : {elapsed:F1} ms.");
    }

    [Fact]
    public void Cancellation_stops_the_run_before_the_duration_elapses()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(50));

        long started = SiroccoClock.Now;
        Drain(TimeSpan.FromSeconds(30), cts.Token);
        double elapsed = SiroccoClock.ToMilliseconds(SiroccoClock.Now - started);

        Assert.True(elapsed < 5_000d, $"L'annulation n'a pas ete honoree : {elapsed:F1} ms.");
    }

    [Fact]
    public async Task A_full_queue_slows_the_producer_instead_of_dropping_tokens()
    {
        // File d'un seul jeton : l'ordonnanceur doit bloquer, pas jeter, exactement comme le
        // modele ouvert.
        Channel<ExecutionToken> channel = Channel.CreateBounded<ExecutionToken>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
        });

        ClosedModelScheduler scheduler = new(TimeSpan.FromMilliseconds(100));
        Task producer = Task.Run(() =>
        {
            scheduler.Run(channel.Writer, CancellationToken.None);
            channel.Writer.TryComplete();
        });

        int received = 0;
        while (await channel.Reader.WaitToReadAsync())
        {
            while (channel.Reader.TryRead(out _))
            {
                received++;
            }
        }

        await producer;

        Assert.True(received > 0);
        Assert.Equal(received, scheduler.TokensIssued);
    }

    [Fact]
    public void TokensPlanned_always_equals_TokensIssued()
    {
        ClosedModelScheduler scheduler = new(TimeSpan.FromMilliseconds(100));

        Assert.Equal(0, scheduler.TokensPlanned);
        Assert.Equal(0, scheduler.StartTicks);

        Channel<ExecutionToken> channel = Channel.CreateUnbounded<ExecutionToken>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true,
        });
        scheduler.Run(channel.Writer, CancellationToken.None);

        Assert.True(scheduler.TokensIssued > 0);
        Assert.Equal(scheduler.TokensIssued, scheduler.TokensPlanned);
        Assert.True(scheduler.StartTicks > 0);
    }
}