using Tempest.Domain.Execution;
using Tempest.Scenarios.Declarative;
using Tempest.Scenarios.Scripting;

namespace Tempest.Scenarios;

/// <summary>
/// Charge un <see cref="IWorkflow"/> depuis un fichier de scenario, en deduisant le format —
/// declaratif ou scripte — de son extension.
/// <para>
/// Point d'entree unique pour <c>Tempest.Host</c>/<c>Tempest.Cli</c> : ni l'un ni l'autre n'a
/// besoin de connaitre <see cref="ScenarioDefinitionLoader"/> ou <see cref="ScriptedWorkflowLoader"/>
/// individuellement, seulement ce dispatcher.
/// </para>
/// </summary>
public static class WorkflowFileLoader
{
    /// <summary>
    /// Charge un <see cref="IWorkflow"/> depuis <paramref name="path"/> : <c>.yaml</c>/<c>.yml</c>/
    /// <c>.json</c> pour le format declaratif, <c>.csx</c>/<c>.cs</c> pour un scenario scripte.
    /// </summary>
    /// <exception cref="FileNotFoundException">Le fichier n'existe pas.</exception>
    /// <exception cref="NotSupportedException">L'extension n'est reconnue par aucun des deux formats.</exception>
    /// <exception cref="FormatException">Le contenu ne peut pas etre interprete dans le format deduit.</exception>
    public static IWorkflow LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".yaml" or ".yml" or ".json" => new DeclarativeWorkflow(ScenarioDefinitionLoader.LoadFromFile(path)),
            ".csx" or ".cs" => ScriptedWorkflowLoader.LoadFromFile(path),
            var extension => throw new NotSupportedException(
                $"Extension de fichier de scenario non reconnue : '{extension}'. " +
                "Utilisez .yaml/.yml/.json (declaratif) ou .csx/.cs (scripte)."),
        };
    }
}