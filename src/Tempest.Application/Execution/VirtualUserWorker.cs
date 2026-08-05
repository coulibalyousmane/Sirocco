using System.Threading.Channels;
using Tempest.Domain.Execution;
using Tempest.Domain.Metrics;
using Tempest.Domain.Timing;

namespace Tempest.Application.Execution;

/// <summary>
/// Boucle de consommation d'un utilisateur virtuel : prendre un jeton, executer une iteration
/// du scenario, publier la mesure, recommencer.
/// <para>
/// Separee du moteur pour que celui-ci ne fasse qu'orchestrer : le moteur decide <i>combien</i>
/// de travailleurs et <i>quand</i> ils s'arretent, le travailleur decide ce qui se passe pour
/// une iteration. Les deux evoluent pour des raisons differentes.
/// </para>
/// </summary>
internal sealed class VirtualUserWorker
{
    private readonly IWorkflow _workflow;
    private readonly long _maxSchedulingDelayTicks;

    /// <summary>Cree un travailleur.</summary>
    /// <param name="context">Contexte de l'utilisateur virtuel, reutilise a chaque iteration.</param>
    /// <param name="workflow">Scenario a executer.</param>
    /// <param name="maxSchedulingDelayTicks">Retard au-dela duquel un jeton est abandonne ; 0 pour ne jamais abandonner.</param>
    public VirtualUserWorker(VirtualUserContext context, IWorkflow workflow, long maxSchedulingDelayTicks)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(workflow);

        Context = context;
        _workflow = workflow;
        _maxSchedulingDelayTicks = maxSchedulingDelayTicks;
    }

    /// <summary>Contexte de l'utilisateur virtuel, porteur des compteurs du travailleur.</summary>
    public VirtualUserContext Context { get; }

    /// <summary>Consomme des jetons jusqu'a cloture de la file ou annulation du tir.</summary>
    public async Task RunAsync(ChannelReader<ExecutionToken> tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);

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

            if (!await ExecuteIterationAsync(token, cancellationToken).ConfigureAwait(false))
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
        long startedTicks = TempestClock.Now;

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