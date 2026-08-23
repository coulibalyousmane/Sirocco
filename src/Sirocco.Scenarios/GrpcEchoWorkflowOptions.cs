using Sirocco.Domain.Execution;

namespace Sirocco.Scenarios;

/// <summary>Reglages de <see cref="GrpcEchoWorkflow"/> et des autres scenarios gRPC de reference.</summary>
public sealed class GrpcEchoWorkflowOptions
{
    /// <summary>Nombre de messages envoyes par defaut par un flux pilote par le client (upload, bidirectionnel).</summary>
    public const int DEFAULT_CLIENT_MESSAGE_COUNT = 5;

    /// <summary>
    /// Adresse du service gRPC. Si omise (<see langword="null"/> par defaut), derivee de
    /// <see cref="IVirtualUserContext.HttpClient"/>.BaseAddress — suffisant des que la cible
    /// negocie HTTP/1.1 et HTTP/2 sur le meme port via TLS (ALPN). En clair (<c>http://</c>),
    /// Kestrel ne multiplexe pas les deux sur un seul port : un point d'ecoute gRPC dedie,
    /// comme celui de <c>Sirocco.SampleTarget</c>, doit alors etre renseigne explicitement ici.
    /// </summary>
    public Uri? TargetUri { get; init; }

    /// <summary>
    /// Nombre de messages envoyes par <see cref="GrpcClientStreamEchoWorkflow"/> et
    /// <see cref="GrpcBidiStreamEchoWorkflow"/> avant fermeture du flux montant. A l'inverse du
    /// streaming serveur (<see cref="GrpcStreamEchoWorkflow"/>, ou c'est la cible qui decide),
    /// c'est ici le client qui pilote la longueur du flux.
    /// </summary>
    public int MessageCount { get; init; } = DEFAULT_CLIENT_MESSAGE_COUNT;
}