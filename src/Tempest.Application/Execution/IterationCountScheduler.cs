using System.Threading.Channels;
using Tempest.Domain.Timing;

namespace Tempest.Application.Execution;

/// <summary>
/// Ordonnanceur qui emet exactement un nombre fixe de jetons, aussi vite que le permet la file,
/// puis s'arrete — par opposition a <see cref="ClosedModelScheduler"/>, qui s'arrete sur une
/// duree. Sert aux deux executeurs pilotes par un nombre d'iterations plutot qu'un temps :
/// <list type="bullet">
/// <item><b>Iterations partagees</b> : un seul jeu de N jetons, dispute par tous les utilisateurs
/// virtuels (premier arrive, premier servi) — <see cref="LoadTestOptions.MaxVirtualUsers"/> reste
/// un plafond de concurrence, comme en modele ouvert.</item>
/// <item><b>Iterations par utilisateur</b> : N = effectif x iterations souhaitees, combine avec
/// le plafond individuel <see cref="LoadTestOptions.IterationsPerVirtualUser"/> pour garantir que
/// chaque utilisateur virtuel en fait exactement sa part (voir la remarque de classe de
/// <see cref="VirtualUserWorker"/>).</item>
/// </list>
/// <para>
/// Contrairement au modele ferme a duree fixe, le nombre total est connu <b>avant</b> le tir :
/// <see cref="TokensPlanned"/> vaut donc ce total fixe, pas <see cref="TokensIssued"/> — une
/// annulation avant la fin se traduit alors correctement par
/// <see cref="LoadTestSummary.InjectorFellBehind"/>, exactement comme en modele ouvert.
/// </para>
/// </summary>
public sealed class IterationCountScheduler : ILoadScheduler
{
    private readonly long _totalIterations;

    private long _issued;
    private long _startTicks;

    /// <summary>Cree un ordonnanceur pour un nombre total d'iterations donne.</summary>
    /// <param name="totalIterations">Nombre de jetons a emettre au total.</param>
    public IterationCountScheduler(long totalIterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(totalIterations, 0L);

        _totalIterations = totalIterations;
    }

    /// <inheritdoc />
    public long TokensPlanned => _totalIterations;

    /// <inheritdoc />
    public long TokensIssued => Interlocked.Read(ref _issued);

    /// <inheritdoc />
    public long StartTicks => Interlocked.Read(ref _startTicks);

    /// <inheritdoc />
    public void Run(ChannelWriter<ExecutionToken> tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        Interlocked.Exchange(ref _startTicks, TempestClock.Now);

        long issued = 0L;

        while (issued < _totalIterations && !cancellationToken.IsCancellationRequested)
        {
            // Comme en modele ferme a duree fixe : l'instant du jeton est celui de son emission,
            // il n'existe pas d'echeance theorique anterieure a lui donner.
            ExecutionToken token = new(issued, TempestClock.Now);
            if (!BlockingTokenWriter.TryEmit(tokens, in token, cancellationToken))
            {
                break;
            }

            issued++;
            Interlocked.Exchange(ref _issued, issued);
        }
    }
}