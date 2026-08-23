using System.Threading.Channels;
using Sirocco.Domain.Load;
using Sirocco.Domain.Timing;

namespace Sirocco.Application.Execution;

/// <summary>
/// Pilote un banc d'utilisateurs virtuels dont l'effectif suit un <see cref="VirtualUserProfile"/>
/// au lieu de rester fixe pendant toute la duree du tir.
/// <para>
/// A la difference du plafond statique (<see cref="LoadTestOptions.MaxVirtualUsers"/> en modele
/// ouvert ou ferme classique), ici le nombre de travailleurs actifs varie dans le temps : cette
/// classe en cree de nouveaux quand l'effectif cible monte, et en arrete individuellement quand
/// il descend — sans jamais toucher au reste du banc. Ce qui rend l'arret individuel possible
/// sans fermer la file de jetons partagee : chaque travailleur recoit son propre jeton
/// d'annulation, lie a celui du tir, plutot que le jeton du tir directement.
/// </para>
/// <para>
/// L'emission des jetons reste independante de cette classe : un <see cref="ClosedModelScheduler"/>
/// configure sur <see cref="VirtualUserProfile.TotalDuration"/> continue d'alimenter la file en
/// continu, exactement comme pour un effectif fixe — seule la consommation varie ici.
/// </para>
/// </summary>
internal sealed class RampingVirtualUserPool
{
    private static readonly TimeSpan _tickInterval = TimeSpan.FromMilliseconds(100);

    private readonly VirtualUserProfile _profile;
    private readonly Func<int, VirtualUserWorker> _workerFactory;
    private readonly List<(VirtualUserWorker Worker, CancellationTokenSource Cts, Task Task)> _all = [];
    private readonly List<int> _activeIndices = [];

    /// <summary>Cree un pilote pour le profil donne.</summary>
    /// <param name="profile">Evolution de l'effectif concurrent dans le temps.</param>
    /// <param name="workerFactory">Cree le travailleur d'index donne (identifiant d'utilisateur virtuel).</param>
    public RampingVirtualUserPool(VirtualUserProfile profile, Func<int, VirtualUserWorker> workerFactory)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(workerFactory);

        _profile = profile;
        _workerFactory = workerFactory;
    }

    /// <summary>Tous les travailleurs crees pendant le tir, actifs ou deja arretes.</summary>
    public IReadOnlyList<VirtualUserWorker> Workers => _all.ConvertAll(static entry => entry.Worker);

    /// <summary>
    /// Deroule le profil jusqu'a son terme ou l'annulation, en ajustant l'effectif actif toutes
    /// les <see cref="_tickInterval"/>, puis attend l'arret complet de tous les travailleurs
    /// avant de rendre la main.
    /// </summary>
    public async Task RunAsync(ChannelReader<ExecutionToken> tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        long startTicks = SiroccoClock.Now;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                double elapsedSeconds = SiroccoClock.ToSeconds(SiroccoClock.Now - startTicks);
                if (elapsedSeconds >= _profile.TotalDurationSeconds)
                {
                    break;
                }

                Adjust(_profile.VusAt(elapsedSeconds), tokens, cancellationToken);

                await Task.Delay(_tickInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Arret demande : le nettoyage ci-dessous ramene l'effectif a zero dans tous les cas.
        }

        // Fin de profil ou annulation : plus aucun travailleur ne doit rester actif, y compris si
        // le dernier palier ne redescend pas lui-meme a zero.
        Adjust(0, tokens, cancellationToken);

        await Task.WhenAll(_all.Select(static entry => entry.Task)).ConfigureAwait(false);

        foreach ((_, CancellationTokenSource cts, _) in _all)
        {
            cts.Dispose();
        }
    }

    private void Adjust(int target, ChannelReader<ExecutionToken> tokens, CancellationToken cancellationToken)
    {
        while (_activeIndices.Count < target)
        {
            int index = _all.Count;
            VirtualUserWorker worker = _workerFactory(index);
            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task task = Task.Run(() => worker.RunAsync(tokens, cts.Token), CancellationToken.None);

            _all.Add((worker, cts, task));
            _activeIndices.Add(index);
        }

        while (_activeIndices.Count > target)
        {
            int lastPosition = _activeIndices.Count - 1;
            int index = _activeIndices[lastPosition];
            _activeIndices.RemoveAt(lastPosition);

            // Signale l'arret sans attendre : le travailleur termine son iteration en cours puis
            // sort de sa boucle (voir VirtualUserWorker.RunAsync), le Task est attendu plus tard.
            _all[index].Cts.Cancel();
        }
    }
}