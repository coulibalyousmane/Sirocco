namespace Tempest.SampleTarget.Contracts;

/// <summary>Jeton emis par la connexion, a presenter en <c>Authorization: Bearer</c>.</summary>
internal sealed record LoginResponse(string Token);