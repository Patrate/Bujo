namespace Bujo.Core;

/// <summary>Lectures dédiées aux vues de suivi. Aucune écriture ici.</summary>
public sealed partial class JournalDb
{
    /// <summary>
    /// Toutes les habitudes, archivées comprises. C'est la raison d'être de
    /// l'archivage : une habitude retirée de la routine garde son historique lisible.
    /// </summary>
    public IReadOnlyList<HabitSummary> GetHabitsForStats()
    {
        var list = new List<HabitSummary>();
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, value_type, archived_at
            FROM habits
            WHERE deleted_at IS NULL
            ORDER BY (archived_at IS NOT NULL), position;
            """;

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new HabitSummary(
                Id: r.GetString(0),
                Name: r.GetString(1),
                Type: ParseType(r.GetString(2)),
                Archived: !r.IsDBNull(3)));
        }
        return list;
    }

    public IReadOnlyList<HabitDay> GetHabitHistory(string habitId, DateOnly from, DateOnly to)
    {
        var list = new List<HabitDay>();
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
            SELECT logical_date, done, value_num
            FROM habit_entries
            WHERE habit_id = $h AND logical_date BETWEEN $from AND $to AND deleted_at IS NULL
            ORDER BY logical_date;
            """;
        cmd.Parameters.AddWithValue("$h", habitId);
        cmd.Parameters.AddWithValue("$from", LogicalDay.Key(from));
        cmd.Parameters.AddWithValue("$to", LogicalDay.Key(to));

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new HabitDay(
                Day: DateOnly.Parse(r.GetString(0)),
                Done: r.GetInt32(1) == 1,
                Number: r.IsDBNull(2) ? null : r.GetDouble(2)));
        }
        return list;
    }

    /// <summary>Jours où la routine complète a été validée, sur la fenêtre donnée.</summary>
    public IReadOnlyList<DateOnly> GetRoutineCompletedDays(DateOnly from, DateOnly to)
    {
        var list = new List<DateOnly>();
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
            SELECT logical_date
            FROM days
            WHERE routine_completed_at IS NOT NULL
              AND logical_date BETWEEN $from AND $to
              AND deleted_at IS NULL
            ORDER BY logical_date;
            """;
        cmd.Parameters.AddWithValue("$from", LogicalDay.Key(from));
        cmd.Parameters.AddWithValue("$to", LogicalDay.Key(to));

        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(DateOnly.Parse(r.GetString(0)));
        return list;
    }

    /// <summary>Sorties forcées du verrou, du plus récent au plus ancien.</summary>
    public IReadOnlyList<DateOnly> GetBypassDays(DateOnly from, DateOnly to)
    {
        var list = new List<DateOnly>();
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
            SELECT logical_date
            FROM lock_events
            WHERE kind = 'bypassed' AND logical_date BETWEEN $from AND $to
            ORDER BY occurred_at DESC;
            """;
        cmd.Parameters.AddWithValue("$from", LogicalDay.Key(from));
        cmd.Parameters.AddWithValue("$to", LogicalDay.Key(to));

        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(DateOnly.Parse(r.GetString(0)));
        return list;
    }
}
