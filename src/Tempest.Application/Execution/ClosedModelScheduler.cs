using System.Threading.Channels;
using Tempest.Domain.Timing;

namespace Tempest.Application.Execution;

/// <summary>
/// Ordonnanceur du modele <b>ferme</b> : un nombre fixe d'utilisateurs virtuels enchainent les
/// iterations sans aucune pause imposee, jusqu'a l'expiration d'une duree — « exactement N
/// utilisateurs simultanes », par opposition au modele ouvert (<see cref="CoordinatedRateLimiter"/>)
/// qui vise un debit independant de la latence de la cible.
/// <para>
/// A l'oppose du modele ouvert, il n'existe ici aucun echeancier theorique a comparer a
/// l'instant d'execution reel : chaque jeton porte l'instant ou il a ete emis, jamais un instant
/// planifie a l'avance. Le debit resultant depend entierement de la latence de la cible —
/// exactement l'effet de biais (<i>coordinated omission</i>) que le modele ouvert existe pour
/// eviter (voir le README, "Décisions structurantes"). C'est pourquoi ce modele exige une mise
/// en garde explicite dans le rapport (<see cref="Domain.Metrics.LoadTestReport.ClosedModel"/>)
/// plutot que d'etre presente comme equivalent au modele ouvert.
/// </para>
/// <para>
/// La concurrence n'est pas un parametre de cette classe : elle vient du nombre d'utilisateurs
/// virtuels du moteur (<see cref="LoadTestOptions.MaxVirtualUsers"/>), qui borne deja le nombre
/// de travailleurs consommant le canal de jetons. Cet ordonnanceur se contente d'emettre en
/// continu ; la file bornee entre lui et les travailleurs absorbe le reste — un nouveau jeton
/// n'est ecrit que lorsqu'un utilisateur virtuel vient de se liberer, ce qui suffit a faire
/// emerger le modele ferme sans lui dedier de mecanisme de synchronisation.
/// </para>
/// </summary>
public sealed class ClosedModelScheduler : ILoadScheduler
{
    private readonly long _durationTicks;

    private long _issued;
    private long _startTicks;

    /// <summary>Cree un ordonnanceur pour une duree donnee.</summary>
    /// <param name="duration">Duree totale du palier, jusqu'a laquelle de nouvelles iterations sont emises.</param>
    public ClosedModelScheduler(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        _durationTicks = TempestClock.FromTimeSpan(duration);
    }

    /// <summary>
    /// Toujours egal a <see cref="TokensIssued"/> : le modele ferme ne planifie rien a l'avance,
    /// contrairement au modele ouvert ou un ecart entre planifie et emis signale un injecteur
    /// depasse (<see cref="Execution.LoadTestSummary.InjectorFellBehind"/>). Cette notion n'a
    /// pas de sens ici.
    /// </summary>
    public long TokensPlanned => TokensIssued;

    /// <inheritdoc />
    public long TokensIssued => Interlocked.Read(ref _issued);

    /// <inheritdoc />
    public long StartTicks => Interlocked.Read(ref _startTicks);

    /// <inheritdoc />
    public void Run(ChannelWriter<ExecutionToken> tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        long startTicks = TempestClock.Now;
        Interlocked.Exchange(ref _startTicks, startTicks);
        long endTicks = startTicks + _durationTicks;

        long issued = 0L;

        while (TempestClock.Now < endTicks && !cancellationToken.IsCancellationRequested)
        {
            // L'instant du jeton est celui de son emission : en modele ferme, il n'existe pas
            // d'echeance theorique anterieure a lui donner (voir la doc de classe).
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