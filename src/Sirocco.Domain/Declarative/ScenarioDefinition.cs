using Sirocco.Domain.Metrics;

namespace Sirocco.Domain.Declarative;

/// <summary>
/// Description declarative complete d'un scenario : un nom, une sequence d'etapes HTTP.
/// <para>
/// C'est la structure que <c>Sirocco.Scenarios.Declarative.ScenarioDefinitionLoader</c>
/// produit a partir d'un fichier YAML ou JSON, et que
/// <c>Sirocco.Scenarios.DeclarativeWorkflow</c> interprete a chaque iteration. Aucune des deux
/// classes n'est referencee ici : le Domain ne connait que la forme des donnees, jamais la
/// facon dont elles sont lues ni executees.
/// </para>
/// </summary>
public sealed record ScenarioDefinition
{
    /// <summary>Nom du scenario, utilise dans les rapports.</summary>
    public required string Name { get; init; }

    /// <summary>Etapes du scenario, executees dans l'ordre a chaque iteration.</summary>
    public required IReadOnlyList<HttpStepDefinition> Steps { get; init; }

    /// <summary>
    /// Jeux de donnees charges au demarrage du tir, accessibles depuis n'importe quelle etape
    /// via <c>{{nom.colonne}}</c>. Vide par defaut : aucun scenario existant n'en depend.
    /// </summary>
    public IReadOnlyList<DataSetDefinition> Datasets { get; init; } = [];

    /// <summary>
    /// Etiquettes du tir (ex. <c>région: eu-west</c>, <c>version: v2</c>) : une metadonnee du
    /// scenario dans son ensemble, pas d'une etape precise — utile pour distinguer deux rapports
    /// produits par le meme scenario contre des cibles differentes. Vide par defaut. Reportee
    /// telle quelle dans <see cref="Metrics.LoadTestReport.Tags"/>, jamais utilisee pour
    /// l'agregation des metriques.
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    /// <summary>Valide la coherence du scenario et de chacune de ses etapes.</summary>
    /// <exception cref="ArgumentException">Le scenario est incoherent.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Le nom du scenario ne peut pas etre vide.", nameof(Name));
        }

        if (Steps.Count == 0)
        {
            throw new ArgumentException("Un scenario declaratif doit contenir au moins une etape.", nameof(Steps));
        }

        // Meme espace de noms pour les etapes et leurs checks : les deux deviennent chacun leur
        // propre StepId dans le rapport (voir DeclarativeWorkflow), une collision entre les deux
        // fusionnerait silencieusement deux lignes distinctes du rapport en une seule. La cle est
        // QualifiedName (groupe compris), pas Name : deux etapes de meme nom dans deux groupes
        // differents restent deux lignes distinctes du rapport, donc pas une collision.
        HashSet<string> seenNames = new(StringComparer.Ordinal);
        foreach (HttpStepDefinition step in Steps)
        {
            step.Validate();

            if (!seenNames.Add(step.QualifiedName))
            {
                throw new ArgumentException(
                    $"Le nom d'etape '{step.QualifiedName}' apparait plusieurs fois : les noms doivent etre uniques.",
                    nameof(Steps));
            }

            foreach (CheckRule check in step.Checks)
            {
                if (!seenNames.Add(check.Name))
                {
                    throw new ArgumentException(
                        $"Le nom '{check.Name}' apparait plusieurs fois (etape ou check) : les noms doivent etre uniques dans tout le scenario.",
                        nameof(Steps));
                }
            }
        }

        HashSet<string> seenDatasetNames = new(StringComparer.Ordinal);
        foreach (DataSetDefinition dataset in Datasets)
        {
            dataset.Validate();

            // "env" est reserve aux variables d'environnement ({{env.NOM}}, voir
            // DeclarativeWorkflow.TrySubstitute) : sans ce garde-fou, un jeu de donnees nomme
            // ainsi changerait silencieusement de sens a la substitution plutot que d'echouer
            // au chargement.
            if (string.Equals(dataset.Name, "env", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Le nom de jeu de donnees 'env' est reserve aux variables d'environnement "
                    + "({{env.NOM}}) et ne peut pas etre reutilise pour un jeu de donnees.",
                    nameof(Datasets));
            }

            if (!seenDatasetNames.Add(dataset.Name))
            {
                throw new ArgumentException(
                    $"Le nom de jeu de donnees '{dataset.Name}' apparait plusieurs fois : les noms doivent etre uniques.",
                    nameof(Datasets));
            }
        }

        foreach ((string key, string value) in Tags)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"L'etiquette '{key}' est invalide : ni la cle ni la valeur ne peuvent etre vides.",
                    nameof(Tags));
            }
        }

        // Contrairement aux etapes et aux checks, une meme metrique peut legitimement apparaitre
        // dans plusieurs etapes (un compteur metier alimente a deux endroits differents) : ce
        // n'est pas une collision de nom qui est rejetee ici, seulement une incoherence de type.
        Dictionary<string, CustomMetricKind> seenMetricKinds = new(StringComparer.Ordinal);
        foreach (HttpStepDefinition step in Steps)
        {
            foreach (MetricRule metric in step.Metrics)
            {
                if (seenMetricKinds.TryGetValue(metric.Name, out CustomMetricKind existingKind))
                {
                    if (existingKind != metric.Kind)
                    {
                        throw new ArgumentException(
                            $"La metrique '{metric.Name}' est declaree en tant que {existingKind} puis " +
                            $"{metric.Kind} : le type d'une metrique doit etre le meme partout ou elle apparait.",
                            nameof(Steps));
                    }
                }
                else
                {
                    seenMetricKinds[metric.Name] = metric.Kind;
                }
            }
        }
    }
}