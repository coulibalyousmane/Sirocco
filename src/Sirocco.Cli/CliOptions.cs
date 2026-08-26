using System.Globalization;
using Sirocco.Domain.Metrics;

namespace Sirocco.Cli;

/// <summary>
/// Options de la commande <c>sirocco run</c>, deja analysees et typees.
/// <para>
/// Reste deliberement un aplat de valeurs optionnelles, pas un objet metier : c'est
/// <c>Program.cs</c> qui decide comment les combiner avec la configuration (voir <c>--target-url</c>
/// et le profil de charge, qui peuvent aussi venir d'un <c>appsettings.json</c> du repertoire
/// courant), pas cette classe.
/// </para>
/// </summary>
internal sealed class CliOptions
{
    public string? ScenarioPath { get; private init; }

    public string? Workflow { get; private init; }

    public string? PluginWorkflowType { get; private init; }

    public string? PluginPackageId { get; private init; }

    public string? PluginPackageVersion { get; private init; }

    public IReadOnlyList<string> PluginPackageSources { get; private init; } = [];

    public bool AllowUnsignedPlugins { get; private init; }

    public IReadOnlyList<string> AllowedEnvironmentVariables { get; private init; } = [];

    public bool AllowAllEnvironmentVariables { get; private init; }

    public string? TargetUrl { get; private init; }

    public double? Rps { get; private init; }

    public double? FromRps { get; private init; }

    public double? ToRps { get; private init; }

    public TimeSpan? Duration { get; private init; }

    public int? MaxVirtualUsers { get; private init; }

    public int? Vus { get; private init; }

    public int? VusFrom { get; private init; }

    public int? VusTo { get; private init; }

    public long? Iterations { get; private init; }

    public long? IterationsPerVirtualUser { get; private init; }

    public double? MaxRequestsPerSecond { get; private init; }

    public string? ReportHtmlPath { get; private init; }

    public string? ReportJsonPath { get; private init; }

    public IReadOnlyList<ThresholdRule> Thresholds { get; private init; } = [];

    /// <exception cref="FormatException">Un argument est mal forme ou non reconnu.</exception>
    public static CliOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? scenarioPath = null;
        string? workflow = null;
        string? pluginWorkflowType = null;
        string? pluginPackageId = null;
        string? pluginPackageVersion = null;
        List<string> pluginPackageSources = [];
        bool allowUnsignedPlugins = false;
        List<string> allowedEnvironmentVariables = [];
        bool allowAllEnvironmentVariables = false;
        string? targetUrl = null;
        double? rps = null;
        double? fromRps = null;
        double? toRps = null;
        TimeSpan? duration = null;
        int? maxVirtualUsers = null;
        int? vus = null;
        int? vusFrom = null;
        int? vusTo = null;
        long? iterations = null;
        long? iterationsPerVirtualUser = null;
        double? maxRequestsPerSecond = null;
        string? reportHtmlPath = null;
        string? reportJsonPath = null;
        List<ThresholdRule> thresholds = [];

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--workflow" when i + 1 < args.Length:
                    workflow = args[++i];
                    break;

                case "--plugin-type" when i + 1 < args.Length:
                    pluginWorkflowType = args[++i];
                    break;

                case "--plugin-package" when i + 1 < args.Length:
                    pluginPackageId = args[++i];
                    break;

                case "--plugin-package-version" when i + 1 < args.Length:
                    pluginPackageVersion = args[++i];
                    break;

                case "--plugin-source" when i + 1 < args.Length:
                    pluginPackageSources.Add(args[++i]);
                    break;

                case "--plugin-allow-unsigned":
                    allowUnsignedPlugins = true;
                    break;

                case "--allow-env" when i + 1 < args.Length:
                    allowedEnvironmentVariables.Add(args[++i]);
                    break;

                case "--allow-env-all":
                    allowAllEnvironmentVariables = true;
                    break;

                case "--target-url" when i + 1 < args.Length:
                    targetUrl = args[++i];
                    break;

                case "--rps" when i + 1 < args.Length:
                    rps = ParseDouble(args[++i], "--rps");
                    break;

