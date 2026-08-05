using System.Threading.Channels;
using Tempest.Application.Execution;
using Tempest.Domain.Load;
using Tempest.Domain.Timing;

namespace Tempest.UnitTests.Execution;

public sealed class CoordinatedRateLimiterTests
{
    private static readonly TimeSpan _spinThreshold = TimeSpan.FromMilliseconds(2);

    /// <summary>Deroule le regulateur dans une file assez large pour ne jamais le bloquer.</summary>
    private static List<ExecutionToken> Drain(LoadProfile profile, CancellationToken cancellationToken = default)
    {
        Channel<ExecutionToken> channel = Channel.CreateUnbounded<ExecutionToken>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true,
        });

        new CoordinatedRateLimiter(profile, _spinThreshold).Run(channel.Writer, cancellationToken);
        channel.Writer.TryComplete();

        List<ExecutionToken> tokens = [];
        while (channel.Reader.TryRead(out var token))
        {
            tokens.Add(token);
        }

        return tokens;
    }

    [Fact]
    public void Issues_exactly_the_number_of_tokens_the_profile_plans()
    {
        LoadProfile profile = LoadProfile.Constant(200d, TimeSpan.FromMilliseconds(250));

        var tokens = Drain(profile);

        Assert.Equal(50, profile.PlannedRequestCount);
        Assert.Equal(50, tokens.Count);
    }

    [Fact]
    public void Token_indices_are_dense_and_ordered()
    {
        var tokens = Drain(LoadProfile.Constant(200d, TimeSpan.FromMilliseconds(250)));

        for (int i = 0; i < tokens.Count; i++)
        {
            Assert.Equal(i, tokens[i].IterationIndex);
        }
    }

    /// <summary>
    /// Assertion deterministe malgre la nature temporelle du composant : les instants
    /// programmes viennent de l'echeancier, pas de l'horloge. Une machine chargee peut
    /// retarder l'<i>emission</i>, jamais la <i>planification</i>.
    /// </summary>
    [Fact]
    public void Scheduled_instants_follow_the_profile_not_the_wall_clock()
    {
        var tokens = Drain(LoadProfile.Constant(200d, TimeSpan.FromMilliseconds(250)));
        long origin = tokens[0].ScheduledTicks;

        for (int i = 0; i < tokens.Count; i++)
        {
            double offsetMilliseconds = TempestClock.ToMilliseconds(tokens[i].ScheduledTicks - origin);

            // 200 RPS : un tir toutes les 5 ms, exactement.
            Assert.Equal(i * 5d, offsetMilliseconds, 0.01d);
        }
    }

    [Fact]
    public void A_ramp_tightens_the_gap_between_scheduled_instants()
    {
        LoadProfile profile = new([LoadStage.Ramp(50d, 500d, TimeSpan.FromMilliseconds(400))]);

        var tokens = Drain(profile);

        Assert.True(tokens.Count > 10, $"Trop peu de jetons pour comparer : {tokens.Count}");

        long firstGap = tokens[1].ScheduledTicks - tokens[0].ScheduledTicks;
        long lastGap = tokens[^1].ScheduledTicks - tokens[^2].ScheduledTicks;

        Assert.True(lastGap < firstGap, $"L'ecart devrait se reduire : {lastGap} vs {firstGap}");
    }

    [Fact]
    public void The_run_lasts_roughly_the_profile_duration()
    {
        LoadProfile profile = LoadProfile.Constant(500d, TimeSpan.FromMilliseconds(300));

        long started = TempestClock.Now;
        var tokens = Drain(profile);
        double elapsed = TempestClock.ToMilliseconds(TempestClock.Now - started);

        Assert.Equal(150, tokens.Count);

        // Borne basse stricte : emettre plus vite que le profil fausserait le test de charge.
        Assert.True(elapsed >= 295d, $"Tir termine trop tot : {elapsed:F1} ms.");

        // Borne haute large : une machine de CI partagee a le droit d'etre lente.
        Assert.True(elapsed < 3_000d, $"Tir anormalement long : {elapsed:F1} ms.");
    }

    [Fact]
    public void Cancellation_stops_the_run_before_the_profile_ends()
    {
        LoadProfile profile = LoadProfile.Constant(100d, TimeSpan.FromSeconds(30));
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(150));

        long started = TempestClock.Now;
        var tokens = Drain(profile, cts.Token);
        double elapsed = TempestClock.ToMilliseconds(TempestClock.Now - started);

        Assert.True(elapsed < 5_000d, $"L'annulation n'a pas ete honoree : {elapsed:F1} ms.");
        Assert.True(tokens.Count < profile.PlannedRequestCount);
    }

    [Fact]
    public async Task A_full_queue_slows_the_producer_instead_of_dropping_tokens()
    {
        // File d'un seul jeton : le regulateur doit bloquer, pas jeter.
        LoadProfile profile = LoadProfile.Constant(1_000d, TimeSpan.FromMilliseconds(100));
        Channel<ExecutionToken> channel = Channel.CreateBounded<ExecutionToken>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
        });

        CoordinatedRateLimiter limiter = new(profile, _spinThreshold);
        Task producer = Task.Run(() =>
        {
            limiter.Run(channel.Writer, CancellationToken.None);
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

        // Aucun jeton perdu : le retard se paiera en dette d'ordonnancement, pas en trous.
        Assert.Equal(profile.PlannedRequestCount, received);
        Assert.Equal(profile.PlannedRequestCount, limiter.TokensIssued);
    }

    [Fact]
    public void TokensPlanned_matches_the_profile_before_any_run()
    {
        LoadProfile profile = LoadProfile.Constant(250d, TimeSpan.FromSeconds(4));
        CoordinatedRateLimiter limiter = new(profile, _spinThreshold);

        Assert.Equal(1_000, limiter.TokensPlanned);
        Assert.Equal(0, limiter.TokensIssued);
        Assert.Equal(0, limiter.StartTicks);
    }
}