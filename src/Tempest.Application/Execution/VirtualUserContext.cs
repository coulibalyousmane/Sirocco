using System.Net.WebSockets;
using Tempest.Domain.Execution;
using Tempest.Domain.Metrics;
using Tempest.Domain.Timing;

namespace Tempest.Application.Execution;

/// <summary>
/// Etat d'un utilisateur virtuel. Une instance par travailleur, <b>reutilisee</b> a chaque
/// iteration : le moteur n'alloue rien par requete.
/// <para>
/// Aucune synchronisation : un contexte n'est touche que par son propre travailleur.
/// </para>
/// </summary>
public sealed class VirtualUserContext : IVirtualUserContext
{
    private readonly IMetricSink _sink;
    private readonly StepId _iterationStep;

    private bool _firstStepPending;
    private RequestOutcome? _firstStepFailureThisIteration;

    /// <summary>Cree le contexte d'un utilisateur virtuel.</summary>
    /// <param name="virtualUserId">Index stable de l'utilisateur virtuel.</param>
    /// <param name="httpClient">Client HTTP partage par tout l'injecteur.</param>
    /// <param name="sink">Destination des mesures.</param>
    /// <param name="iterationStep">Etape technique portant la duree totale d'une iteration.</param>
    public VirtualUserContext(int virtualUserId, HttpClient httpClient, IMetricSink sink, StepId iterationStep)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(sink);

