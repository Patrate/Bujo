using System.IO;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Timer = System.Threading.Timer;

namespace Bujo.Core;

/// <summary>
/// Sauvegardes : copie locale à chaud, rotation, puis envoi optionnel par SFTP.
///
/// Trois principes tenus ici :
///   * la copie locale ne dépend PAS du réseau — une sauvegarde qui échoue parce
///     que le VPS est indisponible n'est pas une sauvegarde
///   * rien ne descend jamais du serveur. Ceci n'est pas de la synchronisation,
///     c'est de l'archivage unidirectionnel
///   * aucune exception ne sort d'ici : une sauvegarde ratée ne casse pas l'app
/// </summary>
public sealed class BackupService(JournalDb db, Settings settings) : IDisposable
{
    private const int KeepDays = 14;

    private Timer? _timer;

    public static string BackupDir => Path.Combine(
        Path.GetDirectoryName(JournalDb.DefaultPath)!, "backups");

    public string LastResult => db.GetLocal("backup.last_result") ?? "jamais exécutée";

    /// <summary>
    /// Vérifie maintenant, puis toutes les six heures. Un PC allumé en continu
    /// doit quand même produire une sauvegarde par jour.
    /// </summary>
    public void Start()
    {
        _timer = new Timer(_ => RunIfDue(), null, TimeSpan.FromSeconds(20), TimeSpan.FromHours(6));
    }

    private void RunIfDue() => Run(dueOnly: true);

    /// <summary>
    /// Sauvegarde complète. Synchrone : le timer tourne sur un thread de fond, et
    /// SSH.NET est de toute façon une API bloquante.
    ///
    /// IMPORTANT : ce code ouvre sa PROPRE connexion à la base. SqliteConnection
    /// n'est pas thread-safe, et l'interface utilise la sienne pendant ce temps.
    /// Le mode WAL rend cette concurrence sûre au niveau du fichier : plusieurs
    /// lecteurs, un seul écrivain à la fois, géré par le moteur.
    /// </summary>
    public string? Run(bool dueOnly = false)
    {
        JournalDb? work = null;
        string? path = null;
        try
        {
            work = new JournalDb();

            if (dueOnly
                && DateOnly.TryParse(work.GetLocal("backup.last_day"), out var last)
                && last == LogicalDay.Today()) return null;

            Directory.CreateDirectory(BackupDir);
            var name = $"journal-{LogicalDay.Key(LogicalDay.Today())}.db";
            path = Path.Combine(BackupDir, name);

            // VACUUM INTO : copie cohérente sans fermer la base ni bloquer l'écriture.
            work.BackupTo(path);
            Rotate();

            Upload(work, path, name);

            work.SetLocal("backup.last_day", LogicalDay.Key(LogicalDay.Today()));
            work.SetLocal("backup.last_result", $"OK — {DateTimeOffset.Now:dd/MM HH:mm}");
            return path;
        }
        catch (Exception ex)
        {
            // La copie locale a pu réussir avant l'échec d'envoi : on le distingue,
            // sinon un VPS injoignable donne l'impression qu'il n'y a aucune sauvegarde.
            var message = path is not null && File.Exists(path)
                ? $"Copie locale OK, envoi échoué : {ex.Message}"
                : $"Échec : {ex.Message}";
            try { work?.SetLocal("backup.last_result", message); } catch (Exception) { /* base inaccessible */ }
            Log(ex);
            return null;
        }
        finally
        {
            work?.Dispose();
        }
    }

    /// <summary>Ne conserve que les dernières copies locales : le disque n'est pas une archive infinie.</summary>
    private static void Rotate()
    {
        var files = new DirectoryInfo(BackupDir)
            .GetFiles("journal-*.db")
            .OrderByDescending(f => f.Name)
            .Skip(KeepDays);

        foreach (var f in files)
        {
            try { f.Delete(); } catch (IOException) { /* fichier verrouillé : au prochain passage */ }
        }
    }

