using System.Net.Http.Json;
using Sirocco.Host.Configuration;

namespace Sirocco.Host.Distributed;

/// <summary>
/// Annonce ce worker au maitre des le demarrage du process, avec plusieurs tentatives : rien
/// ne garantit que le maitre soit deja disponible au moment ou ce worker demarre. Une fois
/// enregistre, continue de signaler que ce worker est vivant (<c>POST /master/heartbeat</c>) a
/// intervalle regulier, jusqu'a l'arret du process.
/// <para>
/// C'est ce heartbeat continu qui permet au maitre de detecter un worker perdu en cours de tir
/// (<see cref="MasterCoordinator.MarkDeadIfStale"/>) plutot que d'attendre indefiniment un
/// rapport qui ne viendra jamais — l'enregistrement initial seul ne donnait aucun signal une
/// fois le tir en cours.
/// </para>
/// </summary>
internal sealed class WorkerLivenessHostedService(
    WorkerOptions options,
    SiroccoHostOptions siroccoOptions,
    IHttpClientFactory httpClientFactory,
    ILogger<WorkerLivenessHostedService> logger) : BackgroundService
{
    private const int MAX_REGISTRATION_ATTEMPTS = 10;
    private static readonly TimeSpan _registrationRetryDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        HttpClient client = httpClientFactory.CreateClient(ClusterCertificatePinning.CLUSTER_CLIENT_NAME);
        client.DefaultRequestHeaders.Authorization = ClusterAuthentication.BuildHeader(siroccoOptions.ClusterSharedSecret);
        WorkerRegistration registration = new(options.SelfUrl);

        bool registered = await RegisterAsync(client, registration, stoppingToken).ConfigureAwait(false);
        if (!registered)
        {
            return;
        }

        await HeartbeatLoopAsync(client, registration, stoppingToken).ConfigureAwait(false);
    }

    private async Task<bool> RegisterAsync(HttpClient client, WorkerRegistration registration, CancellationToken stoppingToken)
    {
        string registerUrl = $"{options.MasterUrl.TrimEnd('/')}/master/register";

        for (int attempt = 1; attempt <= MAX_REGISTRATION_ATTEMPTS; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await client
                    .PostAsJsonAsync(registerUrl, registration, stoppingToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Enregistre aupres du maitre {MasterUrl}.", options.MasterUrl);
                }

                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(
                    "Tentative {Attempt}/{MaxAttempts} d'enregistrement aupres de {MasterUrl} echouee : {Message}",
                    attempt,
                    MAX_REGISTRATION_ATTEMPTS,
                    options.MasterUrl,
                    ex.Message);

                await Task.Delay(_registrationRetryDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        logger.LogError(
            "Impossible de s'enregistrer aupres du maitre {MasterUrl} apres {MaxAttempts} tentatives.",
            options.MasterUrl,
            MAX_REGISTRATION_ATTEMPTS);

        return false;
    }

    /// <summary>
    /// Poste un heartbeat toutes les <see cref="WorkerOptions.HeartbeatIntervalSeconds"/> jusqu'a
    /// l'arret du process. Un heartbeat qui echoue est logue puis ignore : un blip reseau isole
    /// ne doit pas arreter la boucle, seul le maitre decide, par l'absence prolongee de
    /// heartbeat, qu'un worker est perdu.
    /// </summary>
    private async Task HeartbeatLoopAsync(HttpClient client, WorkerRegistration registration, CancellationToken stoppingToken)
    {
        string heartbeatUrl = $"{options.MasterUrl.TrimEnd('/')}/master/heartbeat";
        TimeSpan interval = TimeSpan.FromSeconds(options.HeartbeatIntervalSeconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);

                try
                {
                    using HttpResponseMessage response = await client
                        .PostAsJsonAsync(heartbeatUrl, registration, stoppingToken)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    logger.LogWarning(ex, "Heartbeat vers le maitre {MasterUrl} sans reponse exploitable.", options.MasterUrl);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Arret normal du process.
        }
    }
}

/// <summary>Corps de <c>POST /master/register</c> et de <c>POST /master/heartbeat</c>.</summary>
public sealed record WorkerRegistration(string WorkerUrl);