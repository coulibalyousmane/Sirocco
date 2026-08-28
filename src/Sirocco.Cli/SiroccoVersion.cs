using System.Reflection;
using System.Runtime.InteropServices;

namespace Sirocco.Cli;

/// <summary>
/// Repond a <c>sirocco --version</c> (AUDIT-MATURITE.md, M3).
/// <para>
/// Ce n'est pas du confort : Sirocco se distribue par <b>deux</b> canaux, l'outil
/// <c>dotnet tool</c> et les quatre binaires autonomes des releases GitHub. Pour le second, un
/// executable telecharge sans SDK ni gestionnaire de paquets autour, il n'existait aucun moyen
/// d'etablir ce qu'on execute — donc aucun moyen de repondre a la premiere question posee sur
/// n'importe quelle issue.
/// </para>
/// <para>
/// La version rapportee inclut le <b>commit</b> des lors que le paquet est construit depuis le
/// depot : le SDK ajoute <c>+&lt;sha&gt;</c> a l'<c>AssemblyInformationalVersion</c> a partir du
/// <c>SourceRevisionId</c> que SourceLink renseigne, active en corrigeant M5. Les deux constats se
/// completent donc : l'un rend le binaire identifiable, l'autre rend son code retrouvable.
/// </para>
/// </summary>
internal static class SiroccoVersion
{
    /// <summary>Nom de la commande, tel qu'il apparait sur le <c>PATH</c>.</summary>
    private const string COMMAND_NAME = "sirocco";

    /// <summary>Releve la version reelle de cet executable et son environnement d'execution.</summary>
    public static string Describe() => Format(
        typeof(SiroccoVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(SiroccoVersion).Assembly.GetName().Version?.ToString(),
        RuntimeInformation.FrameworkDescription,
        RuntimeInformation.OSDescription,
        RuntimeInformation.RuntimeIdentifier);

    /// <summary>
    /// Met en forme les quatre grandeurs qu'un rapport de bug doit contenir. Separe de
    /// <see cref="Describe"/> pour etre verifiable sans dependre de la machine qui execute les
    /// tests : une version absente ou un descripteur vide ne doit jamais faire echouer
    /// <c>--version</c>, qui est justement la commande qu'on tape quand quelque chose va mal.
    /// </summary>
    public static string Format(
        string? informationalVersion,
        string? frameworkDescription,
        string? operatingSystemDescription,
        string? runtimeIdentifier)
    {
        string[] lines =
        [
            $"{COMMAND_NAME} {Or(informationalVersion, "version inconnue")}",
            $"runtime  {Or(frameworkDescription, "inconnu")}",
            $"systeme  {Or(operatingSystemDescription, "inconnu")} ({Or(runtimeIdentifier, "rid inconnu")})",
        ];

        return string.Join(Environment.NewLine, lines);
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}