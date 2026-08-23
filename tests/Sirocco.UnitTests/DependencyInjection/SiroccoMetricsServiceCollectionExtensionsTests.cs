using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sirocco.Application.Metrics;
using Sirocco.Domain.Metrics;
using Sirocco.Infrastructure.DependencyInjection;
using Sirocco.Infrastructure.Metrics;

namespace Sirocco.UnitTests.DependencyInjection;

public sealed class SiroccoMetricsServiceCollectionExtensionsTests
{
    /// <summary>
    /// Reproduit exactement le defaut du premier tir reel : <see cref="SiroccoMeter"/> n'etait
    /// demande par aucun autre service enregistre par <c>AddSiroccoMetrics</c>. Un singleton
    /// jamais resolu ne se construit jamais, donc son <see cref="Meter"/> — et ses instruments
    /// — n'existaient tout simplement pas. Prometheus n'exposait alors que <c>target_info</c>,
    /// aucune metrique Sirocco.
    /// <para>
    /// Le test ne resout jamais <see cref="SiroccoMeter"/> directement : il se contente de
    /// demarrer les <see cref="IHostedService"/> enregistres, exactement comme le fait
    /// <c>IHost.StartAsync</c>, puis verifie qu'un instrument est apparu. La valeur observee
    /// est une sentinelle deposee par ce test sur cette instance d'agregateur precise, pour
    /// rester correct meme si d'autres tests publient un meter du meme nom en parallele.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Starting_the_registered_hosted_services_constructs_the_meter_without_anyone_asking_for_it_directly()
    {
        const long SENTINEL_DROPPED_COUNT = 918_273_645L;
        const string DROPPED_INSTRUMENT = "sirocco.metrics.dropped";

        ServiceCollection services = new();
        services.AddSingleton<StepRegistry>();
        services.AddSingleton<CustomMetricRegistry>();
        services.AddSiroccoMetrics();

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<MetricsAggregator>().MetricsDropped = SENTINEL_DROPPED_COUNT;

        long observed = -1L;
        using MeterListener listener = new()
        {
            InstrumentPublished = (instrument, activeListener) =>
            {
                if (instrument.Meter.Name == SiroccoMeter.METER_NAME && instrument.Name == DROPPED_INSTRUMENT)
                {
                    activeListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            if (value == SENTINEL_DROPPED_COUNT)
            {
                observed = value;
            }
        });
        listener.Start();

        // Ce qu'un hote ASP.NET Core fait reellement au demarrage : jamais un appel direct a
        // GetRequiredService<SiroccoMeter>().
        foreach (IHostedService hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        listener.RecordObservableInstruments();

        Assert.Equal(SENTINEL_DROPPED_COUNT, observed);
    }

    [Fact]
    public void AddSiroccoMetrics_registers_an_activation_service_for_the_meter()
    {
        ServiceCollection services = new();
        services.AddSingleton<StepRegistry>();
        services.AddSingleton<CustomMetricRegistry>();
        services.AddSiroccoMetrics();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Contains(provider.GetServices<IHostedService>(), service => service is MeterActivationHostedService);
    }

    [Fact]
    public void Invalid_window_options_are_rejected_before_anything_is_registered()
    {
        ServiceCollection services = new();

        Assert.Throws<ArgumentException>(() => services.AddSiroccoMetrics(
            new MetricsAggregatorOptions { WindowBucketCount = 0 }));
    }
}