using System.Buffers;
using System.Text;
using MQTTnet;
using MQTTnet.Exceptions;
using Tempest.Domain.Execution;
using Tempest.Domain.Metrics;

namespace Tempest.Extensions.Mqtt;

/// <summary>
/// Troisieme protocole de reference de la roadmap phase 6 : contrairement a
/// <c>Tempest.Extensions.Sse</c>, qui restait au-dessus de HTTP, celui-ci revient a un protocole
/// reellement different comme <c>Tempest.Extensions.Sql</c> — mais oriente publication/abonnement
/// plutot que requete/reponse. Chaque iteration s'abonne a un sujet qui lui est propre, y publie un
/// message, puis attend sa propre reception : le round-trip complet (aller MQTT jusqu'au courtier,
/// retour jusqu'au meme client) est ce que mesure la seconde etape, pas un simple accuse de
/// publication.
/// <para>
/// Sujet propre a chaque iteration (<c>{prefixe}/{utilisateur}/{iteration}</c>) plutot qu'un sujet
/// partage : sans cela, un utilisateur virtuel pourrait recevoir le message publie par un autre,
/// rendant le round-trip mesure non attribuable a la bonne iteration.
/// </para>
/// <para>
/// Limite assumee, comme les protocoles de reference precedents : aucune configuration injectee par
/// Tempest, ce plugin lit la sienne (hote, port, prefixe de sujet, delai maximal par iteration)
/// depuis des variables d'environnement.
/// </para>
/// <para>
/// Confirmation reelle plutot que nouvelle trouvaille : comme <c>Tempest.Extensions.Sql</c>, ce
/// plugin doit etre <b>publie</b> (<c>dotnet publish</c>), pas seulement compile — vrai meme ici ou
/// la seule dependance ajoutee (<c>MQTTnet</c>) est geree, sans composant natif. Un <c>dotnet
/// build</c> seul charge le type sans erreur (<c>PluginWorkflowLoader</c> resout la reflexion), mais
/// <c>ExecuteAsync</c> echoue des le premier acces a un type MQTTnet (le constructeur statique
/// initialisant <see cref="_factory"/>), faute de trouver <c>MQTTnet.dll</c> a cote de l'assembly —
/// confirme en isolant le probleme via un harnais direct (memes appels, sans passer par
/// <c>Assembly.LoadFrom</c>) avant de le reproduire puis de le corriger via publication.
/// </para>
/// </summary>
public sealed class MqttWorkflow : IWorkflow
{
    private const string HOST_ENVIRONMENT_VARIABLE = "TEMPEST_MQTT_PLUGIN_HOST";
    private const string PORT_ENVIRONMENT_VARIABLE = "TEMPEST_MQTT_PLUGIN_PORT";
    private const string TOPIC_PREFIX_ENVIRONMENT_VARIABLE = "TEMPEST_MQTT_PLUGIN_TOPIC_PREFIX";
    private const string TIMEOUT_SECONDS_ENVIRONMENT_VARIABLE = "TEMPEST_MQTT_PLUGIN_TIMEOUT_SECONDS";
    private const string DEFAULT_HOST = "localhost";
    private const int DEFAULT_PORT = 1883;
    private const string DEFAULT_TOPIC_PREFIX = "tempest/mqtt-plugin";
    private const int DEFAULT_TIMEOUT_SECONDS = 10;

    private static readonly MqttClientFactory _factory = new();

    private readonly string _host;
    private readonly int _port;
    private readonly string _topicPrefix;
    private readonly TimeSpan _timeout;

    private StepId _connectStep;
    private StepId _publishReceiveStep;

    public MqttWorkflow()
    {
        _host = Environment.GetEnvironmentVariable(HOST_ENVIRONMENT_VARIABLE) is { Length: > 0 } configuredHost
            ? configuredHost
            : DEFAULT_HOST;

        _port = Environment.GetEnvironmentVariable(PORT_ENVIRONMENT_VARIABLE) is { Length: > 0 } configuredPort
            && int.TryParse(configuredPort, out int parsedPort) && parsedPort > 0
            ? parsedPort
            : DEFAULT_PORT;

        _topicPrefix = Environment.GetEnvironmentVariable(TOPIC_PREFIX_ENVIRONMENT_VARIABLE) is { Length: > 0 } configuredPrefix
            ? configuredPrefix
            : DEFAULT_TOPIC_PREFIX;

        int timeoutSeconds = Environment.GetEnvironmentVariable(TIMEOUT_SECONDS_ENVIRONMENT_VARIABLE) is { Length: > 0 } configuredTimeout
            && int.TryParse(configuredTimeout, out int parsedTimeout) && parsedTimeout > 0
            ? parsedTimeout
            : DEFAULT_TIMEOUT_SECONDS;
        _timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }

