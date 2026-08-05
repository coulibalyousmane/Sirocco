using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bogus;
using Tempest.Domain.Execution;
using Tempest.Domain.Metrics;
using Tempest.Scenarios.Contracts;

namespace Tempest.Scenarios;

/// <summary>
/// Scenario de reference : connexion, consultation du catalogue, commande.
/// <para>
/// Trois etapes qui illustrent chacune une capacite du moteur : <see cref="DynamicCheckoutSteps.LOGIN"/>
/// maintient un jeton dans l'etat local de l'utilisateur virtuel et n'est rejoue qu'apres un 401 ;
/// <see cref="DynamicCheckoutSteps.BROWSE"/> fournit les identifiants de produits reellement connus
/// de la cible, sans coordination fragile entre deux processus independants ; <see cref="DynamicCheckoutSteps.CHECKOUT"/>
/// consomme ces identifiants pour composer un panier.
/// </para>
/// </summary>
public sealed class DynamicCheckoutWorkflow : IWorkflow
{
    /// <summary>Nom du scenario, utilise dans les rapports.</summary>
    public const string WORKFLOW_NAME = "dynamic-checkout";

    private readonly DynamicCheckoutWorkflowOptions _options;

    private UserAccount[] _users = [];
    private StepId _loginStep;
    private StepId _browseStep;
    private StepId _checkoutStep;

    /// <summary>Cree le scenario.</summary>
    /// <param name="options">Reglages ; valeurs par defaut si omis.</param>
    public DynamicCheckoutWorkflow(DynamicCheckoutWorkflowOptions? options = null)
    {
        _options = options ?? new DynamicCheckoutWorkflowOptions();
        _options.Validate();
    }

    /// <inheritdoc />
    public string Name => WORKFLOW_NAME;

    /// <inheritdoc />
    public void RegisterSteps(StepRegistry registry)
    {
        _loginStep = registry.Register(DynamicCheckoutSteps.LOGIN);
        _browseStep = registry.Register(DynamicCheckoutSteps.BROWSE);
        _checkoutStep = registry.Register(DynamicCheckoutSteps.CHECKOUT);
    }

    /// <summary>
    /// Pre-genere le pool de comptes de demonstration.
    /// <para>
    /// Une graine fixe rend le jeu de comptes identique d'un tir a l'autre : deux rapports
    /// restent comparables, et une anomalie observee lors d'un tir se reproduit au suivant.
    /// </para>
    /// </summary>
    public ValueTask SetUpAsync(CancellationToken cancellationToken)
    {
        Faker faker = new() { Random = new Randomizer(_options.RandomSeed) };

        _users = new UserAccount[_options.UserPoolSize];
        for (int i = 0; i < _users.Length; i++)
        {
            _users[i] = new UserAccount(faker.Internet.UserName(), faker.Internet.Password());
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        CheckoutSession session = (CheckoutSession)(context.State ??=
            new CheckoutSession(context.VirtualUserId % _users.Length));

        if (session.Token is null && !await LoginAsync(context, session, cancellationToken).ConfigureAwait(false))
        {
            // Sans jeton, ni la consultation ni la commande n'ont de sens pour cette iteration.
            return;
        }

        Product[]? products = await BrowseAsync(context, cancellationToken).ConfigureAwait(false);
        if (products is not { Length: > 0 })
        {
            // Aucun catalogue frais : rien de connu a mettre au panier.
            return;
        }

        await CheckoutAsync(context, session, products, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> LoginAsync(
        IVirtualUserContext context,
        CheckoutSession session,
        CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(_loginStep);
        UserAccount account = _users[session.UserIndex];

        HttpResponseMessage response;
        try
        {
            response = await context.HttpClient
                .PostAsJsonAsync(
                    _options.LoginPath,
                    new LoginRequest(account.Username, account.Password),
                    CheckoutJsonContext.Default.LoginRequest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            scope.Fail(RequestOutcome.ConnectionError);
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scope.Fail(RequestOutcome.Timeout);
            return false;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                scope.CompleteHttp((int)response.StatusCode);
                return false;
            }

            LoginResponse? body = await response.Content
                .ReadFromJsonAsync(CheckoutJsonContext.Default.LoginResponse, cancellationToken)
                .ConfigureAwait(false);

            if (body is null)
            {
                // Reponse 2xx mais corps illisible : un echec d'assertion, pas un echec de transport.
                scope.Fail(RequestOutcome.AssertionFailed, (int)response.StatusCode);
                return false;
            }

            session.Token = body.Token;
            scope.CompleteHttp((int)response.StatusCode, ContentLengthOf(response));
            return true;
        }
    }

    private async Task<Product[]?> BrowseAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(_browseStep);

        HttpResponseMessage response;
        try
        {
            response = await context.HttpClient
                .GetAsync(_options.ProductsPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            scope.Fail(RequestOutcome.ConnectionError);
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scope.Fail(RequestOutcome.Timeout);
            return null;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                scope.CompleteHttp((int)response.StatusCode);
                return null;
            }

            Product[]? products = await response.Content
                .ReadFromJsonAsync(CheckoutJsonContext.Default.ProductArray, cancellationToken)
                .ConfigureAwait(false);

            scope.CompleteHttp((int)response.StatusCode, ContentLengthOf(response));
            return products;
        }
    }

    private async Task CheckoutAsync(
        IVirtualUserContext context,
        CheckoutSession session,
        Product[] products,
        CancellationToken cancellationToken)
    {
        StepScope scope = context.BeginStep(_checkoutStep);

        using HttpRequestMessage request = new(HttpMethod.Post, _options.CheckoutPath)
        {
            Content = JsonContent.Create(
                new CheckoutRequest(BuildCart(products)),
                CheckoutJsonContext.Default.CheckoutRequest),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);

        HttpResponseMessage response;
        try
        {
            response = await context.HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            scope.Fail(RequestOutcome.ConnectionError);
            return;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scope.Fail(RequestOutcome.Timeout);
            return;
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Jeton expire ou revoque : la prochaine iteration se reconnectera.
                session.Token = null;
            }

            scope.CompleteHttp((int)response.StatusCode, ContentLengthOf(response));
        }
    }

    /// <summary>
    /// Compose un panier a partir du catalogue reellement recu : 1 a <see cref="DynamicCheckoutWorkflowOptions.MaxCartItems"/>
    /// lignes, choisies avec <see cref="Random.Shared"/> — sans etat par utilisateur virtuel,
    /// puisqu'il est thread-safe et lock-free depuis .NET 6.
    /// </summary>
    private CartItem[] BuildCart(Product[] products)
    {
        int itemCount = Math.Min(Random.Shared.Next(1, _options.MaxCartItems + 1), products.Length);
        CartItem[] cart = new CartItem[itemCount];

        for (int i = 0; i < itemCount; i++)
        {
            Product product = products[Random.Shared.Next(products.Length)];
            cart[i] = new CartItem(product.Id, Random.Shared.Next(1, 4));
        }

        return cart;
    }

    private static long ContentLengthOf(HttpResponseMessage response) =>
        response.Content.Headers.ContentLength.GetValueOrDefault();
}