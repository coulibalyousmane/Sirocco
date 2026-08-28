using Sirocco.Cli;

namespace Sirocco.UnitTests.Cli;

/// <summary>
/// Couvre la mise en forme de <c>sirocco --version</c> (AUDIT-MATURITE.md, M3). C'est la commande
/// qu'on tape quand quelque chose va mal : elle ne doit jamais echouer, meme si une des grandeurs
/// qu'elle rapporte manque a l'appel.
/// <para>
/// Le releve reel (attribut d'assemblage, descripteurs de runtime) n'est pas teste ici : il depend
/// de la machine qui execute les tests. Il est verifie par un vrai <c>sirocco --version</c> sur le
/// binaire installe, documente dans <c>docs/demarrer/cli.md</c>.
/// </para>
/// </summary>
public sealed class SiroccoVersionTests
{
    [Fact]
    public void The_four_values_a_bug_report_needs_are_all_present()
    {
        string text = SiroccoVersion.Format("0.1.0+abcdef1", ".NET 10.0.0", "Windows 11", "win-x64");

        Assert.Contains("sirocco 0.1.0+abcdef1", text);
        Assert.Contains(".NET 10.0.0", text);
        Assert.Contains("Windows 11", text);
        Assert.Contains("win-x64", text);
    }

    [Fact]
    public void The_commit_is_kept_verbatim_because_it_is_the_point()
    {
        // Le suffixe +sha vient de SourceRevisionId, que SourceLink renseigne depuis la correction
        // de M5. C'est lui qui rend le binaire tracable a son code : le tronquer ou le nettoyer
        // reviendrait a jeter la seule information qu'un rapport de bug ne peut pas reconstituer.
        const string INFORMATIONAL = "0.1.0+1ee092db1b8b57de0b78ff30a9529e657447d6a8";

        Assert.Contains(INFORMATIONAL, SiroccoVersion.Format(INFORMATIONAL, "x", "y", "z"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_version_is_named_as_such_instead_of_producing_a_blank(string? informationalVersion)
    {
        string text = SiroccoVersion.Format(informationalVersion, ".NET 10.0.0", "Linux", "linux-x64");

        Assert.Contains("version inconnue", text);
        Assert.DoesNotContain("sirocco  ", text);
    }

    [Fact]
    public void Every_value_can_be_missing_at_once_without_throwing()
    {
        string text = SiroccoVersion.Format(null, null, null, null);

        Assert.Contains("version inconnue", text);
        Assert.Contains("inconnu", text);
        Assert.Contains("rid inconnu", text);
    }

    [Fact]
    public void The_output_is_three_lines_so_it_pastes_into_an_issue_unchanged()
    {
        string text = SiroccoVersion.Format("0.1.0", ".NET 10.0.0", "Linux", "linux-x64");

        Assert.Equal(3, text.Split(Environment.NewLine).Length);
    }

    [Fact]
    public void The_real_assembly_reports_a_version_rather_than_the_fallback()
    {
        // Verifie le releve reel, pas seulement la mise en forme : si le SDK cessait d'emettre
        // AssemblyInformationalVersion, --version se degraderait silencieusement en
        // "version inconnue" et le constat M3 serait rouvert sans que rien ne le signale.
        string text = SiroccoVersion.Describe();

        Assert.StartsWith("sirocco 0.", text);
        Assert.DoesNotContain("version inconnue", text);
    }
}