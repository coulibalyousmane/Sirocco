namespace Tempest.Domain.Metrics;

/// <summary>
/// Table de correspondance <c>nom d'etape &lt;-&gt; <see cref="StepId"/></c>.
/// <para>
/// Peuplee une seule fois pendant la phase de configuration (avant le premier tir),
/// puis lue en O(1) par index pendant l'agregation. Aucun acces a cette classe
/// n'a lieu sur le chemin critique d'emission d'une metrique.
/// </para>
/// </summary>
public sealed class StepRegistry
{
    private readonly Dictionary<string, StepId> _byName = new(StringComparer.Ordinal);
    private readonly List<string> _byId = [];
    private bool _sealed;

    /// <summary>Nombre d'etapes enregistrees.</summary>
    public int Count => _byId.Count;

    /// <summary>Noms des etapes, indexes par <see cref="StepId.Value"/>.</summary>
    public IReadOnlyList<string> Names => _byId;

    /// <summary>
    /// Enregistre une etape, ou renvoie l'identifiant existant si le nom est deja connu.
    /// </summary>
    /// <exception cref="InvalidOperationException">Le registre a ete scelle.</exception>
    public StepId Register(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_byName.TryGetValue(name, out StepId existing))
        {
            return existing;
        }

        if (_sealed)
        {
            throw new InvalidOperationException(
                $"Le registre d'etapes est scelle : impossible d'enregistrer '{name}' apres le demarrage du test. " +
                "Declarez toutes vos etapes dans IWorkflow.RegisterSteps.");
        }

        StepId id = new(_byId.Count);
        _byId.Add(name);
        _byName[name] = id;
        return id;
    }

    /// <summary>Resout le nom d'une etape a partir de son identifiant.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Identifiant inconnu.</exception>
    public string GetName(StepId id)
    {
        if ((uint)id.Value >= (uint)_byId.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "Identifiant d'etape inconnu.");
        }

        return _byId[id.Value];
    }

    /// <summary>Recherche l'identifiant associe a un nom.</summary>
    public bool TryGetId(string name, out StepId id) => _byName.TryGetValue(name, out id);

    /// <summary>
    /// Verrouille le registre : toute nouvelle etape leve une exception.
    /// Appele par le moteur juste avant la premiere iteration afin de garantir
    /// que les tableaux d'agregation dimensionnes sur <see cref="Count"/> restent valides.
    /// </summary>
    public void Seal() => _sealed = true;
}