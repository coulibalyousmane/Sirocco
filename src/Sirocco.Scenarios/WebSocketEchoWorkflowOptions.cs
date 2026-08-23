namespace Sirocco.Scenarios;

/// <summary>Reglages de <see cref="WebSocketEchoWorkflow"/>.</summary>
public sealed class WebSocketEchoWorkflowOptions
{
    /// <summary>Chemin par defaut de l'endpoint d'echo.</summary>
    public const string DEFAULT_ECHO_PATH = "/ws/echo";

    /// <summary>Chemin relatif de l'endpoint WebSocket, derive de la meme base que <see cref="HttpClient"/>.</summary>
    public string EchoPath { get; init; } = DEFAULT_ECHO_PATH;

    /// <summary>Valide la coherence des reglages.</summary>
    /// <exception cref="ArgumentException">Un reglage est hors domaine.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(EchoPath))
        {
            throw new ArgumentException("EchoPath ne peut pas etre vide.", nameof(EchoPath));
        }
    }
}