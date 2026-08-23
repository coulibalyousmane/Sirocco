using System.Threading.Channels;

namespace Sirocco.Application.Execution;

/// <summary>
/// Source de rythme du tir : produit un <see cref="ExecutionToken"/> par requete planifiee.
/// <para>
/// Abstraire l'ordonnanceur permet au moteur de ne dependre que de la <i>cadence</i>, pas de
/// la facon dont elle est calculee : un profil de charge aujourd'hui, un pilotage distribue
/// par un maitre distant ou un rejeu de trafic enregistre demain, sans toucher au moteur.
/// C'est aussi ce qui rend le moteur testable avec une cadence deterministe.
/// </para>
/// </summary>
public interface ILoadScheduler
{
    /// <summary>Nombre de jetons que l'ordonnanceur prevoit d'emettre au total.</summary>
    long TokensPlanned { get; }

    /// <summary>Nombre de jetons deja emis.</summary>
    long TokensIssued { get; }

    /// <summary>Instant de demarrage du tir, en ticks monotones. Vaut 0 avant <see cref="Run"/>.</summary>
    long StartTicks { get; }

    /// <summary>
    /// Deroule la cadence jusqu'a epuisement ou annulation.
    /// <para>
    /// Appel <b>bloquant</b> : l'implementation est libre d'occuper son thread en rotation
    /// active pour tenir la precision. L'appelant doit lui reserver un thread dedie.
    /// </para>
    /// </summary>
    void Run(ChannelWriter<ExecutionToken> tokens, CancellationToken cancellationToken);
}