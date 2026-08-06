using System.Net.Http.Json;
using Tempest.Host.Configuration;

namespace Tempest.Host.Distributed;

/// <summary>
/// Annonce ce worker au maitre des le demarrage du process, avec plusieurs tentatives : rien
/// ne garantit que le maitre soit deja disponible au moment ou ce worker demarre.
/// </summary>
internal sealed class WorkerRegistrationHostedService(
    WorkerOptions options,
    TempestHostOptions tempestOptions,
    IHttpClientFactory httpClientFactory,
    ILogger<WorkerRegistrationHostedService> logger) : BackgroundService
{
    private const int MAX_ATTEMPTS = 10;
    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        HttpClient client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = ClusterAuthentication.BuildHeader(tempestOptions.ClusterSharedSecret);
        string registerUrl = $"{options.MasterUrl.TrimEnd('/')}/master/register";
        WorkerRegistration registration = new(options.SelfUrl);

        for (int attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
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

                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(
                    "Tentative {Attempt}/{MaxAttempts} d'enregistrement aupres de {MasterUrl} echouee : {Message}",
                    attempt,
                    MAX_ATTEMPTS,
                    options.MasterUrl,
                    ex.Message);

                await Task.Delay(_retryDelay, stoppingToken).ConfigureAwait(false);
            }
        }

        logger.LogError(
            "Impossible de s'enregistrer aupres du maitre {MasterUrl} apres {MaxAttempts} tentatives.",
            options.MasterUrl,
            MAX_ATTEMPTS);
    }
}

/// <summary>Corps de <c>POST /master/register</c>.</summary>
public sealed record WorkerRegistration(string WorkerUrl);