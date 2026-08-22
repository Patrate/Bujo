using System.Globalization;

namespace Bujo.Core;

/// <summary>
/// Écriture de cellules CSV. Trois pièges valent d'être écrits noir sur blanc,
/// parce qu'ils ne se voient qu'une fois le fichier ouvert dans un tableur :
///
///   * SÉPARATEUR — la virgule est le standard, mais Excel en configuration
///     française attend le point-virgule, et met sinon toute la ligne dans une
///     seule colonne. Point-virgule ici, et le séparateur décimal reste donc la
///     virgule locale sans jamais entrer en collision.
///
///   * ÉCHAPPEMENT — une note du jour peut contenir un point-virgule, un guillemet
///     ou un retour à la ligne, le champ étant multiligne. Sans guillemets doublés,
///     une seule note décale tout le reste du fichier.
///
///   * BOM — sans marque d'ordre des octets, Excel lit le fichier en ANSI et
///     « Étirements » devient « Ã‰tirements ». Voir JournalDb.Export.
/// </summary>
public static class Csv
{
    public const char Separator = ';';

    public static string Row(IReadOnlyList<string> cells) => string.Join(Separator, cells);

    /// <summary>
    /// Formate une valeur brute lue en base. Les nombres suivent la culture locale,
    /// pour qu'Excel les reconnaisse comme des nombres et non comme du texte ;
    /// l'analyse en Python demandera decimal=',' et sep=';', deux arguments.
    /// </summary>
    public static string Cell(object? value) => value switch
    {
        null or DBNull => "",
        // "0.#########" en notation .NET : jamais de notation scientifique, et pas
        // de zéros décimaux inutiles derrière un entier saisi comme 40.
        double d => Escape(d.ToString("0.#########", CultureInfo.CurrentCulture)),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => Escape(value.ToString() ?? "")
    };

    private static string Escape(string s) =>
        s.Contains(Separator) || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? '"' + s.Replace("\"", "\"\"") + '"'
            : s;
}
