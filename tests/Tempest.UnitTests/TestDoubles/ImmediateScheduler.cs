using System.Threading.Channels;
using Tempest.Application.Execution;
using Tempest.Domain.Timing;

namespace Tempest.UnitTests.TestDoubles;

/// <summary>
/// Ordonnanceur de test qui emet un nombre fixe de jetons aussi vite que possible, tous
/// programmes au meme instant.
/// <para>
/// Permet de verifier la mecanique du moteur — decompte des iterations, gestion des erreurs,
/// abandon des jetons — sans dependre d'une horloge, donc sans test instable.
/// </para>
/// </summary>
/// <remarks>Cree l'ordonnanceur.</remarks>
/// <param name="tokenCount">Nombre de jetons a emettre.</param>
/// <param name="backdatedTicks">
/// Anteriorite artificielle des jetons : simule un injecteur deja en retard, sans
/// avoir a saturer reellement le banc d'utilisateurs virtuels.
/// </param>
internal sealed class ImmediateScheduler(long tokenCount, long backdatedTicks = 0L) : ILoadScheduler
{
    private readonly long _tokenCount = tokenCount;
    private readonly long _backdatedTicks = backdatedTicks;

    private long _issued;
    private long _startTicks;

    public long TokensPlanned => _tokenCount;

    public long TokensIssued => Interlocked.Read(ref _issued);

    public long StartTicks => Interlocked.Read(ref _startTicks);

    public void Run(ChannelWriter<ExecutionToken> tokens, CancellationToken cancellationToken)
    {
        long startTicks = TempestClock.Now;
        Interlocked.Exchange(ref _startTicks, startTicks);

        for (long index = 0; index < _tokenCount; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ExecutionToken token = new(index, startTicks - _backdatedTicks);

            while (!tokens.TryWrite(token))
            {
                try
                {
                    if (!tokens.WaitToWriteAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            Interlocked.Increment(ref _issued);
        }
    }
}