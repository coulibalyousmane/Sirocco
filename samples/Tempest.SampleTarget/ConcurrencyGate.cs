namespace Tempest.SampleTarget;

/// <summary>
/// Simule la capacite finie d'un vrai backend : un nombre borne de commandes s'executent
/// en meme temps ; au-dela, une requete patiente une courte duree puis est refusee plutot
/// que d'attendre indefiniment — c'est ce qui rend cette cible capable de saturer sous une
/// charge suffisante, condition necessaire pour observer le <i>coordinated omission</i>
/// cote injecteur.
/// </summary>
internal sealed class ConcurrencyGate(int maxConcurrent) : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(maxConcurrent, maxConcurrent);

    /// <summary>Tente d'obtenir une place, en attendant au plus <paramref name="maxWait"/>.</summary>
    /// <returns><see langword="false"/> si aucune place ne s'est liberee a temps.</returns>
    public Task<bool> TryEnterAsync(TimeSpan maxWait, CancellationToken cancellationToken) =>
        _semaphore.WaitAsync(maxWait, cancellationToken);

    /// <summary>Libere la place occupee par la requete courante.</summary>
    public void Exit() => _semaphore.Release();

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();
}