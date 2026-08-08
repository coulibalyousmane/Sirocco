using System.Globalization;

namespace Tempest.Scenarios.Declarative;

/// <summary>
/// Analyse une duree telle qu'un auteur de scenario l'ecrit en YAML/JSON — <c>500ms</c>,
/// <c>1s</c>, <c>2m</c>, <c>1h</c> — plutot que le format <c>TimeSpan.Parse</c>
/// (<c>00:00:01</c>), que personne n'ecrit a la main dans un fichier de scenario.
/// <para>
/// Duplique volontairement <c>Tempest.Cli.CliDuration</c> plutot que d'en faire une dependance
/// partagee : les deux couches (CLI, format declaratif) ont chacune leur propre raison de faire
/// evoluer ce format, et <c>Tempest.Scenarios</c> ne doit pas dependre de <c>Tempest.Cli</c>.
/// </para>
/// </summary>
internal static class DurationText
{
    // "ms" doit etre teste avant "s" : sinon "500ms" matcherait le suffixe "s" avec un prefixe
    // numerique invalide ("500m") plutot que le bon suffixe.
    private static readonly (string Suffix, double MillisecondsPerUnit)[] _units =
    [
        ("ms", 1d),
        ("h", 3_600_000d),
        ("m", 60_000d),
        ("s", 1_000d),
    ];

    /// <summary>
    /// Convertit <paramref name="text"/> en <see cref="TimeSpan"/>. Un nombre sans suffixe est
    /// interprete en secondes.
    /// </summary>
    /// <exception cref="FormatException"><paramref name="text"/> n'est pas une duree reconnue.</exception>
    public static TimeSpan Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string trimmed = text.Trim();

        foreach ((string suffix, double millisecondsPerUnit) in _units)
        {
            if (trimmed.Length > suffix.Length && trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                string numeric = trimmed[..^suffix.Length];
                if (double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    return TimeSpan.FromMilliseconds(value * millisecondsPerUnit);
                }
            }
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        throw new FormatException(
            $"Duree invalide : '{text}'. Formats acceptes : '30s', '5m', '1h', '500ms', ou un nombre de secondes.");
    }
}