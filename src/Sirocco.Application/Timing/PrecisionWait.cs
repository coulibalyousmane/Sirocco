using Sirocco.Domain.Timing;

namespace Sirocco.Application.Timing;

/// <summary>
/// Attente hybride sommeil / rotation active, destinee au thread d'ordonnancement.
/// <para>
/// <see cref="Thread.Sleep(int)"/> est soumis a la granularite du timer systeme
/// (jusqu'a ~15 ms sous Windows si aucun processus n'a abaisse la resolution) : viser
/// une echeance a 200 microsecondes avec un simple sommeil produirait une gigue superieure
/// a l'intervalle entre deux tirs des 5 000 RPS. On dort donc jusqu'a une marge de securite,
/// puis on termine en rotation active — precise a la microseconde, au prix d'un coeur occupe
/// pendant cette marge seulement.
/// </para>
/// </summary>
internal static class PrecisionWait
{
    /// <summary>Duree maximale d'un sommeil unitaire : garde la reactivite a l'annulation.</summary>
    private const int MAX_SLEEP_MILLISECONDS = 20;

    /// <summary>Desactive la degradation de <see cref="SpinWait.SpinOnce(int)"/> vers un sommeil.</summary>
    private const int NEVER_SLEEP_WHILE_SPINNING = -1;

    /// <summary>
    /// Bloque le thread appelant jusqu'a <paramref name="targetTicks"/>, ou jusqu'a l'annulation.
    /// </summary>
    /// <param name="targetTicks">Echeance, en ticks <see cref="SiroccoClock"/>.</param>
    /// <param name="spinThresholdTicks">Marge finale traitee en rotation active.</param>
    /// <param name="cancellationToken">Interrompt l'attente des son declenchement.</param>
    public static void Until(long targetTicks, long spinThresholdTicks, CancellationToken cancellationToken)
    {
        WaitHandle? handle = cancellationToken.CanBeCanceled ? cancellationToken.WaitHandle : null;

        while (true)
        {
            long remaining = targetTicks - SiroccoClock.Now;
            if (remaining <= spinThresholdTicks)
            {
                break;
            }

            int sleepMilliseconds = (int)SiroccoClock.ToMilliseconds(remaining - spinThresholdTicks);
            if (sleepMilliseconds <= 0)
            {
                break;
            }

            sleepMilliseconds = Math.Min(sleepMilliseconds, MAX_SLEEP_MILLISECONDS);

            if (handle is null)
            {
                Thread.Sleep(sleepMilliseconds);
            }
            else if (handle.WaitOne(sleepMilliseconds))
            {
                return;
            }
        }

        SpinWait spinner = default;
        while (SiroccoClock.Now < targetTicks)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Ne jamais degrader en Thread.Sleep(1) : cela reintroduirait la gigue du timer
            // systeme que toute cette methode existe pour contourner.
            spinner.SpinOnce(NEVER_SLEEP_WHILE_SPINNING);
        }
    }
}