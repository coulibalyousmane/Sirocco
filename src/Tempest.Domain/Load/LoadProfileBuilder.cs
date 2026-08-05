namespace Tempest.Domain.Load;

/// <summary>
/// Composition fluide d'un <see cref="LoadProfile"/>.
/// <para>
/// Chaque palier enchaine sur le debit final du precedent, ce qui evite les
/// discontinuites involontaires dans la courbe de charge.
/// </para>
/// <example>
/// <code>
/// var profile = LoadProfile.Create()
///     .RampTo(5_000, TimeSpan.FromSeconds(30))
///     .Sustain(TimeSpan.FromMinutes(5))
///     .RampTo(0, TimeSpan.FromSeconds(15))
///     .Build();
/// </code>
/// </example>
/// </summary>
public sealed class LoadProfileBuilder
{
    private readonly List<LoadStage> _stages = [];
    private double _currentRps;

    /// <summary>Definit le debit de depart, sans consommer de temps. A appeler en premier.</summary>
    public LoadProfileBuilder StartAt(double rps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rps);

        if (_stages.Count > 0)
        {
            throw new InvalidOperationException("StartAt doit etre appele avant tout palier.");
        }

        _currentRps = rps;
        return this;
    }

    /// <summary>Ajoute une rampe depuis le debit courant vers <paramref name="rps"/>.</summary>
    public LoadProfileBuilder RampTo(double rps, TimeSpan duration)
    {
        _stages.Add(LoadStage.Ramp(_currentRps, rps, duration));
        _currentRps = rps;
        return this;
    }

    /// <summary>Maintient le debit courant pendant <paramref name="duration"/>.</summary>
    public LoadProfileBuilder Sustain(TimeSpan duration)
    {
        _stages.Add(LoadStage.Constant(_currentRps, duration));
        return this;
    }

    /// <summary>Ajoute un palier a debit constant, en rompant avec le debit courant.</summary>
    public LoadProfileBuilder Constant(double rps, TimeSpan duration)
    {
        _stages.Add(LoadStage.Constant(rps, duration));
        _currentRps = rps;
        return this;
    }

    /// <summary>Ajoute un palier deja construit.</summary>
    public LoadProfileBuilder Add(LoadStage stage)
    {
        _stages.Add(stage);
        _currentRps = stage.ToRps;
        return this;
    }

    /// <summary>Materialise le profil.</summary>
    /// <exception cref="InvalidOperationException">Aucun palier n'a ete defini.</exception>
    public LoadProfile Build()
    {
        if (_stages.Count == 0)
        {
            throw new InvalidOperationException("Le profil de charge ne contient aucun palier.");
        }

        return new LoadProfile(_stages);
    }
}