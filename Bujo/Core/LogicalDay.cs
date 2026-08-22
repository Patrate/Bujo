namespace Bujo.Core;

/// <summary>
/// Le "jour logique" démarre à 6h du matin, heure locale. Se coucher à 2h du matin
/// ne fait donc pas basculer le jour : la routine reste celle de la veille jusqu'à 6h.
/// </summary>
public static class LogicalDay
{
    /// <summary>Heure de bascule, en heures locales.</summary>
    public static TimeSpan Cutoff { get; set; } = TimeSpan.FromHours(6);

    public static DateOnly For(DateTimeOffset localNow) =>
        DateOnly.FromDateTime((localNow.LocalDateTime - Cutoff).Date);

    public static DateOnly Today() => For(DateTimeOffset.Now);

    /// <summary>Instant local exact où le jour logique donné laissera place au suivant.</summary>
    public static DateTimeOffset EndOf(DateOnly logicalDate)
    {
        var next = logicalDate.AddDays(1).ToDateTime(TimeOnly.MinValue) + Cutoff;
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
