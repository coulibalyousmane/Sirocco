// Meme parcours que scenarios/smoke-test.yaml (login -> browse -> checkout), mais scripte en
// C# plutot que declare en YAML — la decision structurante de la roadmap phase 2 (voir
// ROADMAP.md). Demontre deux choses que le format declaratif ne peut pas exprimer aussi
// simplement : une boucle de nouvelle tentative bornee sur "checkout", avec un arret anticipe
// dès qu'il ne s'agit plus d'une saturation temporaire (503) ; et un jeu de donnees
// (scenarios/users.csv) charge dans SetUpAsync, un identifiant reel par utilisateur virtuel
// plutot que "demo"/"demo" en dur pour tout le monde.
//
// Un script doit se terminer par une expression qui produit un IWorkflow : ici, la derniere
// ligne instancie la classe declaree juste au-dessus.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

public sealed class ScriptedCheckoutWorkflow : IWorkflow
{
    private StepId _loginStep;
    private StepId _browseStep;
    private StepId _checkoutStep;
    private DataSet? _users;

    public string Name => "scripted-checkout";

    public void RegisterSteps(StepRegistry registry)
    {
        _loginStep = registry.Register("login");
        _browseStep = registry.Register("browse");
        _checkoutStep = registry.Register("checkout");
    }

    public ValueTask SetUpAsync(CancellationToken cancellationToken)
    {
        _users = DataSetLoader.LoadFromFile("scenarios/users.csv", DataSetIterationStrategy.UniquePerVirtualUser);
        return ValueTask.CompletedTask;
    }

    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        // Jeton mis en cache par utilisateur virtuel, comme DynamicCheckoutWorkflow : "login"
        // n'est rejoue qu'a la premiere iteration de cet utilisateur virtuel.
        string? token = context.State as string;

        if (token is null)
        {
            IReadOnlyDictionary<string, string> user = _users!.Pick(context);

            StepScope loginScope = context.BeginStep(_loginStep);
            HttpResponseMessage loginResponse = await context.HttpClient.PostAsJsonAsync(
                "/api/auth/login", new { username = user["username"], password = user["password"] }, cancellationToken);
            string loginBody = await loginResponse.Content.ReadAsStringAsync(cancellationToken);
            loginScope.CompleteHttp((int)loginResponse.StatusCode);

            token = JsonNode.Parse(loginBody)?["token"]?.GetValue<string>();
            context.State = token;
        }

        StepScope browseScope = context.BeginStep(_browseStep);
        HttpResponseMessage browseResponse = await context.HttpClient.GetAsync("/api/catalog/products", cancellationToken);
        string browseBody = await browseResponse.Content.ReadAsStringAsync(cancellationToken);
        browseScope.CompleteHttp((int)browseResponse.StatusCode);

        JsonArray? products = JsonNode.Parse(browseBody) as JsonArray;
        int productId = products is { Count: > 0 } ? products[0]!["id"]!.GetValue<int>() : 1;

        StepScope checkoutScope = context.BeginStep(_checkoutStep);
        HttpResponseMessage checkoutResponse;
        int attempt = 0;
        do
        {
            HttpRequestMessage checkoutRequest = new(HttpMethod.Post, "/api/checkout")
            {
                Content = JsonContent.Create(new { items = new[] { new { productId, quantity = 1 } } }),
            };
            checkoutRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            checkoutResponse = await context.HttpClient.SendAsync(checkoutRequest, cancellationToken);
            attempt++;
        }
        while (checkoutResponse.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 3);

        checkoutScope.CompleteHttp((int)checkoutResponse.StatusCode);
    }
}

new ScriptedCheckoutWorkflow()