                case "--from-rps" when i + 1 < args.Length:
                    fromRps = ParseDouble(args[++i], "--from-rps");
                    break;

                case "--to-rps" when i + 1 < args.Length:
                    toRps = ParseDouble(args[++i], "--to-rps");
                    break;

                case "--duration" when i + 1 < args.Length:
                    duration = CliDuration.Parse(args[++i]);
                    break;

                case "--max-vus" when i + 1 < args.Length:
                    maxVirtualUsers = ParseInt(args[++i], "--max-vus");
                    break;

                case "--vus" when i + 1 < args.Length:
                    vus = ParseInt(args[++i], "--vus");
                    break;

                case "--vus-from" when i + 1 < args.Length:
                    vusFrom = ParseInt(args[++i], "--vus-from");
                    break;

                case "--vus-to" when i + 1 < args.Length:
                    vusTo = ParseInt(args[++i], "--vus-to");
                    break;

                case "--iterations" when i + 1 < args.Length:
                    iterations = ParseLong(args[++i], "--iterations");
                    break;

                case "--iterations-per-vu" when i + 1 < args.Length:
                    iterationsPerVirtualUser = ParseLong(args[++i], "--iterations-per-vu");
                    break;

                case "--max-rps" when i + 1 < args.Length:
                    maxRequestsPerSecond = ParseDouble(args[++i], "--max-rps");
                    break;

                case "--report-html" when i + 1 < args.Length:
                    reportHtmlPath = args[++i];
                    break;

                case "--report-json" when i + 1 < args.Length:
                    reportJsonPath = args[++i];
                    break;

                case "--threshold" when i + 1 < args.Length:
                    thresholds.Add(ParseThreshold(args[++i]));
                    break;

                default:
                    if (!arg.StartsWith("--", StringComparison.Ordinal) && scenarioPath is null)
                    {
                        scenarioPath = arg;
                    }
                    else
                    {
                        throw new FormatException($"Option non reconnue ou incomplete : '{arg}'.");
                    }

