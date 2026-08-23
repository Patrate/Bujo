namespace Bujo.Core;

public enum ScheduleKind
{
    /// <summary>Due tous les jours. Valeur 0, donc default(Schedule) est quotidien.</summary>
    Daily = 0,

    /// <summary>Due certains jours de la semaine, désignés par le masque Weekdays.</summary>
    Weekly = 1,

    /// <summary>Due tous les Interval jours, en phase avec Anchor.</summary>
    Interval = 2
}

/// <summary>
/// Règle d'horaire d'une habitude. Volontairement une structure sans état ni
/// dépendance : elle ne connaît ni la base, ni le jour courant, ni l'habitude à
/// laquelle elle appartient. Tout ce qu'elle sait faire, c'est répondre « ce jour-là
/// est-il dû ? », ce qui la rend vérifiable sans SQLite.
///
/// Le zéro de la structure est délibérément « quotidienne » : <c>default(Schedule)</c>
/// est donc une valeur valide et sans surprise, ce qui permet de la donner comme
/// valeur par défaut à un paramètre et évite un type nullable dans tout le code
/// appelant.
/// </summary>
public readonly record struct Schedule(ScheduleKind Kind, int Weekdays, int Interval, DateOnly? Anchor)
{
    public const int Monday    = 1;
    public const int Tuesday   = 2;
    public const int Wednesday = 4;
    public const int Thursday  = 8;
    public const int Friday    = 16;
    public const int Saturday  = 32;
    public const int Sunday    = 64;
    public const int EveryDay  = 127;

    /// <summary>
    /// Même règle, débarrassée de ses champs hors sujet. Un horaire quotidien qui
    /// traînerait un masque de jours ou un intervalle décrirait une règle qui n'existe
    /// pas : la colonne partirait telle quelle à l'export, et la synchro future
    /// transporterait un réglage mort.
    ///
    /// Sert de filet au point d'écriture, ce qui laisse l'interface libre de conserver
    /// les réglages abandonnés le temps d'une édition — basculer en hebdomadaire puis
    /// revenir à l'intervalle ne doit pas faire oublier le nombre de jours saisi.
    /// </summary>
    public Schedule Canonical() => Kind switch
    {
        ScheduleKind.Weekly => new Schedule(ScheduleKind.Weekly, Weekdays, 0, null),
        ScheduleKind.Interval => new Schedule(ScheduleKind.Interval, 0, Interval, Anchor),
        _ => Daily
    };

    public static Schedule Daily => default;

    /// <summary>
    /// Vrai quand la règle revient à « tous les jours », par quelque chemin que ce
    /// soit. Trois écritures différentes disent la même chose : le mode quotidien,
    /// un intervalle dégénéré, et les sept jours cochés. Le Suivi s'en sert pour
    /// choisir entre « N jours d'affilée » et « N fois d'affilée » — afficher
    /// « 12 fois d'affilée » à une habitude quotidienne serait une régression
    /// d'affichage par rapport à la V1.1.
    /// </summary>
    public bool IsEveryDay => Kind switch
    {
        ScheduleKind.Weekly => Weekdays == EveryDay,
        ScheduleKind.Interval => Interval < 2,
        _ => true
    };

    public static Schedule OnWeekdays(int mask) => new(ScheduleKind.Weekly, mask, 0, null);

    public static Schedule Every(int interval, DateOnly anchor) =>
        new(ScheduleKind.Interval, 0, interval, anchor);

    /// <summary>
    /// Bit du masque correspondant à un jour de la semaine. Le décalage de 6 remet
    /// lundi en tête : DayOfWeek numérote à partir de dimanche = 0, alors que la
    /// grille de régularité et l'usage français commencent le lundi.
    /// </summary>
    public static int Bit(DayOfWeek day) => 1 << (((int)day + 6) % 7);

    public bool IsDue(DateOnly day) => Kind switch
    {
        // Un masque vide n'est jamais dû. L'interface doit l'interdire à la saisie,
        // mais la synchro future peut rapatrier n'importe quoi : le cas est traité
        // ici plutôt que supposé impossible.
        ScheduleKind.Weekly => (Weekdays & Bit(day.DayOfWeek)) != 0,
        ScheduleKind.Interval => IsIntervalDue(day),
        _ => true
    };

    private bool IsIntervalDue(DateOnly day)
    {
        // Un intervalle de 0 ou 1 veut dire « tous les jours ». Le traiter ainsi
        // plutôt que lever évite surtout la division par zéro sur une ligne mal
        // formée, et retombe sur le comportement le plus prévisible.
        if (Interval < 2) return true;

        // Ancre absente : la phase est prise sur l'origine de DateOnly. Arbitraire,
        // mais déterministe, donc la grille et le taux resteront cohérents entre eux.
        // AddHabit et UpdateHabit posent toujours une ancre, ce cas ne vient que
        // d'une ligne écrite par un autre appareil.
        var delta = day.DayNumber - (Anchor?.DayNumber ?? 0);

        // Le reste suit le signe du dividende en C#, mais on ne teste que la nullité :
        // -14 % 7 vaut 0 comme 14 % 7. C'est exactement ce qu'on veut, l'ancre est une
        // phase et non un début, et les jours antérieurs à l'ancre sont dus aussi.
        return delta % Interval == 0;
    }

    /// <summary>Libellé court, pour l'interface et pour le journal d'erreurs.</summary>
    public string Describe() => Kind switch
    {
        ScheduleKind.Weekly => DescribeWeekdays(Full),
        ScheduleKind.Interval => Interval < 2 ? "tous les jours" : $"tous les {Interval} jours",
        _ => "tous les jours"
    };

    /// <summary>
    /// Même chose en abrégé, pour un bouton de largeur bornée : « lun, mer » plutôt
    /// que « lundi, mercredi », qui déborde dès trois jours cochés.
    /// </summary>
    public string DescribeShort() => Kind switch
    {
        ScheduleKind.Weekly => DescribeWeekdays(Short),
        ScheduleKind.Interval => Interval < 2 ? "tous les jours" : $"tous les {Interval} j",
        _ => "tous les jours"
    };

    private static readonly string[] Full =
        ["lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi", "dimanche"];

    private static readonly string[] Short =
        ["lun", "mar", "mer", "jeu", "ven", "sam", "dim"];

    private string DescribeWeekdays(string[] names)
    {
        if (Weekdays == 0) return "aucun jour";
        if (Weekdays == EveryDay) return "tous les jours";

        // Boucle et non LINQ : dans une STRUCT, une lambda ne peut pas accéder aux
        // membres d'instance, parce qu'elle capturerait une copie de this dont les
        // mutations seraient invisibles (CS1673). Le contournement habituel est de
        // recopier le champ dans une locale ; ici la boucle est plus courte, et elle
        // évite au passage de reposer sur la surcharge indexée de Where.
        var picked = new List<string>(7);
        for (var i = 0; i < names.Length; i++)
            if ((Weekdays & (1 << i)) != 0) picked.Add(names[i]);

        return string.Join(", ", picked);
    }
}

