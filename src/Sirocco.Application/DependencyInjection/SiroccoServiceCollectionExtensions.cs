using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sirocco.Application.Execution;
using Sirocco.Application.Metrics;
using Sirocco.Domain.Load;
using Sirocco.Domain.Metrics;

namespace Sirocco.Application.DependencyInjection;

/// <summary>
/// Enregistrement du moteur dans un conteneur d'injection de dependances.
/// <para>
/// Tous les enregistrements passent par <c>TryAdd</c> : declarer son propre
/// <see cref="ILoadScheduler"/> ou son propre <see cref="IMetricSink"/> avant d'appeler cette
/// methode suffit a le substituer, sans avoir a renoncer au reste du cablage.
/// </para>
/// </summary>
public static class SiroccoServiceCollectionExtensions
{
    /// <summary>
    /// Enregistre le moteur, son ordonnanceur, son registre d'etapes et son puits de metriques.
    /// <para>
    /// Restent a la charge de l'appelant, parce qu'ils dependent de l'hote : le
    /// <see cref="HttpClient"/> (via <c>AddHttpClient</c>) et le
    /// <see cref="Domain.Execution.IWorkflow"/> a jouer.
    /// </para>
    /// </summary>
    /// <param name="services">Collection de services a completer.</param>
    /// <param name="profile">
    /// Profil de charge a derouler (modele ouvert). <see langword="null"/> pour le modele ferme
    /// (<see cref="ClosedModelScheduler"/>) ou tout autre <see cref="ILoadScheduler"/> personnalise :
    /// dans ce cas, enregistrer ce <see cref="ILoadScheduler"/> <b>avant</b> d'appeler cette
    /// methode (voir la remarque de classe) — sans quoi la resolution de
    /// <see cref="TargetRpsLoadEngine"/> echouera, faute d'ordonnanceur enregistre.
    /// </param>
    /// <param name="options">Reglages de l'injecteur ; valeurs par defaut si omis.</param>
    /// <param name="metricQueueCapacity">Profondeur du canal de metriques.</param>
    /// <param name="schedulerSpinThreshold">Marge de rotation active de l'ordonnanceur, si <paramref name="profile"/> est fourni.</param>
    /// <param name="customMetricQueueCapacity">Profondeur du canal de metriques personnalisees.</param>
    public static IServiceCollection AddSiroccoEngine(
        this IServiceCollection services,
        LoadProfile? profile,
        LoadTestOptions? options = null,
        int metricQueueCapacity = ChannelMetricSink.DEFAULT_CAPACITY,
        TimeSpan? schedulerSpinThreshold = null,
        int customMetricQueueCapacity = ChannelCustomMetricSink.DEFAULT_CAPACITY)
    {
        ArgumentNullException.ThrowIfNull(services);

        LoadTestOptions effectiveOptions = options ?? new LoadTestOptions();
        effectiveOptions.Validate();

        services.TryAddSingleton(effectiveOptions);
        services.TryAddSingleton<StepRegistry>();
        services.TryAddSingleton<CustomMetricRegistry>();

        if (profile is not null)
        {
            services.TryAddSingleton(profile);
            services.TryAddSingleton<ILoadScheduler>(_ => new CoordinatedRateLimiter(profile, schedulerSpinThreshold));
        }

        // Le puits est enregistre sous son type concret *et* sous son abstraction : l'agregateur
        // de metriques a besoin du ChannelReader, que l'interface n'expose deliberement pas.
        services.TryAddSingleton(_ => new ChannelMetricSink(metricQueueCapacity));
        services.TryAddSingleton<IMetricSink>(provider => provider.GetRequiredService<ChannelMetricSink>());

        services.TryAddSingleton(_ => new ChannelCustomMetricSink(customMetricQueueCapacity));
        services.TryAddSingleton<ICustomMetricSink>(provider => provider.GetRequiredService<ChannelCustomMetricSink>());

        services.TryAddSingleton<TargetRpsLoadEngine>();

        return services;
    }
}