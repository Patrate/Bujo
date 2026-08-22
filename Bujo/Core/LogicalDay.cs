namespace Bujo.Core;

/// <summary>
/// Le "jour logique" démarre à 6h du matin, heure locale. Se coucher à 2h du matin
/// ne fait donc pas basculer le jour : la routine reste celle de la veille jusqu'à 6h.
/// </summary>
public static class LogicalDay
{
    /// <summary>Heure de bascule, en heures locales.</summary>
    public static TimeSpan Cutoff { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Décalage de mise au point, en jours. Permet de faire croire à l'application
    /// qu'on est demain, sans attendre demain, pour vérifier le retour du verrou.
    ///
    /// Volontairement EN MÉMOIRE SEULE, jamais écrit dans local_state : un
    /// redémarrage le remet à zéro, il est donc impossible de l'oublier allumé
    /// pendant trois semaines et de polluer l'historique sans s'en rendre compte.
    ///
    /// Rien d'autre n'a besoin d'être prévenu : tout le monde passe par Today(),
    /// et la surveillance de bascule de LockController compare simplement le jour
    /// qu'elle affichait à celui qu'on lui répond maintenant. Elle ne demande pas
    /// pourquoi il a changé — c'est précisément ce qui rend ce test fidèle.
    /// </summary>
    public static int DebugOffsetDays { get; set; }

    public static DateOnly For(DateTimeOffset localNow) =>
        DateOnly.FromDateTime((localNow.LocalDateTime - Cutoff).Date).AddDays(DebugOffsetDays);

    public static DateOnly Today() => For(DateTimeOffset.Now);

    /// <summary>Instant local exact où le jour logique donné laissera place au suivant.</summary>
    public static DateTimeOffset EndOf(DateOnly logicalDate)
    {
        // Le décalage est retiré : l'argument est un jour logique, éventuellement
        // décalé, alors que le retour doit être un instant réel de l'horloge.
        var next = logicalDate.AddDays(1 - DebugOffsetDays).ToDateTime(TimeOnly.MinValue) + Cutoff;
        return new DateTimeOffset(next, TimeZoneInfo.Local.GetUtcOffset(next));
    }

    public static string Key(DateOnly d) => d.ToString("yyyy-MM-dd");
}

public static class Clock
{
    /// <summary>Horodatage canonique stocké en base : ISO-8601, UTC, triable en TEXT.</summary>
    public static string Stamp() => DateTimeOffset.UtcNow.ToString("O");

    public static string NewId() => Guid.CreateVersion7().ToString();
}