/// <summary>
/// La règle d'une habitude, bornée à la durée de vie de cette habitude.
///
/// Les deux bornes ne sont PAS de l'historisation d'horaire : elles ne dépendent pas
/// de la règle et ne changent pas quand on la modifie. Elles disent seulement qu'une
/// habitude n'est pas due avant d'exister ni après avoir été archivée.
///
/// Sans la borne basse, une habitude « tous les 3 jours » créée aujourd'hui naîtrait
/// avec dix occurrences manquées et un taux de 0 %. Sans la borne haute, le Suivi
/// afficherait une série morte qui s'effondre après l'archivage, alors que l'archivage
/// existe précisément pour garder un historique lisible.
/// </summary>
/// <param name="Rule">La règle elle-même.</param>
/// <param name="From">Jour logique de création de l'habitude.</param>
/// <param name="To">Jour logique d'archivage, ou null si l'habitude est toujours vivante.</param>
public sealed record HabitSchedule(Schedule Rule, DateOnly From, DateOnly? To)
{
    public bool IsDue(DateOnly day) =>
        day >= From && (To is null || day <= To.Value) && Rule.IsDue(day);

    /// <summary>
    /// Jours dus de l'intervalle demandé, par ordre croissant. C'est cette liste
    /// que Stats consomme : le calcul des séries reste ainsi une fonction pure sur
    /// des listes, sans rien connaître des horaires.
    /// </summary>
    public IReadOnlyList<DateOnly> DueDays(DateOnly from, DateOnly to)
    {
        var start = from > From ? from : From;
        var end = To is not null && To.Value < to ? To.Value : to;

        var days = new List<DateOnly>();
        for (var day = start; day <= end; day = day.AddDays(1))
            if (Rule.IsDue(day)) days.Add(day);
        return days;
    }
}
