namespace Bujo.Core;

public sealed record HabitDay(DateOnly Day, bool Done, double? Number);

public sealed record HabitSummary(string Id, string Name, HabitValueType Type, bool Archived, HabitSchedule Due);

/// <summary>
/// Résultat d'un calcul de suivi. Done et Due se comptent en OCCURRENCES DUES, pas
/// en jours de calendrier : pour une habitude « lundi et mercredi », une semaine
/// parfaite vaut 2 sur 2, jamais 2 sur 7.
/// </summary>
public sealed record StreakInfo(int Current, int Best, int Done, int Due)
{
    /// <summary>
    /// null quand aucune occurrence n'est due sur la fenêtre — habitude tous les
    /// 45 jours, ou habitude créée aujourd'hui. L'interface affiche « — », jamais
    /// 0 %, qui serait un reproche pour une exigence qui ne s'est pas encore présentée.
    /// </summary>
    public double? Rate => Due == 0 ? null : (double)Done / Due;
}

/// <summary>
/// Calculs de suivi, volontairement séparés de la base : fonctions pures sur des
/// listes, donc vérifiables sans SQLite.
///
/// Depuis la V1.2, aucune fonction d'ici ne raisonne sur le calendrier. Toutes
/// raisonnent sur une liste de JOURS DUS fournie par l'appelant, qui seul connaît
/// les horaires. C'est ce qui évite qu'une habitude « lundi et mercredi »
/// parfaitement tenue affiche 28 % de réussite.
/// </summary>
public static class Stats
{
    /// <summary>
    /// Série en cours, meilleure série, et taux de réussite sur la fenêtre observée.
    ///
    /// Deux nuances, et elles sont le cœur du sujet :
    ///
    ///   * un jour NON DÛ est transparent. Il ne rompt pas la série et ne la
    ///     prolonge pas. La prolonger afficherait « 7 jours d'affilée » à une
    ///     habitude du lundi tenue une seule fois ; la rompre rendrait toute série
    ///     structurellement incapable de dépasser 1
    ///
    ///   * le jour courant dû et non encore validé ne rompt pas la série : on repart
    ///     de l'occurrence due précédente. Sans cela, toute série afficherait zéro
    ///     chaque matin avant la routine, sur un écran affiché justement le matin
    /// </summary>
    /// <param name="history">Entrées lues en base sur la fenêtre observée.</param>
    /// <param name="dueDays">
    /// Jours dus sur la MÊME fenêtre que <paramref name="history"/>, par ordre
    /// croissant. Une fenêtre plus courte que l'historique tronquerait les séries en
    /// silence.
    /// </param>
    /// <param name="windowDays">Largeur, en jours de calendrier, de la fenêtre du taux.</param>
    public static StreakInfo Compute(
        IReadOnlyList<HabitDay> history,
        IReadOnlyList<DateOnly> dueDays,
        DateOnly today,
        int windowDays)
    {
        var done = history.Where(h => h.Done).Select(h => h.Day).ToHashSet();

        // Défensif : l'ordre croissant est un contrat, mais un appelant qui fusionne
        // les horaires de plusieurs habitudes (la routine) pourrait le rompre, et le
        // parcours de série ci-dessous n'a alors plus aucun sens.
        var due = dueDays.Where(d => d <= today).Order().ToList();

        var current = 0;
        var cursor = due.Count - 1;
        if (cursor >= 0 && due[cursor] == today && !done.Contains(today)) cursor--;
        while (cursor >= 0 && done.Contains(due[cursor]))
        {
            current++;
            cursor--;
        }

        // Meilleure série sur la fenêtre observée seulement — c'était déjà le cas en
        // V1.1, l'historique étant borné par la requête. Une occurrence due et manquée
        // remet le compteur à zéro ; un jour non dû n'apparaît pas dans la liste et ne
        // peut donc rien interrompre.
        var best = 0;
        var run = 0;
        foreach (var day in due)
        {
            if (!done.Contains(day)) { run = 0; continue; }
            run++;
            if (run > best) best = run;
        }

        var from = today.AddDays(-(windowDays - 1));
        var inWindow = due.Where(d => d >= from).ToList();
        var heldInWindow = inWindow.Count(done.Contains);

        return new StreakInfo(current, best, heldInWindow, inWindow.Count);
    }

    /// <summary>
    /// Série de la routine elle-même, à partir des jours où elle a été validée.
    ///
    /// La routine agrège des habitudes aux horaires différents : son jour dû est un
    /// jour où AU MOINS UNE habitude de routine est due. C'est pour cette raison que
    /// les jours dus arrivent ici sous forme de liste et non de règle — l'union de
    /// plusieurs horaires n'est pas un horaire.
    ///
    /// Les HabitDay synthétiques restent le moyen de réutiliser Compute plutôt que de
    /// refaire le calcul, et d'hériter donc de ses deux nuances.
    /// </summary>
    public static StreakInfo ComputeRoutine(
        IReadOnlyList<DateOnly> completedDays,
        IReadOnlyList<DateOnly> dueDays,
        DateOnly today,
        int windowDays) =>
        Compute(completedDays.Select(d => new HabitDay(d, true, null)).ToList(), dueDays, today, windowDays);

    /// <summary>
    /// Valeurs numériques non nulles, dans l'ordre chronologique.
    ///
    /// Volontairement PAS filtrée sur les jours dus : une valeur saisie est une
    /// mesure, elle reste vraie même si l'horaire a changé depuis et que ce jour-là
    /// n'est plus dû. Une courbe de progression amputée après un changement d'horaire
    /// serait un mensonge, là où un taux non filtré n'en serait qu'une flatterie.
    /// </summary>
    public static IReadOnlyList<(DateOnly Day, double Value)> NumberSeries(IReadOnlyList<HabitDay> history) =>
        history.Where(h => h.Number is not null)
               .OrderBy(h => h.Day)
               .Select(h => (h.Day, h.Number!.Value))
               .ToList();
}
