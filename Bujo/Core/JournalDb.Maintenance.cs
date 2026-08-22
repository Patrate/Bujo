using System.IO;

namespace Bujo.Core;

/// <summary>
/// Opérations destructrices. Les seules de toute l'application à faire de vrais
/// DELETE : partout ailleurs, une suppression est logique (deleted_at).
/// </summary>
public sealed partial class JournalDb
{
    /// <summary>
    /// Copie de sûreté horodatée, à appeler AVANT toute opération destructrice.
    /// Retourne le chemin du fichier créé.
    /// </summary>
    public string BackupBeforeWipe()
    {
        var dir = Path.Combine(Path.GetDirectoryName(DefaultPath)!, "backups");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"journal-avant-effacement-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.db");
        BackupTo(path);
        return path;
    }

    /// <summary>
    /// Efface tout l'historique : jours, entrées de journal, valeurs d'habitudes,
    /// événements de verrou. Conserve les habitudes elles-mêmes et les paramètres,
    /// pour que l'application reste utilisable immédiatement après.
    /// </summary>
    public void DeleteAllHistory()
    {
        using (var tx = Connection.BeginTransaction())
        {
            Exec("DELETE FROM habit_entries;", tx);
            Exec("DELETE FROM log_entries;", tx);
            Exec("DELETE FROM lock_events;", tx);
            Exec("DELETE FROM days;", tx);
            tx.Commit();
        }

        // VACUUM ne peut pas s'exécuter dans une transaction : il récupère l'espace
        // disque, sinon le fichier garde la taille de l'historique effacé.
        Exec("VACUUM;");
    }

    /// <summary>Efface uniquement les sorties forcées. Le reste du journal du verrou est conservé.</summary>
    public void ResetBypassCounter() => Exec("DELETE FROM lock_events WHERE kind = 'bypassed';");
}
