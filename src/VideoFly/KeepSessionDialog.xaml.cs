using System.Windows;

namespace VideoFly;

/// <summary>À la fermeture : garder les vidéos ouvertes pour les retrouver au prochain lancement ?</summary>
public partial class KeepSessionDialog : Window
{
    /// <summary>L'utilisateur veut retrouver ses vidéos.</summary>
    public bool Keep { get; private set; }

    /// <summary>Appliquer ce choix à chaque fermeture sans redemander.</summary>
    public bool Remember => RememberCheck.IsChecked == true;

    public KeepSessionDialog(int count)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        MessageText.Text = (count == 1 ? "Une vidéo est ouverte." : $"{count} vidéos sont ouvertes.") +
                           " Au prochain lancement, VideoFly peut les rouvrir à la même place et au même moment, en pause.";
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        Keep = true;
        DialogResult = true;
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        Keep = false;
        DialogResult = true;
    }
}
