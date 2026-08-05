namespace Tempest.Scenarios;

/// <summary>Reglages de <see cref="DynamicCheckoutWorkflow"/>.</summary>
public sealed class DynamicCheckoutWorkflowOptions
{
    /// <summary>Chemin par defaut de l'etape de connexion.</summary>
    public const string DEFAULT_LOGIN_PATH = "/api/auth/login";

    /// <summary>Chemin par defaut de l'etape de consultation du catalogue.</summary>
    public const string DEFAULT_PRODUCTS_PATH = "/api/catalog/products";

    /// <summary>Chemin par defaut de l'etape de commande.</summary>
    public const string DEFAULT_CHECKOUT_PATH = "/api/checkout";

    /// <summary>Taille par defaut du pool de comptes utilisateur pre-generes.</summary>
    public const int DEFAULT_USER_POOL_SIZE = 500;

    /// <summary>Nombre maximal d'articles par defaut dans un panier.</summary>
    public const int DEFAULT_MAX_CART_ITEMS = 3;

    /// <summary>
    /// Graine par defaut du generateur de donnees. Fixe pour que deux tirs produisent le
    /// meme jeu de comptes : un rapport reste comparable d'une execution a l'autre.
    /// </summary>
    public const int DEFAULT_RANDOM_SEED = 20_260_803;

    /// <summary>Chemin relatif de l'etape de connexion.</summary>
    public string LoginPath { get; init; } = DEFAULT_LOGIN_PATH;

    /// <summary>Chemin relatif de l'etape de consultation du catalogue.</summary>
    public string ProductsPath { get; init; } = DEFAULT_PRODUCTS_PATH;

    /// <summary>Chemin relatif de l'etape de commande.</summary>
    public string CheckoutPath { get; init; } = DEFAULT_CHECKOUT_PATH;

    /// <summary>
    /// Nombre de comptes utilisateur pre-generes au demarrage. Un utilisateur virtuel se voit
    /// assigner un compte par l'index <c>VirtualUserId % UserPoolSize</c> : plusieurs
    /// utilisateurs virtuels peuvent partager un compte si le pool est plus petit que leur
    /// nombre, ce qui reste un comportement realiste.
    /// </summary>
    public int UserPoolSize { get; init; } = DEFAULT_USER_POOL_SIZE;

    /// <summary>Nombre maximal d'articles distincts ajoutes a un panier.</summary>
    public int MaxCartItems { get; init; } = DEFAULT_MAX_CART_ITEMS;

    /// <summary>Graine du generateur de donnees Bogus.</summary>
    public int RandomSeed { get; init; } = DEFAULT_RANDOM_SEED;

    /// <summary>Valide la coherence des reglages.</summary>
    /// <exception cref="ArgumentException">Un reglage est hors domaine.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(LoginPath))
        {
            throw new ArgumentException("LoginPath ne peut pas etre vide.", nameof(LoginPath));
        }

        if (string.IsNullOrWhiteSpace(ProductsPath))
        {
            throw new ArgumentException("ProductsPath ne peut pas etre vide.", nameof(ProductsPath));
        }

        if (string.IsNullOrWhiteSpace(CheckoutPath))
        {
            throw new ArgumentException("CheckoutPath ne peut pas etre vide.", nameof(CheckoutPath));
        }

        if (UserPoolSize < 1)
        {
            throw new ArgumentException("UserPoolSize doit valoir au moins 1.", nameof(UserPoolSize));
        }

        if (MaxCartItems < 1)
        {
            throw new ArgumentException("MaxCartItems doit valoir au moins 1.", nameof(MaxCartItems));
        }
    }
}