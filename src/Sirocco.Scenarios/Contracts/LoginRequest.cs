namespace Sirocco.Scenarios.Contracts;

/// <summary>Corps de la requete de connexion.</summary>
internal sealed record LoginRequest(string Username, string Password);