    // ------------------------------------------------------------------ SFTP

    /// <summary>
    /// Envoi par SFTP. VpsUrl contient un hôte nu (« mon-vps.ovh.net »), sans
    /// schéma ni slash ; le dossier distant vient de backup.remote_dir.
    /// </summary>
    private void Upload(JournalDb work, string path, string name)
    {
        // La configuration est relue sur la connexion de travail, pas celle de l'UI.
        var host = (work.GetLocal("settings.vps_url") ?? "").Trim();
        var user = work.GetLocal("backup.user") ?? "";
        if (host.Length == 0 || user.Length == 0) return;   // non configuré : copie locale seule

        var remoteDir = work.GetLocal("backup.remote_dir") ?? "backups/bujo";

        using var client = new SftpClient(BuildConnection(work, host, user));
        client.Connect();
        EnsureRemoteDir(client, remoteDir);

        // Écriture sous un nom temporaire puis renommage : une coupure réseau ne
        // laisse jamais un fichier tronqué portant le nom d'une sauvegarde valide.
        var temp = $"{remoteDir}/{name}.part";
        var final = $"{remoteDir}/{name}";

        using (var stream = File.OpenRead(path))
            client.UploadFile(stream, temp, canOverride: true);

        if (client.Exists(final)) client.DeleteFile(final);
        client.RenameFile(temp, final);
        client.Disconnect();
    }

    /// <summary>Clé privée si elle est renseignée et lisible, mot de passe sinon.</summary>
    private static ConnectionInfo BuildConnection(JournalDb work, string host, string user)
    {
        var keyPath = work.GetLocal("backup.key_path");

        if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
        {
            var passphrase = Secret.Unprotect(work.GetLocal("backup.key_passphrase"));
            var key = string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(keyPath)
                : new PrivateKeyFile(keyPath, passphrase);
            return new ConnectionInfo(host, 22, user, new PrivateKeyAuthenticationMethod(user, key));
        }

        var password = Secret.Unprotect(work.GetLocal("backup.password")) ?? "";
        return new ConnectionInfo(host, 22, user, new PasswordAuthenticationMethod(user, password));
    }

    /// <summary>Crée l'arborescence distante segment par segment : SSH.NET n'a pas de mkdir -p.</summary>
    private static void EnsureRemoteDir(SftpClient client, string dir)
    {
        var current = dir.StartsWith('/') ? "" : ".";
        foreach (var segment in dir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? "/" + segment : current + "/" + segment;
            if (!client.Exists(current)) client.CreateDirectory(current);
        }
    }

    // --------------------------------------------------------- configuration

    public void SetRemote(string host, string remoteDir)
    {
        settings.VpsUrl = host.Trim();
        db.SetLocal("backup.remote_dir", remoteDir.Trim());
    }

    public void SetUser(string user) => db.SetLocal("backup.user", user.Trim());

    public void SetPassword(string password) =>
        db.SetLocal("backup.password", Secret.Protect(password) ?? "");

    /// <summary>Chemin vers la clé privée OpenSSH. La passphrase peut être nulle.</summary>
    public void SetKey(string keyPath, string? passphrase)
    {
        db.SetLocal("backup.key_path", keyPath.Trim());
        db.SetLocal("backup.key_passphrase", Secret.Protect(passphrase) ?? "");
    }

    private static void Log(Exception ex) =>
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "bujo-error.log"),
            $"{DateTimeOffset.Now:O} backup {ex}\n");

    public void Dispose() => _timer?.Dispose();
}

/// <summary>
/// Chiffrement des secrets par DPAPI, lié au compte Windows courant.
/// Ce n'est pas un coffre-fort : cela évite d'écrire un mot de passe en clair
/// dans un fichier que tu vas justement copier sur ton VPS.
/// </summary>
internal static class Secret
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Bujo.backup.v1");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return null;   // profil Windows différent : secret illisible, pas corrompu
        }
    }
}
