using System.Windows.Controls;
using Bujo.Core;

namespace Bujo.Lock;

/// <summary>
/// La liste des habitudes dues du jour, et la logique de complétion qui va avec.
///
/// Extrait de LockWindow en V1.4 : le verrou bloquant et l'encart non bloquant
/// affichent exactement la même routine et doivent écrire exactement la même chose.
/// Laisser ce code dans LockWindow aurait obligé l'encart à en dépendre — donc à
/// dépendre d'une fenêtre plein écran, de son WndProc et de sa géométrie Win32 —
/// ou à le dupliquer, ce qui aurait fait deux endroits à corriger au prochain
/// changement de règle.
///
/// Ce qui reste HORS de cette classe est délibéré : l'en-tête, la série et les
/// boutons d'action. Les deux écrans les habillent différemment, et rien n'y est
/// partagé qu'une poignée de TextBlock.
/// </summary>
public sealed class RoutinePanel : StackPanel
{
    private readonly JournalDb _db;
    private readonly DateOnly _day;

    /// <summary>
    /// Levé après chaque écriture, complétion comprise. C'est le signal qui permet à
    /// l'écran hôte de rafraîchir ses boutons sans rien savoir du contenu des lignes.
    /// </summary>
    public event Action? Changed;

    public RoutinePanel(JournalDb db, DateOnly day)
    {
        _db = db;
        _day = day;
        Render();
    }

    public bool IsComplete => _db.IsRoutineDone(_day);

    /// <summary>
    /// Reconstruit toutes les lignes. Coûteux en apparence, mais c'est ce qui garantit
    /// qu'une ligne barrée, une opacité ou une valeur relue en base ne peuvent pas
    /// diverger de l'état réel après une écriture.
    /// </summary>
    public void Render()
    {
        Children.Clear();
        foreach (var item in _db.GetRoutine(_day))
            Children.Add(new RoutineRow(_db, _day, item, OnRowChanged));
    }

    private void OnRowChanged()
    {
        Render();

        // Le marquage de complétion vit ici et non dans l'écran hôte : une routine
        // terminée depuis l'encart doit compter dans la série exactement comme une
        // routine terminée depuis le verrou bloquant.
        if (_db.IsRoutineDone(_day))
        {
            _db.MarkRoutineCompleted(_day);
            _db.LogLockEvent(_day, "completed");
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Série de la routine, en jours. 365 jours de recul : borne la requête sans
    /// amputer une série plausible, et les jours dus sont demandés sur la MÊME fenêtre
    /// que les jours validés, sans quoi la série serait tronquée en silence.
    /// </summary>
    public static int Streak(JournalDb db, DateOnly day)
    {
        var from = day.AddDays(-365);
        return Stats.ComputeRoutine(
            db.GetRoutineCompletedDays(from, day),
            db.GetRoutineDueDays(from, day),
            day, 30).Current;
    }

    public static string StreakLabel(int streak) =>
        streak == 1 ? "1 jour d'affilée" : $"{streak} jours d'affilée";
}