        VirtualUserId = virtualUserId;
        HttpClient = httpClient;
        _sink = sink;
        _iterationStep = iterationStep;
    }

    /// <inheritdoc />
    public int VirtualUserId { get; }

    /// <inheritdoc />
    public long IterationNumber { get; private set; } = -1;

    /// <inheritdoc />
    public long ScheduledTicks { get; private set; }

    /// <inheritdoc />
    public HttpClient HttpClient { get; }

    /// <inheritdoc />
    public CancellationToken CancellationToken { get; private set; }

    /// <inheritdoc />
    public object? State { get; set; }

    /// <summary>Nombre d'iterations demarrees par cet utilisateur virtuel.</summary>
    public long IterationsStarted { get; private set; }

    /// <summary>Nombre d'iterations terminees sans exception.</summary>
    public long IterationsCompleted { get; private set; }

    /// <summary>Nombre d'iterations interrompues par une exception du scenario.</summary>
    public long IterationsFailed { get; private set; }

    /// <summary>Nombre de jetons abandonnes pour cause de retard excessif.</summary>
    public long IterationsDropped { get; private set; }

    /// <summary>Nombre de mesures emises, etape technique d'iteration comprise.</summary>
    public long MetricsEmitted { get; private set; }

    /// <summary>Dette d'ordonnancement maximale observee, en ticks.</summary>
    public long MaxSchedulingDelayTicks { get; private set; }

    /// <summary>
    /// Premiere exception non geree levee par le scenario. Conservee pour le diagnostic :
    /// sans elle, un scenario casse produirait un tir vert avec 100 % d'echecs muets.
    /// </summary>
    public Exception? FirstScenarioError { get; private set; }

    /// <summary>Prepare le contexte pour une nouvelle iteration. Appele par le moteur.</summary>
    public void BeginIteration(in ExecutionToken token, long startedTicks, CancellationToken cancellationToken)
    {
        IterationNumber = token.IterationIndex;
        ScheduledTicks = token.ScheduledTicks;
        CancellationToken = cancellationToken;
        _firstStepPending = true;
        _firstStepFailureThisIteration = null;

        IterationsStarted++;

        long delay = startedTicks - token.ScheduledTicks;
        if (delay > MaxSchedulingDelayTicks)
        {
            MaxSchedulingDelayTicks = delay;
        }
    }

    /// <summary>
    /// Cloture l'iteration et publie l'etape technique <see cref="WellKnownSteps.ITERATION"/>,
    /// chronometree depuis l'instant de depart <b>theorique</b>.
    /// <para>
    /// L'issue publiee n'est pas toujours <paramref name="outcome"/> tel quel : un scenario
    /// peut choisir de ne pas lever d'exception quand une etape HTTP echoue (un 500 n'est pas
    /// un bug du scenario), auquel cas <paramref name="outcome"/> vaut <see cref="RequestOutcome.Success"/>
    /// alors qu'une etape a echoue. Dans ce cas, c'est cette premiere defaillance qui est
    /// retenue — sans quoi le rapport afficherait un test entierement vert malgre des
    /// requetes en erreur.
    /// </para>
    /// </summary>
    public void EndIteration(long startedTicks, RequestOutcome outcome)
    {
        RequestOutcome effectiveOutcome = outcome == RequestOutcome.Success
            ? _firstStepFailureThisIteration ?? RequestOutcome.Success
            : outcome;

        if (effectiveOutcome == RequestOutcome.Success)
        {
            IterationsCompleted++;
        }
        else
        {
            IterationsFailed++;
        }

        Report(new MetricResult(
            _iterationStep,
            VirtualUserId,
            ScheduledTicks,
            startedTicks,
            TempestClock.Now,
            MetricResult.NO_STATUS_CODE,
            effectiveOutcome,
            MetricResult.NO_PAYLOAD));
    }

    /// <summary>
    /// Abandonne un jeton arrive trop tard. La mesure est tout de meme publiee : une
    /// iteration jamais executee reste une seconde perdue pour l'utilisateur final,
    /// l'omettre des percentiles reintroduirait le biais qu'on cherche a supprimer.
    /// </summary>
    public void RecordDropped(in ExecutionToken token, long detectedAtTicks)
    {
        IterationsDropped++;

        long delay = detectedAtTicks - token.ScheduledTicks;
        if (delay > MaxSchedulingDelayTicks)
        {
            MaxSchedulingDelayTicks = delay;
        }

        Report(MetricResult.Dropped(_iterationStep, VirtualUserId, token.ScheduledTicks, detectedAtTicks));
    }

    /// <summary>Memorise la premiere exception non geree du scenario.</summary>
    public void RecordScenarioError(Exception error) => FirstScenarioError ??= error;

    /// <summary>
    /// Ouvre le chronometrage d'une etape.
    /// <para>
    /// La <b>premiere</b> etape d'une iteration herite de l'instant de depart theorique :
    /// c'est elle qui porte la dette accumulee par un injecteur en retard. Les etapes
    /// suivantes partent de leur propre instant reel — elles n'ont attendu que la reponse
    /// precedente, pas la file de l'injecteur, et leur imputer la dette la compterait deux fois.
    /// </para>
    /// </summary>
    public StepScope BeginStep(StepId step)
    {
        long now = TempestClock.Now;

        long scheduled;
        if (_firstStepPending)
        {
            _firstStepPending = false;
            scheduled = ScheduledTicks;
        }
        else
        {
            scheduled = now;
        }

        return new StepScope(this, step, scheduled, now);
    }

    /// <inheritdoc />
    public void Report(in MetricResult result)
    {
        MetricsEmitted++;

        if (result.Step != _iterationStep && result.Outcome != RequestOutcome.Success)
        {
            _firstStepFailureThisIteration ??= result.Outcome;
        }

        _sink.TryWrite(in result);
    }

    /// <inheritdoc />
    public async Task<WebSocketConnection> ConnectWebSocketAsync(
        Uri uri,
        Action<ClientWebSocketOptions>? configureOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        ClientWebSocket socket = new();
        configureOptions?.Invoke(socket.Options);

        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Une connexion qui echoue ne laisse jamais la socket entre les mains de
            // l'appelant : sans ce Dispose, elle fuirait, personne d'autre ne la liberant.
            socket.Dispose();
            throw;
        }

        return new WebSocketConnection(socket);
    }
}