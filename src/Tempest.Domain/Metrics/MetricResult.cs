using System.Runtime.CompilerServices;
using Tempest.Domain.Timing;

namespace Tempest.Domain.Metrics;

/// <summary>
/// Mesure immuable d'une etape executee. Structure <b>unmanaged</b> (48 octets, aucune
/// reference) : elle transite par un <c>Channel&lt;MetricResult&gt;</c> sans jamais
/// declencher d'allocation sur le tas ni de scan GC.
/// </summary>
/// <param name="Step">Etape concernee, resolue via <see cref="StepRegistry"/>.</param>
/// <param name="VirtualUserId">Utilisateur virtuel emetteur, pour le diagnostic.</param>
/// <param name="ScheduledTicks">
/// Instant theorique de depart impose par le profil de charge. C'est la reference
/// utilisee pour corriger le <i>coordinated omission</i>.
/// </param>
/// <param name="StartedTicks">Instant reel d'envoi de la requete.</param>
/// <param name="CompletedTicks">Instant de reception complete de la reponse.</param>
/// <param name="StatusCode">Code de statut protocolaire (HTTP), ou 0 si indisponible.</param>
/// <param name="Outcome">Issue de l'etape.</param>
/// <param name="BytesReceived">Volume de charge utile recu, en octets.</param>
public readonly record struct MetricResult(
    StepId Step,
    int VirtualUserId,
    long ScheduledTicks,
    long StartedTicks,
    long CompletedTicks,
    int StatusCode,
    RequestOutcome Outcome,
    long BytesReceived)
{
    /// <summary>Code de statut employe quand le protocole n'en fournit aucun.</summary>
    public const int NO_STATUS_CODE = 0;

    /// <summary>Volume recu employe quand la mesure ne porte sur aucune charge utile.</summary>
    public const long NO_PAYLOAD = 0L;

    /// <summary>
    /// Temps de service brut : ce que mesurent les outils naifs.
    /// Ignore le temps passe en file d'attente cote injecteur, donc <b>sous-estime</b>
    /// la latence percue par l'utilisateur des que le systeme sature.
    /// </summary>
    public long ServiceTicks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => CompletedTicks - StartedTicks;
    }

    /// <summary>
    /// Temps de reponse corrige du <i>coordinated omission</i> : mesure depuis l'instant
    /// ou la requete <b>aurait du</b> partir. C'est la valeur a publier dans les percentiles.
    /// </summary>
    public long ResponseTicks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => CompletedTicks - ScheduledTicks;
    }

    /// <summary>
    /// Dette d'ordonnancement : ecart entre depart theorique et depart reel.
    /// Une valeur qui derive a la hausse signifie que <b>l'injecteur</b> sature,
    /// pas forcement la cible : les resultats deviennent alors non representatifs.
    /// </summary>
    public long SchedulingDelayTicks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => StartedTicks - ScheduledTicks;
    }

    /// <summary>Indique si l'etape est un succes complet.</summary>
    public bool IsSuccess
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Outcome == RequestOutcome.Success;
    }

    /// <summary>Temps de service en millisecondes (confort de lecture, hors chemin critique).</summary>
    public double ServiceMilliseconds => TempestClock.ToMilliseconds(ServiceTicks);

    /// <summary>Temps de reponse corrige en millisecondes (hors chemin critique).</summary>
    public double ResponseMilliseconds => TempestClock.ToMilliseconds(ResponseTicks);

    /// <summary>Dette d'ordonnancement en millisecondes (hors chemin critique).</summary>
    public double SchedulingDelayMilliseconds => TempestClock.ToMilliseconds(SchedulingDelayTicks);

    /// <summary>
    /// Cree la mesure d'une iteration qui n'a jamais demarre parce que l'injecteur
    /// etait sature : le temps de reponse court malgre tout depuis l'instant theorique.
    /// </summary>
    public static MetricResult Dropped(StepId step, int virtualUserId, long scheduledTicks, long detectedAtTicks) =>
        new(
            step,
            virtualUserId,
            scheduledTicks,
            detectedAtTicks,
            detectedAtTicks,
            NO_STATUS_CODE,
            RequestOutcome.Dropped,
            NO_PAYLOAD);
}