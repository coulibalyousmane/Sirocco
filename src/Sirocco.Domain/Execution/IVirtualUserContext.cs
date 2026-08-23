using System.Net.WebSockets;
using Sirocco.Domain.Metrics;

namespace Sirocco.Domain.Execution;

/// <summary>
/// Contexte d'execution d'un utilisateur virtuel pour une iteration donnee.
/// <para>
/// Une instance est allouee <b>une fois par utilisateur virtuel</b> puis reutilisee a
/// chaque iteration : le scenario ne doit donc jamais la conserver au-dela de l'appel
/// a <see cref="IWorkflow.ExecuteAsync"/>.
/// </para>
/// </summary>
public interface IVirtualUserContext
{
    /// <summary>Index de l'utilisateur virtuel, stable pour toute la duree du test.</summary>
    int VirtualUserId { get; }

    /// <summary>Numero de l'iteration en cours pour cet utilisateur, base 0.</summary>
    long IterationNumber { get; }

    /// <summary>
    /// Instant theorique de depart de l'iteration, impose par le profil de charge.
    /// Sert de base au calcul du <i>coordinated omission</i>.
    /// </summary>
    long ScheduledTicks { get; }

    /// <summary>
    /// Client HTTP partage par tous les utilisateurs virtuels. Le pool de connexions
    /// est mutualise volontairement : c'est ce qui permet de tenir des dizaines de
    /// milliers de RPS sans epuiser les ports ephemeres.
    /// </summary>
    HttpClient HttpClient { get; }

    /// <summary>Jeton annule des que le test se termine ou qu'un seuil bloquant est franchi.</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Etat local persistant entre les iterations de <b>cet</b> utilisateur virtuel
    /// (jeton d'authentification, panier en cours, curseur de pagination...).
    /// <para>
    /// Une seule allocation par utilisateur virtuel pour tout le test :
    /// <c>var s = (MyState)(ctx.State ??= new MyState());</c>
    /// </para>
    /// </summary>
    object? State { get; set; }

    /// <summary>
    /// Ouvre le chronometrage d'une etape. Le scope retourne est une structure :
    /// aucune allocation, meme a 50 000 appels par seconde.
    /// </summary>
    StepScope BeginStep(StepId step);

    /// <summary>Publie une mesure deja construite (usage avance : protocoles non HTTP).</summary>
    void Report(in MetricResult result);

    /// <summary>
    /// Enregistre une valeur pour une metrique personnalisee (voir <see cref="CustomMetricRegistry"/>).
    /// L'interpretation de <paramref name="value"/> depend du type de la metrique — voir
    /// <see cref="CustomMetricSnapshot"/>. Sans effet par defaut : un contexte de test qui n'en a
    /// pas besoin n'a rien a implementer.
    /// </summary>
    void RecordCustomMetric(CustomMetricId metric, double value)
    {
    }

    /// <summary>
    /// Ouvre une connexion WebSocket vers <paramref name="uri"/>.
    /// <para>
    /// Cree une nouvelle <see cref="System.Net.WebSockets.ClientWebSocket"/> a chaque appel :
    /// il n'y a pas d'equivalent du pool de connexions de <see cref="HttpClient"/> pour ce
    /// protocole. Un scenario qui veut garder la connexion ouverte entre deux etapes doit
    /// la conserver lui-meme dans <see cref="State"/>.
    /// </para>
    /// </summary>
    /// <param name="uri">Adresse cible, schema <c>ws://</c> ou <c>wss://</c>.</param>
    /// <param name="configureOptions">Reglages optionnels appliques avant la connexion (en-tetes, sous-protocole...).</param>
    /// <param name="cancellationToken">Jeton d'annulation de la tentative de connexion.</param>
    Task<WebSocketConnection> ConnectWebSocketAsync(
        Uri uri,
        Action<ClientWebSocketOptions>? configureOptions,
        CancellationToken cancellationToken);
}