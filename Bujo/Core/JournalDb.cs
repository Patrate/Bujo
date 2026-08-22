using System.IO;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Bujo.Core;

public enum HabitValueType { Bool, Number, Text }

public sealed record RoutineItem(
    string HabitId,
    string Name,
    HabitValueType Type,
    bool Done,
    double? ValueNum,
    string? ValueText,
    int Position);

public sealed partial class JournalDb : IDisposable
{
    private readonly SqliteConnection _cx;
    private SqliteConnection Connection => _cx;
    
    /// <summary>
    /// Migrations dans l'ordre. L'index + 1 est le numéro de version.
    /// On n'édite JAMAIS un fichier déjà appliqué : on en ajoute un nouveau à la fin.
    /// </summary>
    private static readonly string[] Migrations =
    [
        "Bujo.Data.schema.sql",
        "Bujo.Data.migrations.002_typed_habits.sql",
        "Bujo.Data.migrations.003_habit_archive.sql",
        "Bujo.Data.migrations.004_drop_unit_target.sql",
        "Bujo.Data.migrations.005_migration_chain.sql"
    ];

    public string DeviceId { get; private set; } = "";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Bujo", "journal.db");

    public JournalDb(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _cx = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        _cx.Open();

        // Ces pragmas vivent ici, pas dans schema.sql : journal_mode ne peut pas
        // changer depuis l'intérieur d'une transaction, or les migrations en sont une.
        Exec("PRAGMA journal_mode = WAL;");
        Exec("PRAGMA foreign_keys = ON;");
        Exec("PRAGMA synchronous = NORMAL;");

        Migrate();
        EnsureDevice();
        SeedRoutineIfEmpty();
    }

    // ---------------------------------------------------------------- schéma

    private void Migrate()
    {
        var version = Convert.ToInt32(TryScalar("SELECT version FROM schema_version LIMIT 1;") ?? 0);

        for (var i = version; i < Migrations.Length; i++)
        {
            var sql = ReadResource(Migrations[i]);
            using var tx = _cx.BeginTransaction();
            Exec(sql, tx);
            Exec($"DELETE FROM schema_version; INSERT INTO schema_version (version) VALUES ({i + 1});", tx);
            tx.Commit();
        }
    }

