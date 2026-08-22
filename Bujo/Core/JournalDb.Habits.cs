namespace Bujo.Core;

public sealed record Habit(
    string Id,
    string Name,
    HabitValueType Type,
    bool IsRoutine,
    bool Active,
    int Position);

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
            SELECT id, name, value_type, is_routine, active, position
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
                Position: r.GetInt32(5)));
        }
        return list;
    }

    /// <summary>Une routine vide est un état légitime : l'app devient une archive consultable.</summary>
    public bool HasRoutine(DateOnly day) => GetRoutine(day).Count > 0;

    public string AddHabit(string name, HabitValueType type, bool isRoutine, int position)
    {
        var id = Clock.NewId();
        Exec("""
             INSERT INTO habits (id, name, value_type, is_routine,
                                 position, active, created_at, updated_at, device_id)
             VALUES ($id, $name, $type, $routine, $pos, 1, $t, $t, $dev);
             """,
            null,
            ("$id", id), ("$name", name), ("$type", TypeKey(type)),
            ("$routine", isRoutine ? 1 : 0), ("$pos", position),
            ("$t", Clock.Stamp()), ("$dev", DeviceId));
        return id;
    }

    public void UpdateHabit(Habit h) =>
        Exec("""
             UPDATE habits SET name = $name, value_type = $type, is_routine = $routine,
                               active = $active, position = $pos,
                               updated_at = $t, device_id = $dev
             WHERE id = $id;
             """,
            null,
            ("$id", h.Id), ("$name", h.Name), ("$type", TypeKey(h.Type)),
            ("$routine", h.IsRoutine ? 1 : 0), ("$active", h.Active ? 1 : 0), ("$pos", h.Position),
            ("$t", Clock.Stamp()), ("$dev", DeviceId));

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
