using System.Threading.Channels;
using Sirocco.Application.Execution;
using Sirocco.Domain.Timing;

namespace Sirocco.UnitTests.Execution;

public sealed class IterationCountSchedulerTests
{
    /// <summary>Deroule l'ordonnanceur dans une file assez large pour ne jamais le bloquer.</summary>
    private static List<ExecutionToken> Drain(long totalIterations, CancellationToken cancellationToken = default)
    {
        Channel<ExecutionToken> channel = Channel.CreateUnbounded<ExecutionToken>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true,
        });

        new IterationCountScheduler(totalIterations).Run(channel.Writer, cancellationToken);
        channel.Writer.TryComplete();

        List<ExecutionToken> tokens = [];
        while (channel.Reader.TryRead(out var token))
        {
            tokens.Add(token);
        }

        return tokens;
    }

    [Fact]
    public void A_negative_or_zero_total_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IterationCountScheduler(0L));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IterationCountScheduler(-1L));
    }

    [Fact]
    public void Issues_exactly_the_requested_total_then_stops()
    {
        var tokens = Drain(250L);

        Assert.Equal(250, tokens.Count);
    }

    [Fact]
    public void Token_indices_are_dense_and_ordered()
    {
        var tokens = Drain(100L);

        for (int i = 0; i < tokens.Count; i++)
        {
            Assert.Equal(i, tokens[i].IterationIndex);
        }
    }

    /// <summary>
    /// Comme en modele ferme a duree fixe : un jeton porte l'instant de sa propre emission,
    /// jamais un instant planifie a l'avance.
    /// </summary>
    [Fact]
    public void Scheduled_ticks_track_wall_clock_emission_not_a_theoretical_schedule()
    {
        long before = SiroccoClock.Now;
        var tokens = Drain(50L);
        long after = SiroccoClock.Now;

        Assert.NotEmpty(tokens);
        Assert.All(tokens, token => Assert.InRange(token.ScheduledTicks, before, after));
    }

    [Fact]
    public void TokensPlanned_is_the_fixed_total_known_up_front()
    {
        IterationCountScheduler scheduler = new(42L);

        // Connu avant meme que le tir ne commence : a la difference du modele ferme a duree
        // fixe, ce total n'est pas une consequence de l'emission, il la precede.
        Assert.Equal(42, scheduler.TokensPlanned);
        Assert.Equal(0, scheduler.TokensIssued);

        Channel<ExecutionToken> channel = Channel.CreateUnbounded<ExecutionToken>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true,
        });
        scheduler.Run(channel.Writer, CancellationToken.None);

        Assert.Equal(42, scheduler.TokensPlanned);
        Assert.Equal(42, scheduler.TokensIssued);
    }

    [Fact]
    public void Cancellation_leaves_fewer_tokens_issued_than_planned()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        IterationCountScheduler scheduler = new(1_000_000L);
        Channel<ExecutionToken> channel = Channel.CreateUnbounded<ExecutionToken>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true,
        });

        scheduler.Run(channel.Writer, cts.Token);

        // Annule avant meme le premier jeton : c'est exactement le signal que InjectorFellBehind
        // doit remonter, contrairement au modele ferme a duree fixe qui n'a pas de "planifie".
        Assert.True(scheduler.TokensIssued < scheduler.TokensPlanned);
    }

    [Fact]
    public async Task A_full_queue_slows_the_producer_instead_of_dropping_tokens()
    {
        // File d'un seul jeton : l'ordonnanceur doit bloquer, pas jeter, exactement comme le
        // modele ferme a duree fixe.
        Channel<ExecutionToken> channel = Channel.CreateBounded<ExecutionToken>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
        });

        IterationCountScheduler scheduler = new(20L);
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

        Assert.Equal(20, received);
        Assert.Equal(20, scheduler.TokensIssued);
    }
}