                    break;
            }
        }

        if (fromRps.HasValue != toRps.HasValue)
        {
            throw new FormatException("--from-rps et --to-rps doivent etre fournis ensemble.");
        }

        if (rps.HasValue && fromRps.HasValue)
        {
            throw new FormatException("--rps et --from-rps/--to-rps sont mutuellement exclusifs.");
        }

        if (vus.HasValue && (rps.HasValue || fromRps.HasValue))
        {
            throw new FormatException(
                "--vus (modele ferme) est mutuellement exclusif avec --rps/--from-rps/--to-rps (modele ouvert).");
        }

        if (vus.HasValue && maxVirtualUsers.HasValue)
        {
            throw new FormatException(
                "--vus et --max-vus sont mutuellement exclusifs : --vus fixe deja l'effectif exact du modele ferme.");
        }

        if (vusFrom.HasValue != vusTo.HasValue)
        {
            throw new FormatException("--vus-from et --vus-to doivent etre fournis ensemble.");
        }

        if (vusFrom.HasValue && vus.HasValue)
        {
            throw new FormatException(
                "--vus-from/--vus-to (montee d'utilisateurs) et --vus (effectif fixe) sont mutuellement exclusifs.");
        }

        if (vusFrom.HasValue && (rps.HasValue || fromRps.HasValue))
        {
            throw new FormatException(
                "--vus-from/--vus-to (modele ferme) est mutuellement exclusif avec --rps/--from-rps/--to-rps (modele ouvert).");
        }

        if (vusFrom.HasValue && maxVirtualUsers.HasValue)
        {
            throw new FormatException(
                "--vus-from/--vus-to et --max-vus sont mutuellement exclusifs : l'effectif suit deja les paliers, " +
                "jusqu'a leur pic.");
        }

        if (iterationsPerVirtualUser.HasValue && duration.HasValue)
        {
            throw new FormatException(
                "--iterations-per-vu et --duration sont mutuellement exclusifs : --vus s'arrete sur l'un ou " +
                "l'autre, jamais les deux.");
        }

        if (iterationsPerVirtualUser.HasValue && (rps.HasValue || fromRps.HasValue || vusFrom.HasValue))
        {
            throw new FormatException(
                "--iterations-per-vu est mutuellement exclusif avec --rps/--from-rps/--to-rps/--vus-from/--vus-to.");
        }

        if (iterationsPerVirtualUser.HasValue && iterations.HasValue)
        {
            throw new FormatException(
                "--iterations-per-vu (chaque utilisateur virtuel en fait exactement sa part) et --iterations " +
                "(un total partage entre tous) sont mutuellement exclusifs.");
        }

        if (iterations.HasValue && (vus.HasValue || vusFrom.HasValue || rps.HasValue || fromRps.HasValue || duration.HasValue))
        {
            throw new FormatException(
                "--iterations est mutuellement exclusif avec --vus/--vus-from/--vus-to/--rps/--from-rps/--to-rps/--duration.");
        }

        if (maxRequestsPerSecond is <= 0d)
        {
            throw new FormatException("--max-rps doit etre strictement positif.");
        }

        return new CliOptions
        {
            ScenarioPath = scenarioPath,
            Workflow = workflow,
            PluginWorkflowType = pluginWorkflowType,
            PluginPackageId = pluginPackageId,
            PluginPackageVersion = pluginPackageVersion,
            PluginPackageSources = pluginPackageSources,
            AllowUnsignedPlugins = allowUnsignedPlugins,
            AllowedEnvironmentVariables = allowedEnvironmentVariables,
            AllowAllEnvironmentVariables = allowAllEnvironmentVariables,
            TargetUrl = targetUrl,
            Rps = rps,
            FromRps = fromRps,
            ToRps = toRps,
            Duration = duration,
            MaxVirtualUsers = maxVirtualUsers,
            Vus = vus,
            VusFrom = vusFrom,
            VusTo = vusTo,
            Iterations = iterations,
            IterationsPerVirtualUser = iterationsPerVirtualUser,
            MaxRequestsPerSecond = maxRequestsPerSecond,
            ReportHtmlPath = reportHtmlPath,
            ReportJsonPath = reportJsonPath,
            Thresholds = thresholds,
        };
    }

    private static double ParseDouble(string value, string optionName)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            throw new FormatException($"Valeur numerique invalide pour {optionName} : '{value}'.");
        }

        return parsed;
    }

    private static int ParseInt(string value, string optionName)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            throw new FormatException($"Valeur entiere invalide pour {optionName} : '{value}'.");
        }

        return parsed;
    }

    private static long ParseLong(string value, string optionName)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            throw new FormatException($"Valeur entiere invalide pour {optionName} : '{value}'.");
        }

        return parsed;
    }

    private static ThresholdRule ParseThreshold(string value)
    {
        string[] parts = value.Split(':', 5);
        if (parts.Length < 4)
        {
            throw new FormatException(
                $"Seuil invalide : '{value}'. Format attendu : 'etape:grandeur:comparaison:limite[:nom]', " +
                $"ex. '{WellKnownSteps.ITERATION}:ResponseP95Milliseconds:LessThan:200'.");
        }

        if (!Enum.TryParse(parts[1], ignoreCase: true, out ThresholdMetric metric))
        {
            throw new FormatException(
                $"Grandeur de seuil inconnue : '{parts[1]}'. Valeurs possibles : {string.Join(", ", Enum.GetNames<ThresholdMetric>())}.");
        }

        if (!Enum.TryParse(parts[2], ignoreCase: true, out ThresholdComparison comparison))
        {
            throw new FormatException(
                $"Comparaison de seuil inconnue : '{parts[2]}'. Valeurs possibles : {string.Join(", ", Enum.GetNames<ThresholdComparison>())}.");
        }

        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double limit))
        {
            throw new FormatException($"Limite de seuil invalide : '{parts[3]}'.");
        }

        return new ThresholdRule
        {
            StepName = parts[0],
            Metric = metric,
            Comparison = comparison,
            Limit = limit,
            Name = parts.Length == 5 ? parts[4] : null,
        };
    }
}