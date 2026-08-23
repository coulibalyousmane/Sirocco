using System.Threading.Channels;

namespace Sirocco.Application.Execution;

/// <summary>
/// Depose un jeton dans un canal borne, en bloquant si la file est pleine plutot que de le
/// perdre — partage par tout <see cref="ILoadScheduler"/> qui tourne sur son propre thread
/// dedie, ou le blocage synchrone est assume.
/// <para>
/// L'attente est <b>voulue</b> : elle signifie que tous les utilisateurs virtuels sont occupes.
/// Jeter le jeton a la place effacerait le probleme des statistiques — c'est exactement le biais
/// que Sirocco existe pour eviter.
/// </para>
/// </summary>
internal static class BlockingTokenWriter
{
    /// <summary>Depose <paramref name="token"/>, en attendant que la file se libere si elle est pleine.</summary>
    /// <returns><see langword="false"/> si le tir a ete annule ou la file close.</returns>
    public static bool TryEmit(
        ChannelWriter<ExecutionToken> tokens,
        in ExecutionToken token,
        CancellationToken cancellationToken)
    {
        if (tokens.TryWrite(token))
        {
            return true;
        }

        ExecutionToken pending = token;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Blocage synchrone assume : on est sur le thread dedie a l'ordonnancement.
                if (!tokens.WaitToWriteAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
                {
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (ChannelClosedException)
            {
                return false;
            }

            if (tokens.TryWrite(pending))
            {
                return true;
            }
        }

        return false;
    }
}