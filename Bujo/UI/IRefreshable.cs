namespace Bujo.Ui;

/// <summary>
/// Contrat des vues d'onglet. Deux gestes distincts, et la distinction est le
/// cœur du sujet : <see cref="Refresh"/> relit les données sans toucher à l'état
/// de navigation, <see cref="Activate"/> est l'entrée dans l'onglet.
///
/// Les confondre ferait sauter le Journal à aujourd'hui parce qu'on vient de
/// cocher une case en consultant un jour passé.
///
/// L'implémentation par défaut d'Activate évite d'imposer une méthode vide aux
/// vues qui n'ont rien de particulier à faire à l'entrée. Piège C# à connaître :
/// un membre d'interface par défaut n'est appelable QUE via l'interface, jamais
/// via le type concret — d'où la boucle sur IRefreshable dans MainWindow.
/// </summary>
public interface IRefreshable
{
    /// <summary>Relire les données. Ne déplace pas la navigation de la vue.</summary>
    void Refresh();

    /// <summary>Appelée quand l'onglet devient visible.</summary>
    void Activate() => Refresh();
}
