using Tempest.Domain.Metrics;
using Tempest.Domain.Timing;

namespace Tempest.Domain.Execution;

/// <summary>
/// Chronometre d'une etape. Structure en lecture seule, donc allouee sur la pile :
/// ouvrir et fermer une etape ne coute rien au GC.
/// <para>
/// Le scope n'est volontairement <b>pas</b> <see cref="IDisposable"/> : une etape doit
/// declarer explicitement son issue (<see cref="Success"/> / <see cref="Fail"/>), sinon
/// un <c>using</c> oublie enregistrerait silencieusement un faux succes.
/// </para>
/// </summary>
/// <remarks>Cree un scope. Appele par l'implementation du contexte, pas par les scenarios.</remarks>
/// <param name="context">Contexte destinataire de la mesure.</param>
/// <param name="step">Etape chronometree.</param>
/// <param name="scheduledTicks">Instant theorique de depart de l'etape.</param>
/// <param name="startedTicks">Instant reel de depart de l'etape.</param>
public readonly struct StepScope(IVirtualUserContext context, StepId step, long scheduledTicks, long startedTicks)
{
    /// <summary>Code de statut applique par defaut a un succes declare sans precision.</summary>
    public const int DEFAULT_SUCCESS_STATUS_CODE = 200;

    /// <summary>Borne basse incluse de la plage de succes HTTP.</summary>
    private const int FIRST_SUCCESS_STATUS_CODE = 200;

    /// <summary>Borne haute exclue de la plage de succes HTTP.</summary>
    private const int FIRST_REDIRECTION_STATUS_CODE = 300;

    private readonly IVirtualUserContext? _context = context;

    /// <summary>Etape chronometree.</summary>
    public StepId Step { get; } = step;

    /// <summary>Instant theorique de depart.</summary>
    public long ScheduledTicks { get; } = scheduledTicks;

    /// <summary>Instant reel de depart.</summary>
    public long StartedTicks { get; } = startedTicks;

    /// <summary>Duree ecoulee depuis le debut de l'etape, en ticks.</summary>
    public long ElapsedTicks => TempestClock.Now - StartedTicks;

    /// <summary>Enregistre un succes.</summary>
    public void Success(int statusCode = DEFAULT_SUCCESS_STATUS_CODE, long bytesReceived = MetricResult.NO_PAYLOAD) =>
        Complete(RequestOutcome.Success, statusCode, bytesReceived);

    /// <summary>Enregistre un echec.</summary>
    public void Fail(
        RequestOutcome outcome,
        int statusCode = MetricResult.NO_STATUS_CODE,
        long bytesReceived = MetricResult.NO_PAYLOAD) =>
        Complete(outcome, statusCode, bytesReceived);

    /// <summary>
    /// Classe un code de statut HTTP selon l'heuristique usuelle : succes si la plage est 2xx,
    /// <see cref="RequestOutcome.HttpError"/> sinon (une redirection 3xx comprise — si le
    /// scenario ne la suit pas, l'utilisateur n'a pas obtenu la ressource demandee).
    /// <para>
    /// Expose separement de <see cref="CompleteHttp"/> pour les appelants qui doivent composer
    /// cette issue avec une autre logique (ex. une extraction de valeur manquee) avant de
    /// publier la mesure une seule fois.
    /// </para>
    /// </summary>
    public static RequestOutcome ClassifyHttp(int statusCode) =>
        statusCode is >= FIRST_SUCCESS_STATUS_CODE and < FIRST_REDIRECTION_STATUS_CODE
            ? RequestOutcome.Success
            : RequestOutcome.HttpError;

    /// <summary>Enregistre l'issue d'une reponse HTTP via <see cref="ClassifyHttp"/>.</summary>
    public void CompleteHttp(int statusCode, long bytesReceived = MetricResult.NO_PAYLOAD) =>
        Complete(ClassifyHttp(statusCode), statusCode, bytesReceived);

    /// <summary>Cloture l'etape et publie la mesure.</summary>
    public void Complete(RequestOutcome outcome, int statusCode, long bytesReceived)
    {
        if (_context is null)
        {
            return;
        }

        _context.Report(new MetricResult(
            Step,
            _context.VirtualUserId,
            ScheduledTicks,
            StartedTicks,
            TempestClock.Now,
            statusCode,
            outcome,
            bytesReceived));
    }
}