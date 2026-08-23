using System.Threading.Channels;
using Sirocco.Application.Execution;
using Sirocco.Domain.Timing;

namespace Sirocco.UnitTests.Execution;

public sealed class RateCappedSchedulerTests
{
    /// <summary>Deroule le decorateur dans une file assez large pour ne jamais le bloquer.</summary>
    private static List<ExecutionToken> Drain(
        ILoadScheduler inner, double maxTokensPerSecond, CancellationToken cancellationToken = default)
    {
        Channel<ExecutionToken> channel = Channel.CreateUnbounded<ExecutionToken>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true,
        });

        new RateCappedScheduler(inner, maxTokensPerSecond).Run(channel.Writer, cancellationToken);
        channel.Writer.TryComplete();

        List<ExecutionToken> tokens = [];
        while (channel.Reader.TryRead(out var token))
        {
            tokens.Add(token);
        }

        return tokens;
    }

    [Fact]
    public void A_non_positive_cap_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateCappedScheduler(new IterationCountScheduler(1L), 0d));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateCappedScheduler(new IterationCountScheduler(1L), -5d));
    }

    [Fact]
    public void A_null_inner_scheduler_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new RateCappedScheduler(null!, 10d));
    }

    [Fact]
    public void TokensPlanned_and_StartTicks_pass_through_to_the_inner_scheduler_unchanged()
    {
        // Plafond volontairement tres haut : le but est de verifier le relai, pas le bridage.
        IterationCountScheduler inner = new(50L);
        RateCappedScheduler capped = new(inner, 100_000d);

        Assert.Equal(50, capped.TokensPlanned);
        Assert.Equal(0, capped.TokensIssued);
        Assert.Equal(0, capped.StartTicks);

        Channel<ExecutionToken> channel = Channel.CreateUnbounded<ExecutionToken>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true,
        });
        capped.Run(channel.Writer, CancellationToken.None);

        Assert.Equal(50, capped.TokensPlanned);
        Assert.Equal(50, capped.TokensIssued);
        Assert.Equal(inner.TokensIssued, capped.TokensIssued);
        Assert.Equal(inner.StartTicks, capped.StartTicks);
        Assert.True(capped.StartTicks > 0);
    }

    [Fact]
    public void Token_indices_from_the_inner_scheduler_survive_capping_unchanged()
    {
        var tokens = Drain(new IterationCountScheduler(30L), 100_000d);

        Assert.Equal(30, tokens.Count);
        for (int i = 0; i < tokens.Count; i++)
        {
            Assert.Equal(i, tokens[i].IterationIndex);
        }
    }

    /// <summary>
    /// Le coeur du bridage : un ordonnanceur enveloppe qui emettrait librement des centaines de
    /// jetons en 200ms (voir <c>ClosedModelSchedulerTests.Issues_tokens_continuously...</c>) doit
    /// se voir ramene pres du plafond configure une fois enveloppe.
    /// </summary>
    [Fact]
    public void The_actual_throughput_never_exceeds_the_configured_cap()
    {
        const double MaxTokensPerSecond = 50d;

        var tokens = Drain(new ClosedModelScheduler(TimeSpan.FromMilliseconds(200)), MaxTokensPerSecond);

        // 50/s sur 200ms plafonne a 10 jetons ; large marge pour la gigue du test, mais toujours
        // tres loin des centaines que le meme ordonnanceur produirait sans bridage.
        Assert.InRange(tokens.Count, 1, 20);
    }

    [Fact]
    public void Cancellation_stops_the_run_even_while_waiting_on_the_cap()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(50));

        long started = SiroccoClock.Now;
        // Plafond tres bas et duree tres longue : sans annulation, ce tir durerait 30 secondes.
        Drain(new ClosedModelScheduler(TimeSpan.FromSeconds(30)), maxTokensPerSecond: 2d, cts.Token);
        double elapsed = SiroccoClock.ToMilliseconds(SiroccoClock.Now - started);

        Assert.True(elapsed < 5_000d, $"L'annulation n'a pas ete honoree pendant l'attente du plafond : {elapsed:F1} ms.");
    }

    [Fact]
    public async Task A_full_downstream_queue_slows_the_producer_instead_of_dropping_tokens()
    {
        // Plafond tres haut : c'est la file, pas le bridage, qui doit ralentir le producteur ici.
        Channel<ExecutionToken> channel = Channel.CreateBounded<ExecutionToken>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
        });

        RateCappedScheduler capped = new(new IterationCountScheduler(20L), 100_000d);
        Task producer = Task.Run(() =>
        {
            capped.Run(channel.Writer, CancellationToken.None);
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
        Assert.Equal(20, capped.TokensIssued);
    }
}