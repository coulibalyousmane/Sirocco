using System.Threading.Channels;

namespace Tempest.Application.Execution;

/// <summary>
/// Heberge un <see cref="ILoadScheduler"/> sur un thread dedie et remonte fidelement son echec.
/// <para>
/// Le <see cref="ThreadPool"/> est exclu volontairement : il se redimensionne par heuristique
/// et peut laisser une tache prete en attente pendant des centaines de millisecondes sous
/// charge. Une horloge de tir n'y survit pas — elle a besoin d'un thread a elle, en priorite
/// haute, qui ne fait que compter le temps.
/// </para>
/// </summary>
internal sealed class SchedulerThreadHost
{
    private const string THREAD_NAME = "tempest-scheduler";

    private readonly ILoadScheduler _scheduler;
    private readonly ChannelWriter<ExecutionToken> _tokens;

    private Thread? _thread;
    private Exception? _fault;

    /// <summary>Cree l'hote du thread d'ordonnancement.</summary>
    public SchedulerThreadHost(ILoadScheduler scheduler, ChannelWriter<ExecutionToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(tokens);

        _scheduler = scheduler;
        _tokens = tokens;
    }

    /// <summary>Demarre le thread d'ordonnancement.</summary>
    /// <exception cref="InvalidOperationException">L'hote a deja ete demarre.</exception>
    public void Start(CancellationToken cancellationToken)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Le thread d'ordonnancement a deja ete demarre.");
        }

        _thread = new Thread(() => RunScheduler(cancellationToken))
        {
            IsBackground = true,
            Name = THREAD_NAME,
            Priority = ThreadPriority.AboveNormal,
        };

        _thread.Start();
    }

    /// <summary>
    /// Attend la fin de l'ordonnancement et propage l'echec eventuel.
    /// <para>
    /// Sans cette propagation, un ordonnanceur qui plante rendrait un tir apparemment reussi
    /// mais silencieusement tronque : le pire resultat possible pour un outil de mesure.
    /// </para>
    /// </summary>
    public void WaitForCompletion()
    {
        _thread?.Join();

        if (_fault is not null)
        {
            throw new InvalidOperationException("Le thread d'ordonnancement a echoue.", _fault);
        }
    }

    private void RunScheduler(CancellationToken cancellationToken)
    {
        try
        {
            _scheduler.Run(_tokens, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Arret demande : sortie normale.
        }
        catch (Exception ex)
        {
            _fault = ex;
        }
        finally
        {
            // Toujours clore la file : sans cela les travailleurs attendraient indefiniment.
            _tokens.TryComplete();
        }
    }
}