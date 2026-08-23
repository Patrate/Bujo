namespace Bujo.Core;

/// <summary>
/// Une habitude telle que l'écran de configuration la manipule.
/// </summary>
/// <param name="Schedule">
/// Horaire. Valeur par défaut = quotidienne, ce qui permet aux appelants qui ne
/// gèrent pas encore les horaires de construire un Habit sans le mentionner.
///
/// PIÈGE, à lever en même temps que l'interface : UpdateHabit ÉCRIT cette valeur.
/// Un appelant qui construit un Habit sans reprendre l'horaire existant remet donc
/// l'habitude en quotidienne à la première application. C'est sans effet tant que
/// rien ne sait poser un autre horaire, mais RoutineConfigView devra charger
/// l'horaire dans son brouillon en même temps qu'il saura l'éditer.
/// </param>
public sealed record Habit(
    string Id,
    string Name,
    HabitValueType Type,
    bool IsRoutine,
    bool Active,
    int Position,
    Schedule Schedule = default);

/// <summary>
/// Gestion des habitudes. Séparé dans un fichier partiel pour garder JournalDb lisible.
/// Déclare la classe principale en `public sealed partial class JournalDb`.
/// </summary>
public sealed partial class JournalDb
{
    /// <summary>Habitudes visibles dans l'écran de configuration (ni archivées, ni supprimées).</summary>
    public IReadOnlyList<Habit> GetHabits()
    {
        var list = new List<Habit>();
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, value_type, is_routine, active, position,
                   schedule_kind, schedule_days, schedule_interval, schedule_anchor
            FROM habits
            WHERE archived_at IS NULL AND deleted_at IS NULL
            ORDER BY position;
            """;

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Habit(
                Id: r.GetString(0),
                Name: r.GetString(1),
                Type: ParseType(r.GetString(2)),
                IsRoutine: r.GetInt32(3) == 1,
                Active: r.GetInt32(4) == 1,
                Position: r.GetInt32(5),
                Schedule: ReadSchedule(r, 6)));
        }
        return list;
    }

    /// <summary>
    /// Une routine vide pour le jour demandé est un état légitime. Deux causes
    /// depuis la V1.2 : aucune habitude configurée, ou aucune habitude due ce
    /// jour-là. Dans les deux cas le verrou ne peut pas apparaître.
    /// </summary>
    public bool HasRoutine(DateOnly day) => GetRoutine(day).Count > 0;

    public string AddHabit(string name, HabitValueType type, bool isRoutine, int position,
                           Schedule schedule = default)
    {
        var id = Clock.NewId();
        schedule = NormalizeSchedule(schedule);

        Exec("""
             INSERT INTO habits (id, name, value_type, is_routine,
                                 position, active,
                                 schedule_kind, schedule_days, schedule_interval, schedule_anchor,
                                 created_at, updated_at, device_id)
             VALUES ($id, $name, $type, $routine, $pos, 1,
                     $sk, $sd, $si, $sa,
                     $t, $t, $dev);
             """,
            null,
            ("$id", id), ("$name", name), ("$type", TypeKey(type)),
            ("$routine", isRoutine ? 1 : 0), ("$pos", position),
            ("$sk", ScheduleKindKey(schedule.Kind)), ("$sd", schedule.Weekdays),
            ("$si", schedule.Interval), ("$sa", AnchorValue(schedule)),
            ("$t", Clock.Stamp()), ("$dev", DeviceId));
        return id;
    }

    public void UpdateHabit(Habit h)
    {
        var schedule = NormalizeSchedule(h.Schedule);

        Exec("""
             UPDATE habits SET name = $name, value_type = $type, is_routine = $routine,
                               active = $active, position = $pos,
                               schedule_kind = $sk, schedule_days = $sd,
                               schedule_interval = $si, schedule_anchor = $sa,
                               updated_at = $t, device_id = $dev
             WHERE id = $id;
             """,
            null,
            ("$id", h.Id), ("$name", h.Name), ("$type", TypeKey(h.Type)),
            ("$routine", h.IsRoutine ? 1 : 0), ("$active", h.Active ? 1 : 0), ("$pos", h.Position),
            ("$sk", ScheduleKindKey(schedule.Kind)), ("$sd", schedule.Weekdays),
            ("$si", schedule.Interval), ("$sa", AnchorValue(schedule)),
            ("$t", Clock.Stamp()), ("$dev", DeviceId));
    }

    /// <summary>
    /// Deux nettoyages avant écriture, au seul point où les colonnes d'horaire sont
    /// posées, pour qu'aucun appelant n'ait à y penser.
    ///
    /// Canonical() remet à zéro les champs sans objet pour le mode : un horaire
    /// quotidien qui traînerait un masque de jours décrirait une règle qui n'existe
    /// pas, et l'export la sortirait telle quelle.
    ///
    /// Une règle d'intervalle sans ancre serait ensuite en phase avec l'origine du
    /// calendrier, c'est-à-dire avec rien de compréhensible. On l'ancre au jour de
    /// l'écriture : « tous les 3 jours à partir d'aujourd'hui » est ce que quelqu'un
    /// veut dire en choisissant cet horaire.
    /// </summary>
    private static Schedule NormalizeSchedule(Schedule s)
    {
        s = s.Canonical();
        return s.Kind == ScheduleKind.Interval && s.Anchor is null
            ? s with { Anchor = LogicalDay.Today() }
            : s;
    }

    /// <summary>
    /// L'ancre n'est écrite QUE pour le mode intervalle. Laisser traîner l'ancre d'un
    /// horaire abandonné donnerait une colonne qui décrit une règle qui n'existe plus,
    /// et l'export la sortirait telle quelle.
    /// </summary>
    private static object AnchorValue(Schedule s) =>
        s.Kind == ScheduleKind.Interval && s.Anchor is not null
            ? LogicalDay.Key(s.Anchor.Value)
            : DBNull.Value;

    /// <summary>Sort l'habitude de la configuration. Ses entrées passées restent intactes.</summary>
    public void ArchiveHabit(string habitId) =>
        Exec("""
             UPDATE habits SET archived_at = $t, active = 0, is_routine = 0,
                               updated_at = $t, device_id = $dev
             WHERE id = $id;
             """,
            null, ("$id", habitId), ("$t", Clock.Stamp()), ("$dev", DeviceId));

    /// <summary>
    /// Suppression franche. Avec la zone dangereuse, la seule de l'application à
    /// faire un vrai DELETE — réservée au mode développeur.
    ///
    /// Les habit_entries partent d'abord, et cet ordre n'est pas une préférence :
    /// habit_entries.habit_id référence habits(id), et foreign_keys est à ON.
    /// Supprimer l'habitude en premier lèverait « FOREIGN KEY constraint failed ».
    ///
    /// Rien de tout cela n'est synchronisable : une ligne qui disparaît sans laisser
    /// de deleted_at revient au premier échange avec un autre appareil. C'est
    /// acceptable pour un outil de mise au point, cela ne le serait pas ailleurs.
    /// </summary>
    public void DeleteHabitForever(string habitId)
    {
        using var tx = Connection.BeginTransaction();
        Exec("DELETE FROM habit_entries WHERE habit_id = $id;", tx, ("$id", habitId));
        Exec("DELETE FROM habits WHERE id = $id;", tx, ("$id", habitId));
        tx.Commit();
    }

    /// <summary>Nombre d'entrées rattachées, pour annoncer l'ampleur avant de détruire.</summary>
    public int CountHabitEntries(string habitId)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM habit_entries WHERE habit_id = $id;";
        cmd.Parameters.AddWithValue("$id", habitId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static string TypeKey(HabitValueType t) => t switch
    {
        HabitValueType.Number => "number",
        HabitValueType.Text => "text",
        _ => "bool"
    };
}