    /// <inheritdoc />
    public string Name => "mqtt-plugin";

    /// <inheritdoc />
    public void RegisterSteps(StepRegistry registry)
    {
        _connectStep = registry.Register("MQTT connect");
        _publishReceiveStep = registry.Register("MQTT publish/receive");
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(IVirtualUserContext context, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        IMqttClient? client = await ConnectAsync(context, cancellationToken, timeoutSource.Token).ConfigureAwait(false);
        if (client is null)
        {
            return;
        }

        try
        {
            await PublishReceiveAsync(context, client, cancellationToken, timeoutSource.Token).ConfigureAwait(false);
        }
        finally
        {
            await DisconnectQuietlyAsync(client).ConfigureAwait(false);
            client.Dispose();
        }
    }

    private async ValueTask<IMqttClient?> ConnectAsync(
        IVirtualUserContext context,
        CancellationToken cancellationToken,
        CancellationToken connectCancellationToken)
    {
        StepScope scope = context.BeginStep(_connectStep);
        IMqttClient client = _factory.CreateMqttClient();

        try
        {
            MqttClientOptions options = new MqttClientOptionsBuilder()
                .WithTcpServer(_host, _port)
                .WithClientId($"tempest-mqtt-{context.VirtualUserId}-{Guid.NewGuid():N}")
                .Build();

            await client.ConnectAsync(options, connectCancellationToken).ConfigureAwait(false);
            scope.Success();
            return client;
        }
        catch (MqttCommunicationException)
        {
            client.Dispose();
            scope.Fail(RequestOutcome.ConnectionError);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            scope.Fail(RequestOutcome.Timeout);
            return null;
        }
    }

    private async ValueTask PublishReceiveAsync(
        IVirtualUserContext context,
        IMqttClient client,
        CancellationToken cancellationToken,
        CancellationToken readCancellationToken)
    {
        StepScope scope = context.BeginStep(_publishReceiveStep);
        string topic = $"{_topicPrefix}/{context.VirtualUserId}/{context.IterationNumber}";
        string expectedPayload = Guid.NewGuid().ToString("N");
        TaskCompletionSource<string> received = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs args)
        {
            if (args.ApplicationMessage.Topic == topic)
            {
                received.TrySetResult(Encoding.UTF8.GetString(BuffersExtensions.ToArray(args.ApplicationMessage.Payload)));
            }

            return Task.CompletedTask;
        }

        client.ApplicationMessageReceivedAsync += OnMessageReceived;

        try
        {
            MqttClientSubscribeOptions subscribeOptions = _factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(topic)
                .Build();
            await client.SubscribeAsync(subscribeOptions, readCancellationToken).ConfigureAwait(false);

            MqttApplicationMessage message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(expectedPayload)
                .Build();
            await client.PublishAsync(message, readCancellationToken).ConfigureAwait(false);

            await using (readCancellationToken.Register(static state => ((TaskCompletionSource<string>)state!).TrySetCanceled(), received).ConfigureAwait(false))
            {
                string actualPayload = await received.Task.ConfigureAwait(false);
                if (actualPayload != expectedPayload)
                {
                    // Le round-trip a bien eu lieu mais avec un contenu different : une assertion
                    // metier ratee, pas un incident de transport.
                    scope.Fail(RequestOutcome.AssertionFailed);
                    return;
                }
            }

            scope.Success();
        }
        catch (MqttCommunicationException)
        {
            scope.Fail(RequestOutcome.ConnectionError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            scope.Fail(RequestOutcome.Timeout);
        }
        finally
        {
            client.ApplicationMessageReceivedAsync -= OnMessageReceived;
        }
    }

    private static async Task DisconnectQuietlyAsync(IMqttClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync().ConfigureAwait(false);
            }
        }
        catch (MqttCommunicationException)
        {
            // Deja deconnecte ou connexion perdue entre-temps : rien de plus a nettoyer.
        }
    }
}