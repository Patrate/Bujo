using System.Windows;
using System.Windows.Markup;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;

namespace Bujo.Ui;

/// <summary>
/// Habillage global. Un seul sujet pour l'instant : le gabarit des boutons.
///
/// POURQUOI un gabarit complet et non trois Setter. Le gabarit livré avec Windows
/// (Aero2) peint ses états sur le Border INTERNE de son template, par des Setter à
/// TargetName. Ceux-ci l'emportent sur le TemplateBinding, donc sur toute couleur
/// posée de l'extérieur :
///   * survol   -> bleu pâle #BEE6FD, sur une application entièrement sombre
///   * enfoncé  -> bleu plus soutenu
///   * désactivé-> fond gris clair #F4F4F4, avec du texte blanc par-dessus
/// Aucune propriété posée sur le Button ne peut les contredire. Il faut remplacer le
/// template, ou renoncer à désactiver le moindre bouton.
///
/// PIÈGE MAJEUR, découvert en écrivant ceci : un Style implicite REMPLACE le style de
/// thème, il ne s'y ajoute pas. Tout ce que le thème posait et qu'on ne repose pas ici
/// retombe à la valeur par défaut de la propriété de dépendance — à commencer par
/// BorderThickness, qui vaut alors 0 et ferait disparaître la bordure de TOUS les
/// boutons de l'application, aucun ne la fixant localement. D'où la liste de Setter
/// ci-dessous, qui n'est pas décorative.
///
/// XAML plutôt que FrameworkElementFactory : cette dernière est déconseillée par
/// Microsoft et rendrait ce template illisible sur trois écrans. Le fragment ci-dessous
/// est court, et une faute s'y verrait au tout premier lancement.
/// </summary>
public static class Theme
{
    private const string ButtonXaml = """
        <Style TargetType="Button"
               xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

          <!-- Ces Setter remplacent ceux du thème, qui n'est plus consulté du tout. -->
          <Setter Property="Background" Value="Transparent"/>
          <Setter Property="Foreground" Value="White"/>
          <Setter Property="BorderBrush" Value="#444850"/>
          <Setter Property="BorderThickness" Value="1"/>
          <Setter Property="Padding" Value="1"/>
          <Setter Property="HorizontalContentAlignment" Value="Center"/>
          <Setter Property="VerticalContentAlignment" Value="Center"/>
          <Setter Property="SnapsToDevicePixels" Value="True"/>

          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Button">
                <!-- Deux Border imbriqués : le voile porte les états de survol, ce qui
                     laisse intact le fond propre du bouton. Peindre directement sur le
                     Border extérieur écraserait un fond posé par l'appelant. -->
                <Border x:Name="bd"
                        Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}"
                        SnapsToDevicePixels="True">
                  <Border x:Name="veil" Background="Transparent">
                    <ContentPresenter Margin="{TemplateBinding Padding}"
                                      HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                      VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                      RecognizesAccessKey="True"
                                      SnapsToDevicePixels="{TemplateBinding SnapsToDevicePixels}"/>
                  </Border>
                </Border>

                <ControlTemplate.Triggers>
                  <!-- Voiles blancs très faibles : sur fond sombre, quelques pour cent
                       suffisent à signaler le survol sans éclairer la fenêtre. -->
                  <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="veil" Property="Background" Value="#18FFFFFF"/>
                  </Trigger>
                  <Trigger Property="IsPressed" Value="True">
                    <Setter TargetName="veil" Property="Background" Value="#30FFFFFF"/>
                  </Trigger>

                  <!-- Un bouton désactivé sur thème sombre doit S'EFFACER, pas
                       s'éclaircir. L'opacité fane d'un coup le fond, la bordure et le
                       texte, en gardant leurs teintes relatives. -->
                  <Trigger Property="IsEnabled" Value="False">
                    <Setter TargetName="bd" Property="Opacity" Value="0.35"/>
                  </Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """;

    /// <summary>
    /// À appeler une fois, sur l'Application, AVANT la création de la moindre fenêtre.
    /// La clé typeof(Button) est ce qui rend le style implicite : il s'applique alors
    /// à tout bouton de l'application sans qu'aucun appelant ait à le demander.
    ///
    /// Ne touche que Button. ToggleButton, RepeatButton et CheckBox en dérivent tous
    /// de ButtonBase mais pas de Button : les bascules de l'éditeur d'horaire et les
    /// flèches des ascenseurs gardent donc leur gabarit, ce qui est voulu — elles sont
    /// déjà habillées à la main là où c'était nécessaire.
    /// </summary>
    public static void Apply(Application app) =>
        app.Resources[typeof(Button)] = (Style)XamlReader.Parse(ButtonXaml);
}
