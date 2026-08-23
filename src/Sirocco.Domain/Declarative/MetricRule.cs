using Sirocco.Domain.Metrics;

namespace Sirocco.Domain.Declarative;

/// <summary>
/// Métrique personnalisée alimentée par la réponse d'une étape, rapportée séparément du tableau
/// d'étapes (voir <see cref="Metrics.LoadTestReport.CustomMetrics"/>) plutôt que comme sa propre
/// étape — contrairement à <see cref="CheckRule"/>, une métrique personnalisée n'est pas une
/// assertion et ne s'inscrit pas dans le même espace de noms que les étapes.
/// <para>
/// Réutilise le même vocabulaire d'expression que <see cref="ExtractionRule"/>
/// (Regex/XPath/JsonPath, une seule des trois) pour trouver la valeur à mesurer, avec une
/// exception : un <see cref="CustomMetricKind.Counter"/> sans expression compte simplement les
/// exécutions de l'étape (valeur implicite 1 à chaque passage) — le cas le plus courant
/// ("combien de fois ceci s'est produit") ne devrait pas exiger d'extraire quoi que ce soit
/// d'une réponse qui ne contient peut-être rien d'utile pour ça.
/// </para>
/// </summary>
public sealed record MetricRule
{
    /// <summary>
    /// Nom de la métrique, tel qu'il apparaît dans les rapports. Peut apparaître dans plusieurs
    /// étapes du même scénario (un compteur métier alimenté à deux endroits différents, par
    /// exemple) : voir <see cref="Declarative.ScenarioDefinition.Validate"/>, qui exige alors le
    /// même <see cref="Kind"/> partout où ce nom réapparaît.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Type de la métrique.</summary>
    public required CustomMetricKind Kind { get; init; }

    /// <summary>Motif Regex ; le premier groupe capturant est extrait, ou la correspondance entière à défaut.</summary>
    public string? Regex { get; init; }

    /// <summary>Expression XPath, évaluée contre le corps interprété comme XML.</summary>
    public string? XPath { get; init; }

    /// <summary>Expression JsonPath, évaluée contre le corps interprété comme JSON.</summary>
    public string? JsonPath { get; init; }

    /// <summary>
    /// Valeur attendue pour un <see cref="CustomMetricKind.Rate"/> : la mesure vaut 1 si la
    /// valeur trouvée lui est identique, 0 sinon. Sans elle, la mesure vaut 1 dès que
    /// l'expression trouve quoi que ce soit. N'a de sens que pour <see cref="CustomMetricKind.Rate"/>.
    /// </summary>
    public string? Expected { get; init; }

    /// <summary>Valide la cohérence de la métrique et la syntaxe de son expression.</summary>
    /// <exception cref="ArgumentException">La métrique est incohérente ou son expression est invalide.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("Le nom d'une métrique personnalisée ne peut pas être vide.", nameof(Name));
        }

        bool hasExpression = Regex is not null || XPath is not null || JsonPath is not null;

        if (Kind != CustomMetricKind.Counter && !hasExpression)
        {
            throw new ArgumentException(
                $"La métrique '{Name}' ({Kind}) doit préciser une expression (regex, xpath ou jsonPath) : " +
                "sans elle, il n'y a aucune valeur à mesurer. Seul un compteur peut s'en passer " +
                "(il compte alors le nombre de passages sur l'étape).",
                nameof(Kind));
        }

        if (Expected is not null && Kind != CustomMetricKind.Rate)
        {
            throw new ArgumentException(
                $"La métrique '{Name}' ({Kind}) ne peut pas préciser 'expected' : cette option n'a de sens " +
                "que pour un taux (rate).",
                nameof(Expected));
        }

        if (hasExpression)
        {
            try
            {
                ToExtractionRule()!.Validate();
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Métrique '{Name}' invalide : {ex.Message}", nameof(Name), ex);
            }
        }
    }

    /// <summary>
    /// Évalue la métrique contre le corps de réponse fourni. Renvoie <see langword="null"/> si
    /// rien n'a pu être mesuré cette fois (expression absente de la réponse, valeur non
    /// numérique) — ce n'est jamais un échec de la requête, seulement une mesure manquée.
    /// </summary>
    public double? Evaluate(string responseBody)
    {
        ExtractionRule? rule = ToExtractionRule();

        if (rule is null)
        {
            // Compteur sans expression : chaque passage sur l'étape vaut une occurrence.
            return 1d;
        }

        bool matched = rule.TryExtract(responseBody, out string? actual);

        if (Kind == CustomMetricKind.Rate)
        {
            bool passed = Expected is null ? matched : matched && string.Equals(actual, Expected, StringComparison.Ordinal);
            return passed ? 1d : 0d;
        }

        if (!matched)
        {
            return null;
        }

        return double.TryParse(actual, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    private ExtractionRule? ToExtractionRule() =>
        Regex is null && XPath is null && JsonPath is null
            ? null
            : new ExtractionRule { Variable = Name, Regex = Regex, XPath = XPath, JsonPath = JsonPath };
}