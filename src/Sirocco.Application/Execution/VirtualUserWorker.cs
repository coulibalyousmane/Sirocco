using System.Threading.Channels;
using Sirocco.Domain.Execution;
using Sirocco.Domain.Metrics;
using Sirocco.Domain.Timing;

namespace Sirocco.Application.Execution;

/// <summary>
/// Boucle de consommation d'un utilisateur virtuel : prendre un jeton, executer une iteration
/// du scenario, publier la mesure, recommencer.
/// <para>
/// Separee du moteur pour que celui-ci ne fasse qu'orchestrer : le moteur decide <i>combien</i>
/// de travailleurs et <i>quand</i> ils s'arretent, le travailleur decide ce qui se passe pour
/// une iteration. Les deux evoluent pour des raisons differentes.
/// </para>
/// <para>
/// <b>Iterations par utilisateur.</b> Quand <see cref="LoadTestOptions.IterationsPerVirtualUser"/>
/// est renseigne, ce travailleur s'arrete de lui-meme apres en avoir personnellement traite
/// exactement ce nombre, sans jamais fermer la file partagee par les autres. Combine avec un
/// <see cref="IterationCountScheduler"/> emettant exactement effectif x quota jetons, cet
/// auto-arret garantit — par construction, aucun travailleur ne peut en prendre plus que son
/// quota et le total emis egale exactement la somme des quotas — que chaque utilisateur virtuel
/// en fait exactement sa part, plutot qu'une repartition inegale au gre de qui repond le plus
/// vite au canal partage.
/// </para>
/// </summary>
internal sealed class VirtualUserWorker
{
    private readonly IWorkflow _workflow;
    private readonly long _maxSchedulingDelayTicks;
    private readonly long? _maxIterations;
    private readonly ActiveVirtualUserGauge? _activeVirtualUsers;

    /// <summary>Cree un travailleur.</summary>
    /// <param name="context">Contexte de l'utilisateur virtuel, reutilise a chaque iteration.</param>
    /// <param name="workflow">Scenario a executer.</param>
    /// <param name="maxSchedulingDelayTicks">Retard au-dela duquel un jeton est abandonne ; 0 pour ne jamais abandonner.</param>
    /// <param name="maxIterations">
    /// Nombre d'iterations personnelles au-dela duquel ce travailleur s'arrete de lui-meme.
    /// <see langword="null"/> (defaut) : aucune limite propre, seule la fermeture de la file ou
    /// l'annulation du tir l'arrete.
    /// </param>
    /// <param name="activeVirtualUsers">
    /// Jauge partagee a incrementer pendant que ce travailleur consomme des jetons.
    /// <see langword="null"/> (defaut) : aucun suivi, comportement inchange pour tout appelant qui
    /// n'a pas besoin de la concurrence reelle dans le temps.
    /// </param>
    public VirtualUserWorker(
        VirtualUserContext context,
        IWorkflow workflow,
        long maxSchedulingDelayTicks,
        long? maxIterations = null,
        ActiveVirtualUserGauge? activeVirtualUsers = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workflow);

        Context = context;
        _workflow = workflow;
        _maxSchedulingDelayTicks = maxSchedulingDelayTicks;
        _maxIterations = maxIterations;
        _activeVirtualUsers = activeVirtualUsers;
    }

    /// <summary>Contexte de l'utilisateur virtuel, porteur des compteurs du travailleur.</summary>
    public VirtualUserContext Context { get; }

    /// <summary>Consomme des jetons jusqu'a cloture de la file, annulation du tir, ou quota personnel atteint.</summary>
    public async Task RunAsync(ChannelReader<ExecutionToken> tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        _activeVirtualUsers?.Increment();
        try
        {
            await ConsumeAsync(tokens, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _activeVirtualUsers?.Decrement();
        }
    }

    private async Task ConsumeAsync(ChannelReader<ExecutionToken> tokens, CancellationToken cancellationToken)
    {
        long processed = 0L;

        while (true)
        {
            // TryRead d'abord : quand la file n'est pas vide — le cas nominal sous charge —
            // on evite entierement la machinerie asynchrone.
            if (!tokens.TryRead(out ExecutionToken token))
            {
                try
                {
                    // ReadAsync, surtout pas WaitToReadAsync : ce dernier reveille *tous* les
                    // lecteurs en attente a chaque jeton ecrit, et les N-1 perdants se
                    // reinscrivent aussitot. Le cout d'inscription devient O(utilisateurs
                    // virtuels) par requete — mesure a 12 Ko alloues par iteration avec
                    // 256 utilisateurs. ReadAsync ne reveille que le lecteur servi.
                    token = await tokens.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ChannelClosedException)
                {
                    // Fin de tir : l'ordonnanceur a cloture la file.
                    return;
                }
            }

            bool keepRunning = await ExecuteIterationAsync(token, cancellationToken).ConfigureAwait(false);
            processed++;

            if (!keepRunning || (_maxIterations is { } max && processed >= max))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Execute une iteration complete.
    /// <para>
    /// Le jeton est passe par valeur et non par <c>in</c> : une methode <c>async</c> ne peut
    /// pas prendre de reference. Le cout est nul en pratique — la structure tient sur
    /// 16 octets, soit deux registres.
    /// </para>
    /// </summary>
    /// <returns><see langword="false"/> si le travailleur doit s'arreter.</returns>
    private async ValueTask<bool> ExecuteIterationAsync(ExecutionToken token, CancellationToken cancellationToken)
    {
        long startedTicks = SiroccoClock.Now;

        if (_maxSchedulingDelayTicks > 0 && (startedTicks - token.ScheduledTicks) > _maxSchedulingDelayTicks)
        {
            Context.RecordDropped(in token, startedTicks);
            return true;
        }

        Context.BeginIteration(in token, startedTicks, cancellationToken);

        RequestOutcome outcome = RequestOutcome.Success;
        try
        {
            await _workflow.ExecuteAsync(Context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Context.EndIteration(startedTicks, RequestOutcome.Cancelled);
            return false;
        }
        catch (Exception ex)
        {
            // Un scenario qui leve ne doit jamais tuer l'injecteur : on compte,
            // on garde la premiere trace, et le tir continue.
            Context.RecordScenarioError(ex);
            outcome = RequestOutcome.ScenarioError;
        }

        Context.EndIteration(startedTicks, outcome);

        return !cancellationToken.IsCancellationRequested;
    }
}