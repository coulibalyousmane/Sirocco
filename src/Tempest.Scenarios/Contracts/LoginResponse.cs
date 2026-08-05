namespace Tempest.Scenarios.Contracts;

/// <summary>Reponse de connexion : jeton a presenter aux appels suivants.</summary>
internal sealed record LoginResponse(string Token);