    private static string ReadResource(string logicalName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Ressource introuvable : {logicalName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void EnsureDevice()
    {
        DeviceId = GetLocal("device_id") ?? "";
        if (DeviceId.Length > 0) return;

        DeviceId = Clock.NewId();
        Exec("INSERT INTO devices (id, name, platform, created_at) VALUES ($id, $n, 'windows', $t);",
            null, ("$id", DeviceId), ("$n", Environment.MachineName), ("$t", Clock.Stamp()));
        SetLocal("device_id", DeviceId);
    }

    private void SeedRoutineIfEmpty()
    {
        if (TryScalar("SELECT 1 FROM habits LIMIT 1;") is not null) return;

        (string Name, string Type)[] defaults =
        [
            ("Étirements", "bool"),
            ("Squats",     "number"),
            ("Douche",     "bool")
        ];

        for (var i = 0; i < defaults.Length; i++)
        {
            var (name, type) = defaults[i];
            Exec("""
                 INSERT INTO habits (id, name, value_type, unit, target_num, is_routine,
                                     position, active, created_at, updated_at, device_id)
                 VALUES ($id, $name, $type, $unit, $target, 1, $pos, 1, $t, $t, $dev);
                 """,
                null,
                ("$id", Clock.NewId()), ("$name", name), ("$type", type),
                ("$pos", i), ("$t", Clock.Stamp()), ("$dev", DeviceId));
        }
    }

    // ------------------------------------------------------------- la routine

    public IReadOnlyList<RoutineItem> GetRoutine(DateOnly day) => LoadHabitEntries(day, routineOnly: true);

    private static HabitValueType ParseType(string s) => s switch
    {
        "number" => HabitValueType.Number,
        "text" => HabitValueType.Text,
        _ => HabitValueType.Bool
    };

    /// <summary>
    /// Écrit la valeur saisie SANS toucher à done. Appelée à la frappe ou à la
    /// perte de focus : saisir 40 puis corriger en 45 ne valide jamais la ligne.
    /// </summary>
    public void SetHabitValue(string habitId, DateOnly day, double? number, string? text)
    {
        Exec("""
             INSERT INTO habit_entries (id, habit_id, logical_date, done,
                                        value_num, value_text, created_at, updated_at, device_id)
             VALUES ($id, $h, $d, 0, $num, $txt, $t, $t, $dev)
             ON CONFLICT (habit_id, logical_date) DO UPDATE SET
                 value_num  = excluded.value_num,
                 value_text = excluded.value_text,
                 updated_at = excluded.updated_at,
                 deleted_at = NULL,
                 device_id  = excluded.device_id;
             """,
            null,
            ("$id", Clock.NewId()), ("$h", habitId), ("$d", LogicalDay.Key(day)),
            ("$num", (object?)number ?? DBNull.Value), ("$txt", (object?)text ?? DBNull.Value),
            ("$t", Clock.Stamp()), ("$dev", DeviceId));
    }

    /// <summary>Validation explicite. Ne touche pas à la valeur : les deux sont indépendantes.</summary>
    public void SetHabitDone(string habitId, DateOnly day, bool done)
    {
        Exec("""
             INSERT INTO habit_entries (id, habit_id, logical_date, done, done_at,
                                        created_at, updated_at, device_id)
             VALUES ($id, $h, $d, $done, $doneAt, $t, $t, $dev)
             ON CONFLICT (habit_id, logical_date) DO UPDATE SET
                 done       = excluded.done,
                 done_at    = excluded.done_at,
                 updated_at = excluded.updated_at,
                 deleted_at = NULL,
                 device_id  = excluded.device_id;
             """,
            null,
            ("$id", Clock.NewId()), ("$h", habitId), ("$d", LogicalDay.Key(day)),
            ("$done", done ? 1 : 0), ("$doneAt", done ? Clock.Stamp() : (object)DBNull.Value),
            ("$t", Clock.Stamp()), ("$dev", DeviceId));
    }

    public bool IsRoutineDone(DateOnly day)
    {
        var routine = GetRoutine(day);
        return routine.Count > 0 && routine.All(i => i.Done);
    }

    public void MarkRoutineCompleted(DateOnly day)
    {
        EnsureDay(day);
        Exec("""
             UPDATE days SET routine_completed_at = COALESCE(routine_completed_at, $t),
                             updated_at = $t, device_id = $dev
             WHERE logical_date = $d;
             """,
            null, ("$t", Clock.Stamp()), ("$dev", DeviceId), ("$d", LogicalDay.Key(day)));
    }

    public void EnsureDay(DateOnly day) =>
        Exec("""
             INSERT INTO days (id, logical_date, created_at, updated_at, device_id)
             VALUES ($id, $d, $t, $t, $dev)
             ON CONFLICT (logical_date) DO NOTHING;
             """,
            null,
            ("$id", Clock.NewId()), ("$d", LogicalDay.Key(day)),
            ("$t", Clock.Stamp()), ("$dev", DeviceId));

    public void LogLockEvent(DateOnly day, string kind) =>
        Exec("""
             INSERT INTO lock_events (id, logical_date, kind, occurred_at, device_id)
             VALUES ($id, $d, $k, $t, $dev);
             """,
            null,
            ("$id", Clock.NewId()), ("$d", LogicalDay.Key(day)), ("$k", kind),
            ("$t", Clock.Stamp()), ("$dev", DeviceId));

    // Le compteur de sorties forcées a été retiré : information redondante avec la
    // série et le taux, double punition un jour raté, et la dissuasion par la honte
    // mène à l'abandon ou au contournement malhonnête. L'enregistrement dans
    // lock_events reste, il ne coûte rien et sert l'analyse hors application ;
    // GetBypassDays le lit encore.

    // ------------------------------------------------------------- plomberie

    public string? GetLocal(string key) =>
        TryScalar($"SELECT value FROM local_state WHERE key = '{key}';") as string;

    public void SetLocal(string key, string value) =>
        Exec("INSERT INTO local_state (key, value) VALUES ($k, $v) " +
             "ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            null, ("$k", key), ("$v", value));

    private object? TryScalar(string sql)
    {
        try
        {
            using var cmd = _cx.CreateCommand();
            cmd.CommandText = sql;
            var v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private void Exec(string sql, SqliteTransaction? tx = null, params (string, object)[] ps)
    {
        using var cmd = _cx.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    public void BackupTo(string destination)
    {
        if (File.Exists(destination)) File.Delete(destination);
        using var cmd = _cx.CreateCommand();
        cmd.CommandText = "VACUUM INTO $p;";
        cmd.Parameters.AddWithValue("$p", destination);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _cx.Dispose();
}
