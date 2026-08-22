namespace Bujo.Core;

public enum LogKind { Task, Event, Note }
public enum LogState { Open, Done, Migrated, Dropped }

public sealed record LogEntry(
    string Id,
    LogKind Kind,
    string Content,
    LogState State,
    DateOnly? MigratedTo,
    int Position);

public sealed record DayInfo(int? Mood, string? Note, bool RoutineCompleted);

public sealed partial class JournalDb
{
    // ----------------------------------------------------------- daily log

    public IReadOnlyList<LogEntry> GetLog(DateOnly day)
    {
        var list = new List<LogEntry>();
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, content, state, migrated_to, position
            FROM log_entries
            WHERE logical_date = $d AND deleted_at IS NULL
            ORDER BY position, created_at;
            """;
        cmd.Parameters.AddWithValue("$d", LogicalDay.Key(day));

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new LogEntry(
                Id: r.GetString(0),
                Kind: ParseKind(r.GetString(1)),
                Content: r.GetString(2),
                State: ParseState(r.GetString(3)),
                MigratedTo: r.IsDBNull(4) ? null : DateOnly.Parse(r.GetString(4)),
                Position: r.GetInt32(5)));
        }
        return list;
    }

    public string AddLogEntry(DateOnly day, LogKind kind, string content)
    {
        EnsureDay(day);
        var position = Convert.ToInt32(TryScalar(
            $"SELECT COALESCE(MAX(position), -1) + 1 FROM log_entries " +
            $"WHERE logical_date = '{LogicalDay.Key(day)}' AND deleted_at IS NULL;") ?? 0);

        var id = Clock.NewId();
        Exec("""
             INSERT INTO log_entries (id, logical_date, kind, content, state, position,
                                      created_at, updated_at, device_id)
             VALUES ($id, $d, $kind, $content, 'open', $pos, $t, $t, $dev);
             """,
            null,
            ("$id", id), ("$d", LogicalDay.Key(day)), ("$kind", KindKey(kind)),
            ("$content", content), ("$pos", position),
            ("$t", Clock.Stamp()), ("$dev", DeviceId));
        return id;
    }

    public void UpdateLogContent(string entryId, string content) =>
        Exec("UPDATE log_entries SET content = $c, updated_at = $t, device_id = $dev WHERE id = $id;",
            null, ("$id", entryId), ("$c", content), ("$t", Clock.Stamp()), ("$dev", DeviceId));

    public void SetLogState(string entryId, LogState state) =>
        Exec("""
             UPDATE log_entries SET state = $s, migrated_to = NULL,
                                    updated_at = $t, device_id = $dev
             WHERE id = $id;
             """,
            null, ("$id", entryId), ("$s", StateKey(state)), ("$t", Clock.Stamp()), ("$dev", DeviceId));

    /// <summary>
    /// Migration au sens BuJo : l'entrée d'origine reste en place, marquée « migrée »
    /// vers sa destination, et une copie ouverte apparaît au jour cible. On garde ainsi
    /// la trace des reports, ce qui est tout l'intérêt de la revue mensuelle.
    /// </summary>
    public string MigrateLogEntry(string entryId, DateOnly target)
    {
        var source = FindEntry(entryId)
            ?? throw new InvalidOperationException($"Entrée introuvable : {entryId}");

        var copyId = AddLogEntry(target, source.Kind, source.Content);

        Exec("""
             UPDATE log_entries SET state = 'migrated', migrated_to = $target,
                                    updated_at = $t, device_id = $dev
             WHERE id = $id;
             """,
            null,
            ("$id", entryId), ("$target", LogicalDay.Key(target)),
            ("$t", Clock.Stamp()), ("$dev", DeviceId));

        return copyId;
    }

    /// <summary>Suppression logique : jamais de DELETE, sinon la synchro ressuscite la ligne.</summary>
    public void DeleteLogEntry(string entryId) =>
        Exec("UPDATE log_entries SET deleted_at = $t, updated_at = $t, device_id = $dev WHERE id = $id;",
            null, ("$id", entryId), ("$t", Clock.Stamp()), ("$dev", DeviceId));

    public void ReorderLog(IEnumerable<string> orderedIds)
    {
        var position = 0;
        foreach (var id in orderedIds)
            Exec("UPDATE log_entries SET position = $pos, updated_at = $t, device_id = $dev WHERE id = $id;",
                null, ("$id", id), ("$pos", position++), ("$t", Clock.Stamp()), ("$dev", DeviceId));
    }

    private LogEntry? FindEntry(string entryId)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, content, state, migrated_to, position
            FROM log_entries WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", entryId);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new LogEntry(
            r.GetString(0), ParseKind(r.GetString(1)), r.GetString(2), ParseState(r.GetString(3)),
            r.IsDBNull(4) ? null : DateOnly.Parse(r.GetString(4)), r.GetInt32(5));
    }
    
    /// <summary>
    /// Requête commune à la routine (verrou) et au journal. routineOnly = false
    /// ramène aussi les habitudes suivies hors routine, qui ne bloquent pas l'écran.
    /// </summary>
    private IReadOnlyList<RoutineItem> LoadHabitEntries(DateOnly day, bool routineOnly)
    {
        var items = new List<RoutineItem>();
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"""
                           SELECT h.id, h.name, h.value_type,
                                  COALESCE(e.done, 0), e.value_num, e.value_text, h.position
                           FROM habits h
                           LEFT JOIN habit_entries e
                                  ON e.habit_id = h.id
                                 AND e.logical_date = $d
                                 AND e.deleted_at IS NULL
                           WHERE h.active = 1 AND h.archived_at IS NULL AND h.deleted_at IS NULL
                                 {(routineOnly ? "AND h.is_routine = 1" : "")}
                           ORDER BY h.position;
                           """;
        cmd.Parameters.AddWithValue("$d", LogicalDay.Key(day));

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            items.Add(new RoutineItem(
                HabitId: r.GetString(0),
                Name: r.GetString(1),
                Type: ParseType(r.GetString(2)),
                Done: r.GetInt32(3) == 1,
                ValueNum: r.IsDBNull(4) ? null : r.GetDouble(4),
                ValueText: r.IsDBNull(5) ? null : r.GetString(5),
                Position: r.GetInt32(6)));
        }
        return items;
    }

    /// <summary>Toutes les habitudes actives du jour, routine ou non.</summary>
    public IReadOnlyList<RoutineItem> GetDayHabits(DateOnly day) => LoadHabitEntries(day, routineOnly: false);

    // ------------------------------------------------------ humeur et note

    public DayInfo GetDay(DateOnly day)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
            SELECT mood, note, routine_completed_at
            FROM days WHERE logical_date = $d AND deleted_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$d", LogicalDay.Key(day));

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new DayInfo(null, null, false);
        return new DayInfo(
            r.IsDBNull(0) ? null : r.GetInt32(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            !r.IsDBNull(2));
    }

    public void SetDayMood(DateOnly day, int? mood)
    {
        EnsureDay(day);
        Exec("UPDATE days SET mood = $m, updated_at = $t, device_id = $dev WHERE logical_date = $d;",
            null,
            ("$d", LogicalDay.Key(day)), ("$m", (object?)mood ?? DBNull.Value),
            ("$t", Clock.Stamp()), ("$dev", DeviceId));
    }

    public void SetDayNote(DateOnly day, string? note)
    {
        EnsureDay(day);
        Exec("UPDATE days SET note = $n, updated_at = $t, device_id = $dev WHERE logical_date = $d;",
            null,
            ("$d", LogicalDay.Key(day)), ("$n", (object?)note ?? DBNull.Value),
            ("$t", Clock.Stamp()), ("$dev", DeviceId));
    }

    // --------------------------------------------------------- conversions

    private static LogKind ParseKind(string s) => s switch
    {
        "event" => LogKind.Event,
        "note" => LogKind.Note,
        _ => LogKind.Task
    };

    private static string KindKey(LogKind k) => k switch
    {
        LogKind.Event => "event",
        LogKind.Note => "note",
        _ => "task"
    };

    private static LogState ParseState(string s) => s switch
    {
        "done" => LogState.Done,
        "migrated" => LogState.Migrated,
        "dropped" => LogState.Dropped,
        _ => LogState.Open
    };

    private static string StateKey(LogState s) => s switch
    {
        LogState.Done => "done",
        LogState.Migrated => "migrated",
        LogState.Dropped => "dropped",
        _ => "open"
    };
}
