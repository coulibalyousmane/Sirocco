namespace Tempest.SampleTarget.Contracts;

/// <summary>
/// Corps attendu pour se connecter. Aucun mot de passe n'est realmente verifie : cette API
/// est une cible de demonstration, pas un service protege.
/// </summary>
internal sealed record LoginRequest(string Username, string Password);