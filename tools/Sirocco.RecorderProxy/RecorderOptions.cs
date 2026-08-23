namespace Sirocco.RecorderProxy;

/// <summary>
/// Options de <c>sirocco-recorder</c>, deja analysees et typees. Meme discipline que
/// <c>Sirocco.Cli.CliOptions</c> : un aplat de valeurs, analyse a la main, sans bibliotheque
/// d'analyse de ligne de commande.
/// </summary>
public sealed class RecorderOptions
{
    private const string DEFAULT_LISTEN_URL = "http://localhost:8888";

    public required string TargetUrl { get; init; }

    public string ListenUrl { get; init; } = DEFAULT_LISTEN_URL;

    public required string OutputPath { get; init; }

    public required string WorkflowName { get; init; }

    /// <exception cref="FormatException">Un argument est manquant, mal forme ou non reconnu.</exception>
    public static RecorderOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? targetUrl = null;
        string listenUrl = DEFAULT_LISTEN_URL;
        string? outputPath = null;
        string? workflowName = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--target-url" when i + 1 < args.Length:
                    targetUrl = args[++i];
                    break;

                case "--listen" when i + 1 < args.Length:
                    listenUrl = args[++i];
                    break;

                case "--out" when i + 1 < args.Length:
                    outputPath = args[++i];
                    break;

                case "--name" when i + 1 < args.Length:
                    workflowName = args[++i];
                    break;

                default:
                    throw new FormatException($"Option non reconnue ou incomplete : '{args[i]}'.");
            }
        }

        if (targetUrl is null)
        {
            throw new FormatException("--target-url est requis : c'est l'unique cible vers laquelle le proxy retransmet.");
        }

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out _))
        {
            throw new FormatException($"--target-url n'est pas une URL absolue valide : '{targetUrl}'.");
        }

        if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out _))
        {
            throw new FormatException($"--listen n'est pas une URL absolue valide : '{listenUrl}'.");
        }

        if (outputPath is null)
        {
            throw new FormatException("--out est requis : chemin du scenario .csx a ecrire a l'arret du proxy.");
        }

        return new RecorderOptions
        {
            TargetUrl = targetUrl,
            ListenUrl = listenUrl,
            OutputPath = outputPath,
            WorkflowName = workflowName ?? Path.GetFileNameWithoutExtension(outputPath),
        };
    }
}