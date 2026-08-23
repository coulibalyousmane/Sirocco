namespace Sirocco.Scenarios;

/// <summary>Noms des etapes declarees par <see cref="WebSocketEchoWorkflow"/>.</summary>
public static class WebSocketEchoSteps
{
    /// <summary>Ouverture de la connexion WebSocket.</summary>
    public const string CONNECT = "ws-connect";

    /// <summary>Aller-retour d'un message texte.</summary>
    public const string ECHO = "ws-echo";
}