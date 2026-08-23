namespace Sirocco.Domain.Metrics;

/// <summary>
/// Table de correspondance <c>nom de métrique personnalisée &lt;-&gt; <see cref="CustomMetricId"/></c>,
/// avec son type (<see cref="CustomMetricKind"/>) associé.
/// <para>
/// Peuplée une seule fois pendant la phase de configuration (avant le premier tir), puis lue
/// en O(1) par index pendant l'agrégation — même discipline que <see cref="StepRegistry"/>.
/// Contrairement aux étapes, une même métrique peut être enregistrée depuis plusieurs endroits
/// du scénario (un compteur métier alimenté par deux étapes différentes, par exemple) : c'est
/// pour cela que <see cref="Register"/> exige que le type soit le même à chaque appel plutôt que
/// de rejeter tout nom déjà vu.
/// </para>
/// </summary>
public sealed class CustomMetricRegistry
{
    private readonly Dictionary<string, CustomMetricId> _byName = new(StringComparer.Ordinal);
    private readonly List<string> _namesById = [];
    private readonly List<CustomMetricKind> _kindsById = [];
    private bool _sealed;

    /// <summary>Nombre de métriques enregistrées.</summary>
    public int Count => _namesById.Count;

    /// <summary>Noms des métriques, indexés par <see cref="CustomMetricId.Value"/>.</summary>
    public IReadOnlyList<string> Names => _namesById;

    /// <summary>
    /// Enregistre une métrique, ou renvoie l'identifiant existant si le nom est déjà connu.
    /// </summary>
    /// <exception cref="ArgumentException">Le nom est déjà enregistré avec un type différent.</exception>
    /// <exception cref="InvalidOperationException">Le registre a été scellé.</exception>
    public CustomMetricId Register(string name, CustomMetricKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_byName.TryGetValue(name, out CustomMetricId existing))
        {
            CustomMetricKind existingKind = _kindsById[existing.Value];
            if (existingKind != kind)
            {
                throw new ArgumentException(
                    $"La métrique '{name}' est déjà enregistrée en tant que {existingKind} : " +
                    $"impossible de la réenregistrer en tant que {kind}. Le type d'une métrique doit " +
                    "être le même partout où elle apparaît dans le scénario.",
                    nameof(kind));
            }

            return existing;
        }

        if (_sealed)
        {
            throw new InvalidOperationException(
                $"Le registre de métriques personnalisées est scellé : impossible d'enregistrer '{name}' " +
                "après le démarrage du test. Déclarez toutes vos métriques dans IWorkflow.RegisterMetrics.");
        }

        CustomMetricId id = new(_namesById.Count);
        _namesById.Add(name);
        _kindsById.Add(kind);
        _byName[name] = id;
        return id;
    }

    /// <summary>Résout le nom d'une métrique à partir de son identifiant.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Identifiant inconnu.</exception>
    public string GetName(CustomMetricId id)
    {
        if ((uint)id.Value >= (uint)_namesById.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "Identifiant de métrique inconnu.");
        }

        return _namesById[id.Value];
    }

    /// <summary>Résout le type d'une métrique à partir de son identifiant.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Identifiant inconnu.</exception>
    public CustomMetricKind GetKind(CustomMetricId id)
    {
        if ((uint)id.Value >= (uint)_kindsById.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "Identifiant de métrique inconnu.");
        }

        return _kindsById[id.Value];
    }

    /// <summary>Recherche l'identifiant associé à un nom.</summary>
    public bool TryGetId(string name, out CustomMetricId id) => _byName.TryGetValue(name, out id);

    /// <summary>
    /// Verrouille le registre : toute nouvelle métrique lève une exception.
    /// Appelé par le moteur juste avant la première itération.
    /// </summary>
    public void Seal() => _sealed = true;
}