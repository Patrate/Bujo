namespace Bujo.Core;

public sealed record HabitDay(DateOnly Day, bool Done, double? Number);

public sealed record HabitSummary(string Id, string Name, HabitValueType Type, bool Archived);

public sealed record StreakInfo(int Current, int Best, int DoneDays, int WindowDays)
{
    public double Rate => WindowDays == 0 ? 0 : (double)DoneDays / WindowDays;
}

/// <summary>
/// Calculs de suivi, volontairement séparés de la base : fonctions pures sur des
/// listes, donc vérifiables sans SQLite.
/// </summary>
public static class Stats
{
    /// <summary>
    /// Série en cours, meilleure série, et taux de réussite sur la fenêtre observée.
    ///
    /// Nuance importante : si le jour courant n'est pas encore validé, la série n'est
    /// pas considérée comme rompue — on repart de la veille. Sinon toute série
    /// afficherait zéro chaque matin avant la routine.
    /// </summary>
    public static StreakInfo Compute(IReadOnlyList<HabitDay> history, DateOnly today, int windowDays)
    {
        var done = history.Where(h => h.Done).Select(h => h.Day).ToHashSet();

        var current = 0;
        var cursor = done.Contains(today) ? today : today.AddDays(-1);
        while (done.Contains(cursor))
        {
            current++;
            cursor = cursor.AddDays(-1);
        }

        var best = 0;
        var run = 0;
        DateOnly? previous = null;
        foreach (var day in done.Order())
        {
            run = previous is not null && day == previous.Value.AddDays(1) ? run + 1 : 1;
            best = Math.Max(best, run);
            previous = day;
        }

        var from = today.AddDays(-(windowDays - 1));
        var inWindow = done.Count(d => d >= from && d <= today);

        return new StreakInfo(current, best, inWindow, windowDays);
    }

    /// <summary>Valeurs numériques non nulles, dans l'ordre chronologique.</summary>
    public static IReadOnlyList<(DateOnly Day, double Value)> NumberSeries(IReadOnlyList<HabitDay> history) =>
        history.Where(h => h.Number is not null)
               .OrderBy(h => h.Day)
               .Select(h => (h.Day, h.Number!.Value))
               .ToList();
}
