using Microsoft.Extensions.Hosting;

namespace Tempest.Infrastructure.Metrics;

/// <summary>
/// Force la construction de <see cref="TempestMeter"/> au demarrage de l'hote.
/// <para>
/// Le <see cref="System.Diagnostics.Metrics.Meter"/> et ses instruments ne sont crees qu'a la
/// construction de <see cref="TempestMeter"/>. Or rien d'autre ne depend naturellement de
/// cette classe : elle existe pour etre observee de l'exterieur (par un exportateur
/// Prometheus ou OTLP), pas pour etre appelee. Un conteneur d'injection de dependances ne la
/// construirait donc jamais tout seul — un singleton enregistre mais jamais resolu ne
/// s'instancie pas — et aucune metrique n'apparaitrait dans l'export, silencieusement.
/// </para>
/// <para>
/// Ce service n'a d'autre role que de forcer cette construction en demandant
/// <see cref="TempestMeter"/> dans son constructeur.
/// </para>
/// </summary>
internal sealed class MeterActivationHostedService(TempestMeter meter) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Rien a faire : demander TempestMeter au constructeur suffisait a le construire.
        // GC.KeepAlive documente l'intention et evite tout avertissement de parametre inutilise.
        GC.KeepAlive(meter);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}