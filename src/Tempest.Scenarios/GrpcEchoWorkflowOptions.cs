using Tempest.Domain.Execution;

namespace Tempest.Scenarios;

/// <summary>Reglages de <see cref="GrpcEchoWorkflow"/>.</summary>
public sealed class GrpcEchoWorkflowOptions
{
    /// <summary>
    /// Adresse du service gRPC. Si omise (<see langword="null"/> par defaut), derivee de
    /// <see cref="IVirtualUserContext.HttpClient"/>.BaseAddress — suffisant des que la cible
    /// negocie HTTP/1.1 et HTTP/2 sur le meme port via TLS (ALPN). En clair (<c>http://</c>),
    /// Kestrel ne multiplexe pas les deux sur un seul port : un point d'ecoute gRPC dedie,
    /// comme celui de <c>Tempest.SampleTarget</c>, doit alors etre renseigne explicitement ici.
    /// </summary>
    public Uri? TargetUri { get; init; }
}