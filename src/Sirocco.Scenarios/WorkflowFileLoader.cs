using Sirocco.Domain.Execution;
using Sirocco.Scenarios.Declarative;
using Sirocco.Scenarios.Plugins;
using Sirocco.Scenarios.Scripting;

namespace Sirocco.Scenarios;

/// <summary>
/// Charge un <see cref="IWorkflow"/> depuis un fichier de scenario, en deduisant le format —
/// declaratif, scripte ou plugin compile — de son extension.
/// <para>
/// Point d'entree unique pour <c>Sirocco.Host</c>/<c>Sirocco.Cli</c> : aucun des trois n'a besoin
/// de connaitre <see cref="ScenarioDefinitionLoader"/>, <see cref="ScriptedWorkflowLoader"/> ou
/// <see cref="PluginWorkflowLoader"/> individuellement, seulement ce dispatcher.
/// </para>
/// </summary>
public static class WorkflowFileLoader
{
    /// <summary>
    /// Charge un <see cref="IWorkflow"/> depuis <paramref name="path"/> : <c>.yaml</c>/<c>.yml</c>/
    /// <c>.json</c> pour le format declaratif, <c>.csx</c>/<c>.cs</c> pour un scenario scripte,
    /// <c>.dll</c> pour un plugin compile (voir <see cref="PluginWorkflowLoader"/>) — dans ce
    /// dernier cas, <paramref name="pluginTypeName"/> designe le type a instancier s'il y en a
    /// plusieurs dans l'assembly, ignore pour les deux autres formats.
    /// </summary>
    /// <exception cref="FileNotFoundException">Le fichier n'existe pas.</exception>
    /// <exception cref="NotSupportedException">L'extension n'est reconnue par aucun des trois formats.</exception>
    /// <exception cref="FormatException">Le contenu ne peut pas etre interprete dans le format deduit.</exception>
    public static IWorkflow LoadFromFile(string path, string? pluginTypeName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".yaml" or ".yml" or ".json" => new DeclarativeWorkflow(ScenarioDefinitionLoader.LoadFromFile(path)),
            ".csx" or ".cs" => ScriptedWorkflowLoader.LoadFromFile(path),
            ".dll" => PluginWorkflowLoader.Load(path, pluginTypeName),
            var extension => throw new NotSupportedException(
                $"Extension de fichier de scenario non reconnue : '{extension}'. " +
                "Utilisez .yaml/.yml/.json (declaratif), .csx/.cs (scripte) ou .dll (plugin compile)."),
        };
    }
}