using System.IO;
using System.Text;

namespace Bujo.Core;

/// <summary>
/// Export pour analyse hors application. Lecture seule, aucune écriture en base.
///
/// L'export est FIDÈLE : il sort aussi les lignes supprimées logiquement et les
/// habitudes archivées, avec leurs colonnes deleted_at et archived_at. Filtrer à
/// la source donnerait un fichier plus propre mais mensonger, et rendrait
/// impossible de comprendre un trou dans l'historique. Le filtre appartient à qui
/// analyse, pas à qui exporte.
/// </summary>
public sealed partial class JournalDb
{
    public static string ExportDir => Path.Combine(
        Path.GetDirectoryName(DefaultPath)!, "exports");

    /// <summary>
    /// Écrit les deux fichiers et retourne leurs chemins. Horodatés, jamais écrasés :
    /// deux exports du même jour se comparent, c'est souvent tout l'intérêt.
    /// </summary>
    public IReadOnlyList<string> ExportCsv(string directory)
    {
        Directory.CreateDirectory(directory);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");

        var habits = Path.Combine(directory, $"habit_entries-{stamp}.csv");
        var days = Path.Combine(directory, $"days-{stamp}.csv");

        // Le nom d'habitude est joint ici : un fichier qui ne contiendrait que des
        // UUID serait illisible dans un tableur, et obligerait à exporter une
        // troisième table pour faire la correspondance à la main.
        WriteQuery(habits, """
            SELECT h.id            AS habit_id,
                   h.name          AS habit_name,
                   h.value_type    AS value_type,
                   h.archived_at   AS habit_archived_at,
                   e.logical_date  AS logical_date,
                   e.done          AS done,
                   e.done_at       AS done_at,
                   e.value_num     AS value_num,
                   e.value_text    AS value_text,
                   e.created_at    AS created_at,
                   e.updated_at    AS updated_at,
                   e.deleted_at    AS deleted_at,
                   e.device_id     AS device_id
            FROM habit_entries e
            JOIN habits h ON h.id = e.habit_id
            ORDER BY e.logical_date, h.position, h.name;
            """);

        WriteQuery(days, """
            SELECT logical_date, mood, note, routine_completed_at,
                   created_at, updated_at, deleted_at, device_id
            FROM days
            ORDER BY logical_date;
            """);

        return [habits, days];
    }

    /// <summary>
    /// Écrit le résultat d'une requête, en-tête compris. Les noms de colonnes sont
    /// pris du lecteur et non écrits en dur : ajouter une colonne à la requête
    /// suffit, l'en-tête ne peut pas se désynchroniser des données.
    ///
    /// Écriture en flux plutôt qu'en mémoire : sans intérêt aujourd'hui, mais rien
    /// ne borne la croissance de habit_entries avec les années.
    /// </summary>
    private void WriteQuery(string path, string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;

        // encoderShouldEmitUTF8Identifier = true : c'est le BOM. Sans lui, Excel
        // ouvre le fichier en ANSI et massacre tous les accents.
        using var writer = new StreamWriter(path, append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        using var r = cmd.ExecuteReader();

        var header = new string[r.FieldCount];
        for (var i = 0; i < r.FieldCount; i++) header[i] = r.GetName(i);
        writer.WriteLine(Csv.Row(header));

        var cells = new string[r.FieldCount];
        while (r.Read())
        {
            // GetValue rend DBNull pour NULL, double pour REAL, long pour INTEGER,
            // string pour TEXT : Csv.Cell fait le tri, une seule fois pour toutes
            // les colonnes des deux exports.
            for (var i = 0; i < r.FieldCount; i++) cells[i] = Csv.Cell(r.GetValue(i));
            writer.WriteLine(Csv.Row(cells));
        }
    }
}
