namespace Bujo.Core;

/// <summary>Lectures dédiées aux vues de suivi. Aucune écriture ici.</summary>
public sealed partial class JournalDb
{
    /// <summary>
    /// Toutes les habitudes, archivées comprises. C'est la raison d'être de
    /// l'archivage : une habitude retirée de la routine garde son historique lisible.
    ///
    /// Chacune ramène son horaire BORNÉ par sa création et par son archivage, sous
    /// forme de HabitSchedule. C'est ce qui permet au Suivi de ne rien savoir des
    /// horaires : il demande une liste de jours dus et la passe à Stats.
    /// </summary>
    public IReadOnlyList<HabitSummary> GetHabitsForStats()
    {
        var list = new List<HabitSummary>();
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, value_type, archived_at,
                   schedule_kind, schedule_days, schedule_interval, schedule_anchor,
                   created_at
            FROM habits
            WHERE deleted_at IS NULL
            ORDER BY (archived_at IS NOT NULL), position;
            """;

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var archived = r.IsDBNull(3) ? (DateOnly?)null : LogicalDayOf(r.GetString(3));

            list.Add(new HabitSummary(
                Id: r.GetString(0),
                Name: r.GetString(1),
                Type: ParseType(r.GetString(2)),
                Archived: !r.IsDBNull(3),
                Due: new HabitSchedule(ReadSchedule(r, 4), LogicalDayOf(r.GetString(8)), archived)));
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

    /// <summary>
    /// Jours où la ROUTINE est due sur la fenêtre donnée, par ordre croissant.
    ///
    /// Un jour dû pour la routine est un jour où au moins une habitude de routine est
    /// due : l'union des horaires, pas leur intersection. Prendre l'intersection
    /// rendrait la routine non due dès que deux habitudes ont des horaires disjoints,
    /// et le verrou n'apparaîtrait plus jamais.
    ///
    /// Seules les habitudes de routine VIVANTES comptent : archiver une habitude
    /// change donc les jours dus passés de la routine. C'est la même règle que pour
    /// le reste — les jours dus se déduisent toujours de la configuration courante.
    /// </summary>
    public IReadOnlyList<DateOnly> GetRoutineDueDays(DateOnly from, DateOnly to)
    {
        var schedules = new List<HabitSchedule>();

        using (var cmd = Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT schedule_kind, schedule_days, schedule_interval, schedule_anchor,
                       created_at
                FROM habits
                WHERE is_routine = 1 AND active = 1
                  AND archived_at IS NULL AND deleted_at IS NULL;
                """;

            using var r = cmd.ExecuteReader();
            while (r.Read())
                schedules.Add(new HabitSchedule(ReadSchedule(r, 0), LogicalDayOf(r.GetString(4)), null));
        }

        // SortedSet plutôt qu'une liste triée après coup : l'union doit dédoublonner,
        // deux habitudes dues le même lundi ne font qu'un jour dû.
        var days = new SortedSet<DateOnly>();
        foreach (var schedule in schedules)
            foreach (var day in schedule.DueDays(from, to))
                days.Add(day);

        return [.. days];
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
