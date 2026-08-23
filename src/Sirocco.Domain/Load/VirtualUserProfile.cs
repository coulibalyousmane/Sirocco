using System.Collections.ObjectModel;

namespace Sirocco.Domain.Load;

/// <summary>
/// Profil complet d'utilisateurs virtuels : suite ordonnee de paliers decrivant l'evolution de
/// l'effectif concurrent dans le temps — le pendant, pour le modele ferme, du
/// <see cref="LoadProfile"/> (modele ouvert), qui decrit lui un debit plutot qu'un effectif.
/// <para>
/// Contrairement a <see cref="LoadProfile"/>, ce profil ne pilote aucun ordonnanceur de jetons :
/// il pilote la <i>concurrence</i> (le nombre de travailleurs actifs), consommee par
/// <c>RampingVirtualUserPool</c>. Le rythme d'emission des jetons reste independant, assure par
/// un <c>ClosedModelScheduler</c> configure sur <see cref="TotalDuration"/>.
/// </para>
/// </summary>
public sealed class VirtualUserProfile
{
    private readonly VirtualUserStage[] _stages;
    private readonly double[] _stageStartSeconds;

    /// <summary>Cree un profil a partir d'une suite de paliers.</summary>
    /// <exception cref="ArgumentException">La suite est vide.</exception>
    public VirtualUserProfile(IEnumerable<VirtualUserStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);

        _stages = [.. stages];
        if (_stages.Length == 0)
        {
            throw new ArgumentException("Un profil d'utilisateurs virtuels doit contenir au moins un palier.", nameof(stages));
        }

        _stageStartSeconds = new double[_stages.Length + 1];
        for (int i = 0; i < _stages.Length; i++)
        {
            _stageStartSeconds[i + 1] = _stageStartSeconds[i] + _stages[i].DurationSeconds;
        }

        Stages = new ReadOnlyCollection<VirtualUserStage>(_stages);
    }

    /// <summary>Paliers du profil, dans l'ordre d'execution.</summary>
    public IReadOnlyList<VirtualUserStage> Stages { get; }

    /// <summary>Duree totale du profil.</summary>
    public TimeSpan TotalDuration => TimeSpan.FromSeconds(TotalDurationSeconds);

    /// <summary>Duree totale du profil, en secondes.</summary>
    public double TotalDurationSeconds => _stageStartSeconds[^1];

    /// <summary>Effectif concurrent le plus eleve jamais vise par le profil.</summary>
    public int PeakVus
    {
        get
        {
            int peak = 0;
            foreach (VirtualUserStage stage in _stages)
            {
                peak = Math.Max(peak, Math.Max(stage.FromVus, stage.ToVus));
            }

            return peak;
        }
    }

    /// <summary>Effectif concurrent cible (arrondi) a <paramref name="elapsed"/> du debut du profil.</summary>
    public int VusAt(TimeSpan elapsed) => VusAt(elapsed.TotalSeconds);

    /// <summary>Effectif concurrent cible (arrondi) a <paramref name="elapsedSeconds"/> secondes du debut du profil.</summary>
    public int VusAt(double elapsedSeconds)
    {
        if (elapsedSeconds <= 0d)
        {
            return _stages[0].FromVus;
        }

        if (elapsedSeconds >= TotalDurationSeconds)
        {
            return _stages[^1].ToVus;
        }

        int index = LastIndexNotAfter(elapsedSeconds);
        double interpolated = _stages[index].VusAt(elapsedSeconds - _stageStartSeconds[index]);
        return (int)Math.Round(interpolated, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Index du dernier palier dont la borne de debut ne depasse pas <paramref name="value"/>.
    /// Meme logique que <see cref="LoadProfile"/> : parcours descendant, un nombre de paliers
    /// qui se compte sur les doigts d'une main.
    /// </summary>
    private int LastIndexNotAfter(double value)
    {
        for (int i = _stages.Length - 1; i >= 0; i--)
        {
            if (value >= _stageStartSeconds[i])
            {
                return i;
            }
        }

        return 0;
    }
}