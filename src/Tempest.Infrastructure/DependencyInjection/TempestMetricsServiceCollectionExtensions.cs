using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using Tempest.Application.Metrics;
using Tempest.Domain.Metrics;
using Tempest.Infrastructure.Metrics;

namespace Tempest.Infrastructure.DependencyInjection;

/// <summary>
/// Cablage de la chaine de mesure : agregation, consommation du canal, publication.
/// </summary>
public static class TempestMetricsServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre l'agregateur, le processeur et les instruments.
    /// <para>
    /// A appeler <b>apres</b> <c>AddTempestEngine</c> : l'agregateur dimensionne ses tableaux
    /// sur le <see cref="StepRegistry"/>, qui doit donc etre le meme que celui du moteur.
    /// </para>
    /// </summary>
    /// <param name="services">Collection de services a completer.</param>
    /// <param name="options">Reglages de la fenetre glissante ; valeurs par defaut si omis.</param>
    public static IServiceCollection AddTempestMetrics(
        this IServiceCollection services,
        MetricsAggregatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        MetricsAggregatorOptions effectiveOptions = options ?? new MetricsAggregatorOptions();
        effectiveOptions.Validate();

        services.TryAddSingleton(effectiveOptions);
        services.TryAddSingleton(provider => new CustomMetricsAggregator(
            provider.GetRequiredService<CustomMetricRegistry>()));
        services.TryAddSingleton(provider => new MetricsAggregator(
            provider.GetRequiredService<StepRegistry>(),
            provider.GetRequiredService<MetricsAggregatorOptions>(),
            provider.GetRequiredService<CustomMetricsAggregator>()));

        services.TryAddSingleton<MetricsProcessor>();
        services.TryAddSingleton<TempestMeter>();

        // TempestMeter n'est demande par aucun autre service : sans ce declencheur, un
        // singleton enregistre mais jamais resolu ne se construit jamais, et ses instruments
        // n'existent pas — silencieusement, aucune metrique n'apparaitrait dans l'export.
        services.AddHostedService<MeterActivationHostedService>();

        return services;
    }

    /// <summary>
    /// Branche OpenTelemetry sur les instruments de Tempest.
    /// <para>
    /// L'exportateur Prometheus n'est volontairement pas cable ici : il depend d'ASP.NET Core,
    /// que la couche Infrastructure n'a aucune raison de connaitre. C'est l'hote qui l'ajoute,
    /// avec son point de terminaison <c>/metrics</c>.
    /// </para>
    /// </summary>
    /// <param name="services">Collection de services a completer.</param>
    /// <param name="configureMetrics">Point d'extension pour ajouter des exportateurs.</param>
    public static IServiceCollection AddTempestOpenTelemetry(
        this IServiceCollection services,
        Action<MeterProviderBuilder>? configureMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOpenTelemetry().WithMetrics(builder =>
        {
            builder.AddMeter(TempestMeter.METER_NAME);
            configureMetrics?.Invoke(builder);
        });

        return services;
    }
}