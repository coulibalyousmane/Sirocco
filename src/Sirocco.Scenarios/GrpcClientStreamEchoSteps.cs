namespace Sirocco.Scenarios;

/// <summary>Noms des etapes declarees par <see cref="GrpcClientStreamEchoWorkflow"/>.</summary>
public static class GrpcClientStreamEchoSteps
{
    /// <summary>L'appel entier : ouverture du flux montant, envoi des messages, reponse recapitulative.</summary>
    public const string UPLOAD = "grpc-client-stream-upload";
}