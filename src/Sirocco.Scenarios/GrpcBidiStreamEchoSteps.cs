namespace Sirocco.Scenarios;

/// <summary>Noms des etapes declarees par <see cref="GrpcBidiStreamEchoWorkflow"/>.</summary>
public static class GrpcBidiStreamEchoSteps
{
    /// <summary>Aller-retour d'un message : ecriture sur le flux montant puis lecture de son echo.</summary>
    public const string MESSAGE = "grpc-bidi-stream-message";
}