using System.Globalization;
using System.Text;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

// Regenere l'index des extensions publiees a partir d'une VRAIE requete nuget.org, plutot que
// d'une liste tenue a la main qui divergerait de la realite au premier paquet publie par
// quelqu'un d'autre (ROADMAP.md, ligne "Ecosysteme d'extensions communautaire").
//
//   dotnet run --project tools/Sirocco.ExtensionIndex -- docs/extensions/_index-communaute.md
//
// L'index est un instantane : il vaut pour le moment ou il a ete genere, et rien ne le rafraichit
// tout seul. C'est la limite assumee de cette version — un rafraichissement periodique demanderait
// un workflow planifie qui committe, machinerie disproportionnee tant que l'etiquette ne designe
// que les extensions de ce depot.

const string TAG = "sirocco-extension";
const string SOURCE = "https://api.nuget.org/v3/index.json";

string outputPath = args.Length > 0
    ? args[0]
    : Path.Combine("docs", "extensions", "_index-communaute.md");

// L'horodatage vient de l'appelant s'il le fournit : un index regenere doit pouvoir etre compare
// a l'ancien sans que la seule difference soit la date.
string generatedOn = args.Length > 1
    ? args[1]
    : DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

SourceRepository repository = Repository.Factory.GetCoreV3(SOURCE);
PackageSearchResource search = await repository.GetResourceAsync<PackageSearchResource>()
    ?? throw new InvalidOperationException(
        $"La source '{SOURCE}' n'expose pas de service de recherche NuGet v3 : index non regenere.");

// La recherche nuget.org est approximative : "tags:x" ne garantit pas que chaque resultat porte
// reellement l'etiquette. On revalide donc chaque paquet sur ses propres metadonnees plutot que de
// faire confiance au moteur de recherche — cet index nomme des paquets tiers, en lister un qui ne
// revendique pas l'etiquette serait une erreur de fond, pas un detail cosmetique.
List<IPackageSearchMetadata> found = [];
const int PAGE_SIZE = 50;
for (int skip = 0; ; skip += PAGE_SIZE)
{
    IPackageSearchMetadata[] page = [.. await search.SearchAsync(
        $"tags:{TAG}",
        new SearchFilter(includePrerelease: false),
        skip,
        PAGE_SIZE,
        NullLogger.Instance,
        CancellationToken.None)];

    found.AddRange(page.Where(HasTag));

    if (page.Length < PAGE_SIZE)
    {
        break;
    }
}

StringBuilder markdown = new();
markdown.AppendLine(CultureInfo.InvariantCulture, $"<!-- Genere par tools/Sirocco.ExtensionIndex, ne pas editer a la main. -->");
markdown.AppendLine();

if (found.Count == 0)
{
    markdown.AppendLine(CultureInfo.InvariantCulture,
        $"**Aucun paquet ne porte l'étiquette `{TAG}` sur nuget.org à ce jour ({generatedOn}).**");
    markdown.AppendLine();
    markdown.AppendLine("Ce n'est pas une anomalie de l'index : les extensions de référence de ce dépôt sont");
    markdown.AppendLine("empaquetables mais ne partiront sur nuget.org qu'au premier tag `vX.Y.Z`, et aucune");
    markdown.AppendLine("extension tierce n'existe encore. L'index dira le contraire dès que ce sera le cas.");
}
else
{
    markdown.AppendLine(CultureInfo.InvariantCulture,
        $"Instantané du {generatedOn} — {found.Count} paquet(s) portant l'étiquette `{TAG}` sur nuget.org.");
    markdown.AppendLine();
    markdown.AppendLine("| Paquet | Version | Auteur | Description |");
    markdown.AppendLine("|---|---|---|---|");

    foreach (IPackageSearchMetadata package in found.OrderBy(static p => p.Identity.Id, StringComparer.OrdinalIgnoreCase))
    {
        string id = package.Identity.Id;
        string version = package.Identity.Version.ToNormalizedString();
        string authors = Escape(package.Authors);
        string description = Escape(Shorten(package.Description));
        markdown.AppendLine(CultureInfo.InvariantCulture,
            $"| [{id}](https://www.nuget.org/packages/{id}) | {version} | {authors} | {description} |");
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
await File.WriteAllTextAsync(outputPath, markdown.ToString());
Console.WriteLine($"{found.Count} extension(s) trouvee(s) — index ecrit dans {outputPath}");

// Revalidation de l'etiquette sur les metadonnees du paquet lui-meme. Les etiquettes NuGet sont
// une chaine separee par des espaces ou des virgules : on compare jeton par jeton, jamais par
// sous-chaine, sans quoi "sirocco-extension-truc" passerait pour "sirocco-extension".
static bool HasTag(IPackageSearchMetadata package) =>
    (package.Tags ?? string.Empty)
        .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(static tag => string.Equals(tag, TAG, StringComparison.OrdinalIgnoreCase));

// Le texte vient de tiers : il entre dans un tableau Markdown, donc les barres verticales et les
// sauts de ligne doivent etre neutralises, sinon un paquet peut casser la mise en page de la page.
static string Escape(string? value) =>
    (value ?? string.Empty)
        .Replace("|", "\\|", StringComparison.Ordinal)
        .ReplaceLineEndings(" ")
        .Trim();

static string Shorten(string? description)
{
    string text = (description ?? string.Empty).Trim();
    return text.Length <= 160 ? text : string.Concat(text.AsSpan(0, 157), "...");
}