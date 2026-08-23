using System.Globalization;
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
        "Bujo.Data.migrations.005_migration_chain.sql",
        "Bujo.Data.migrations.006_habit_schedule.sql"
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

    /// <summary>
    /// Routine de démarrage sur base neuve.
    ///
    /// CORRECTION V1.2 : cet INSERT nommait encore les colonnes unit et target_num,
    /// retirées par la migration 004, et ne fournissait aucun paramètre pour elles.
    /// Sur une base VIERGE — le seul cas où ce code s'exécute — il levait
    /// « no such column: unit », l'exception remontait au constructeur, et Program
    /// journalisait puis rendait 1 : première installation impossible. Aucune base
    /// déjà semée ne pouvait révéler le défaut, ce chemin ne s'y exécute plus jamais.
    /// </summary>
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
            // Les colonnes d'horaire sont omises : leurs DEFAULT valent quotidienne,
            // ce qui est bien ce qu'on veut pour une routine de démarrage.
            Exec("""
                 INSERT INTO habits (id, name, value_type, is_routine,
                                     position, active, created_at, updated_at, device_id)
                 VALUES ($id, $name, $type, 1, $pos, 1, $t, $t, $dev);
                 """,
                null,
                ("$id", Clock.NewId()), ("$name", name), ("$type", type),
                ("$pos", i), ("$t", Clock.Stamp()), ("$dev", DeviceId));
        }

        // Une base vierge et une vraie base sont INDISCERNABLES à l'œil nu : le semis
        // crée « Étirements », « Squats », « Douche », c'est-à-dire exactement les
        // habitudes réelles. Une matinée de tests a été menée sur une base neuve sans
        // que personne s'en aperçoive, et seul un SELECT created_at l'a révélé.
        // Cette ligne est le seul endroit où l'application dit qu'elle est repartie de zéro.
        Log.Write("seed", $"base vierge : routine de démarrage créée, {defaults.Length} habitudes");
    }

    // ------------------------------------------------------------- la routine

    /// <summary>
    /// Routine du jour. Depuis la V1.2, elle est filtrée sur les JOURS DUS : une
    /// habitude « lundi et mercredi » n'apparaît pas un mardi, ne bloque pas l'écran
    /// et n'entre pas dans IsRoutineDone.
    ///
    /// Conséquence assumée et voulue : un jour où rien n'est dû rend une liste vide,
    /// donc HasRoutine répond false et le verrou ne peut pas apparaître. Une routine
    /// vide pour le jour courant est un état légitime, désormais ordinaire.
    /// </summary>
    public IReadOnlyList<RoutineItem> GetRoutine(DateOnly day) => LoadHabitEntries(day, routineOnly: true);

    private static HabitValueType ParseType(string s) => s switch
    {
        "number" => HabitValueType.Number,
        "text" => HabitValueType.Text,
        _ => HabitValueType.Bool
    };

    // ------------------------------------------------------------- horaires

    /// <summary>
    /// Lit les quatre colonnes d'horaire à partir de l'ordinal de schedule_kind.
    /// Les quatre sont TOUJOURS sélectionnées ensemble et dans cet ordre : kind,
    /// days, interval, anchor. Un seul point de lecture, donc un seul endroit à
    /// corriger le jour où une cinquième colonne arrive.
    /// </summary>
    private static Schedule ReadSchedule(SqliteDataReader r, int ordinal)
    {
        // ParseExact et non Parse : l'ancre est un jour logique 'YYYY-MM-DD', son
        // analyse ne doit rien devoir à la culture de la machine.
        DateOnly? anchor = r.IsDBNull(ordinal + 3)
            ? null
            : DateOnly.ParseExact(r.GetString(ordinal + 3), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new Schedule(
            ParseScheduleKind(r.GetString(ordinal)),
            r.GetInt32(ordinal + 1),
            r.GetInt32(ordinal + 2),
            anchor);
    }

    /// <summary>Toute valeur inconnue retombe sur quotidienne : jamais de ligne illisible.</summary>
    private static ScheduleKind ParseScheduleKind(string s) => s switch
    {
        "weekly" => ScheduleKind.Weekly,
        "interval" => ScheduleKind.Interval,
        _ => ScheduleKind.Daily
    };

    private static string ScheduleKindKey(ScheduleKind k) => k switch
    {
        ScheduleKind.Weekly => "weekly",
        ScheduleKind.Interval => "interval",
        _ => "daily"
    };

    /// <summary>
    /// Jour logique d'un horodatage stocké en base, pour borner les jours dus par la
    /// création et par l'archivage d'une habitude.
    ///
    /// Passe par LogicalDay.Of et NON par For : le décalage de mise au point ne doit
    /// surtout pas s'appliquer ici. Il simule « on est demain », et cette borne est
    /// comparée à des logical_date déjà écrits, qui eux ne bougent pas. Le décalage
    /// poussait donc la date de création par-dessus tout l'historique, et un taux de
    /// 100 % tombait à 0 % au premier « + 1 jour ».
    ///
    /// Ne lève jamais : un horodatage illisible rend DateOnly.MinValue, c'est-à-dire
    /// une borne qui n'exclut rien. Refuser d'afficher une statistique parce qu'une
    /// ligne est mal formée serait pire que l'afficher un peu trop généreusement.
    /// </summary>
    private static DateOnly LogicalDayOf(string stamp) =>
        DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
            ? LogicalDay.Of(when)
            : DateOnly.MinValue;

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

    // ---------------------------------------------------------------- snooze

    private const string SnoozeDayKey = "lock.snooze_day";
    private const string SnoozeUntilKey = "lock.snooze_until";

    /// <summary>
    /// Pose un snooze. DEUX clés, et elles sont testées ensemble : le jour logique de
    /// la pose et l'instant de fin.
    ///
    /// Un horodatage absolu seul ne suffirait pas. Posé à 5 h 58 pour 30 minutes, il
    /// courrait jusqu'à 6 h 28 et offrirait gratuitement le verrou du jour SUIVANT.
    /// Avec le jour logique en garde, la bascule invalide le snooze par construction,
    /// sans aucun code de nettoyage.
    /// </summary>
    public void SetSnooze(DateOnly day, DateTimeOffset until)
    {
        SetLocal(SnoozeDayKey, LogicalDay.Key(day));
        SetLocal(SnoozeUntilKey, until.ToUniversalTime().ToString("O"));
    }

    /// <summary>
    /// Instant de fin du snooze en cours pour ce jour logique, ou null s'il n'y en a
    /// pas, s'il concerne un autre jour, ou s'il est déjà expiré.
    ///
    /// Rend l'instant plutôt qu'un booléen parce que le contrôleur en a besoin pour
    /// réarmer son timer après un redémarrage : le snooze vit en base, pas en mémoire,
    /// et quitter puis relancer Bujo pendant un snooze ne doit pas le rendre éternel.
    /// </summary>
    public DateTimeOffset? SnoozeEnd(DateOnly day)
    {
        if (GetLocal(SnoozeDayKey) != LogicalDay.Key(day)) return null;

        var raw = GetLocal(SnoozeUntilKey);
        if (string.IsNullOrEmpty(raw)) return null;

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var until)
               && DateTimeOffset.UtcNow < until
            ? until
            : null;
    }

    public bool IsSnoozed(DateOnly day) => SnoozeEnd(day) is not null;

    /// <summary>
    /// Efface le snooze de ce jour, s'il y en a un. La garde sur le jour évite qu'une
    /// routine complétée sur un jour PASSÉ, depuis le Journal, n'efface le snooze du
    /// matin en cours.
    ///
    /// Chaîne vide plutôt que DELETE : local_state n'est pas synchronisée et n'a pas
    /// de suppression logique, mais garder un seul verbe d'écriture pour cette table
    /// évite d'avoir à se demander lequel s'applique.
    /// </summary>
    public void ClearSnooze(DateOnly day)
    {
        if (GetLocal(SnoozeDayKey) != LogicalDay.Key(day)) return;
        SetLocal(SnoozeDayKey, "");
        SetLocal(SnoozeUntilKey, "");
    }

    /// <summary>
    /// Vrai seulement si quelque chose était dû ET que tout est validé. Le
    /// Count > 0 n'est pas une précaution : c'est lui qui empêche qu'un jour où rien
    /// n'est dû soit marqué « routine validée » et gonfle la série de dimanches gratuits.
    /// </summary>
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

        // La routine est faite : le snooze n'a plus rien à retarder. Le laisser
        // expirer seul serait sans conséquence — il est borné par le jour logique —
        // mais il resterait en base un état qui ne décrit plus rien.
        ClearSnooze(day);
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
