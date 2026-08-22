using System.IO;

namespace Bujo.Core;

/// <summary>
/// Journal d'erreurs de l'application. Un seul point d'écriture, dans
/// %APPDATA%\Bujo\logs\ : le dossier temporaire est purgé sans prévenir, par
/// Windows comme par n'importe quel nettoyeur de disque, et un journal effacé
/// le jour où on en a besoin ne sert à rien.
///
/// Règle absolue : cette classe n'échoue JAMAIS. Elle est appelée depuis des blocs
/// catch, dont celui du thread de sauvegarde. Une exception levée ici sortirait du
/// catch, remonterait sur un thread de pool que personne ne surveille, et tuerait
/// le processus — au lieu d'écrire la ligne qui expliquait la panne.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string LogDir => Path.Combine(
        Path.GetDirectoryName(JournalDb.DefaultPath)!, "logs");

    public static void Write(string source, Exception ex) => Write(source, ex.ToString());

    public static void Write(string source, string message)
    {
        try
        {
            // Le service de sauvegarde écrit depuis un thread de fond pendant que
            // l'interface vit sur le sien. AppendAllText ouvre le fichier sans
            // partage en écriture : deux appels simultanés se percutent.
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                // Un fichier par mois : borne la taille sans code de rotation,
                // et garde l'historique long, qui est tout l'intérêt d'un journal.
                var path = Path.Combine(LogDir, $"bujo-{DateTimeOffset.Now:yyyy-MM}.log");
                File.AppendAllText(path,
                    $"{DateTimeOffset.Now:O} [{source}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Disque plein, profil en lecture seule, fichier verrouillé par un
            // antivirus : perdre une ligne de journal est acceptable, planter non.
        }
    }
}
