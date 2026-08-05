namespace Tempest.Scenarios;

/// <summary>
/// Etat local d'un utilisateur virtuel, conserve entre les iterations de
/// <see cref="DynamicCheckoutWorkflow"/> : une seule connexion par utilisateur virtuel,
/// pas une par iteration.
/// </summary>
internal sealed class CheckoutSession(int userIndex)
{
    /// <summary>Index du compte assigne a cet utilisateur virtuel, stable pour tout le tir.</summary>
    public int UserIndex { get; } = userIndex;

    /// <summary>
    /// Jeton de la session en cours, ou <see langword="null"/> si l'utilisateur virtuel doit
    /// (re)passer par l'etape de connexion — au demarrage, ou apres un rejet 401.
    /// </summary>
    public string? Token { get; set; }
}