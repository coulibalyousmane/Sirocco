namespace Tempest.Scenarios;

/// <summary>Noms des etapes declarees par <see cref="DynamicCheckoutWorkflow"/>.</summary>
public static class DynamicCheckoutSteps
{
    /// <summary>Authentification de l'utilisateur virtuel.</summary>
    public const string LOGIN = "login";

    /// <summary>Consultation du catalogue.</summary>
    public const string BROWSE = "browse";

    /// <summary>Validation de la commande.</summary>
    public const string CHECKOUT = "checkout";
}