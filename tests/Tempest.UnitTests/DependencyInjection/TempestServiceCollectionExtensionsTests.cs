using Microsoft.Extensions.DependencyInjection;
using Tempest.Application.DependencyInjection;
using Tempest.Application.Execution;
using Tempest.Application.Metrics;
using Tempest.Domain.Execution;
using Tempest.Domain.Load;
using Tempest.Domain.Metrics;
using Tempest.UnitTests.TestDoubles;

namespace Tempest.UnitTests.DependencyInjection;

public sealed class TempestServiceCollectionExtensionsTests
{
    private static ServiceCollection CreateServices()
    {
        ServiceCollection services = new();
        services.AddSingleton(new HttpClient());
        services.AddSingleton<IWorkflow>(DelegateWorkflow.NoOp());

        return services;
    }

    [Fact]
    public void The_engine_resolves_with_all_its_collaborators()
    {
        ServiceCollection services = CreateServices();
        services.AddTempestEngine(LoadProfile.Constant(100d, TimeSpan.FromSeconds(1)));

        using ServiceProvider provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<TargetRpsLoadEngine>();

        Assert.NotNull(engine);
        Assert.IsType<CoordinatedRateLimiter>(provider.GetRequiredService<ILoadScheduler>());
    }

    [Fact]
    public void The_metric_sink_is_the_same_instance_behind_both_registrations()
    {
        ServiceCollection services = CreateServices();
        services.AddTempestEngine(LoadProfile.Constant(100d, TimeSpan.FromSeconds(1)));

        using ServiceProvider provider = services.BuildServiceProvider();

        // L'agregateur a besoin du ChannelReader, que IMetricSink n'expose pas :
        // les deux resolutions doivent pointer sur le meme puits, sinon les mesures
        // partiraient dans un canal que personne ne lit.
        Assert.Same(
            provider.GetRequiredService<ChannelMetricSink>(),
            provider.GetRequiredService<IMetricSink>());
    }

    [Fact]
    public void The_step_registry_is_shared_across_the_whole_run()
    {
        ServiceCollection services = CreateServices();
        services.AddTempestEngine(LoadProfile.Constant(100d, TimeSpan.FromSeconds(1)));

        using ServiceProvider provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<TargetRpsLoadEngine>();

        Assert.Same(provider.GetRequiredService<StepRegistry>(), engine.Steps);
    }

    /// <summary>
    /// Tous les enregistrements passent par <c>TryAdd</c> : une implementation declaree
    /// en amont doit survivre a l'appel a <c>AddTempestEngine</c>.
    /// </summary>
    [Fact]
    public void A_scheduler_registered_beforehand_is_not_overwritten()
    {
        ServiceCollection services = CreateServices();
        ImmediateScheduler custom = new(tokenCount: 5);
        services.AddSingleton<ILoadScheduler>(custom);

        services.AddTempestEngine(LoadProfile.Constant(100d, TimeSpan.FromSeconds(1)));

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(custom, provider.GetRequiredService<ILoadScheduler>());
    }

    [Fact]
    public void Invalid_options_are_rejected_before_anything_is_registered()
    {
        ServiceCollection services = CreateServices();

        Assert.Throws<ArgumentException>(() => services.AddTempestEngine(
            LoadProfile.Constant(100d, TimeSpan.FromSeconds(1)),
            new LoadTestOptions { MaxVirtualUsers = 0 }));
    }

    [Fact]
    public void A_missing_profile_is_rejected()
    {
        ServiceCollection services = CreateServices();

        Assert.Throws<ArgumentNullException>(() => services.AddTempestEngine(null!));
    }
}