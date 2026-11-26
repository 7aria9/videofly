using System.Windows;

namespace VideoFly;

public partial class OptionsWindow : Window
{
    private readonly AppSettings _settings;

    public OptionsWindow(AppSettings settings)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _settings = settings;
        AutoResizeCheck.IsChecked = settings.AutoResizeWindowToVideo;
        UpdateAssociationStatus();
    }

    private void UpdateAssociationStatus()
    {
        bool registered = FileAssociations.IsRegistered();
        UnregisterButton.IsEnabled = registered;
        AssociationStatus.Text = registered ? "VideoFly est proposé par Windows pour les fichiers vidéo." : "";
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociations.Register();
            FileAssociations.OpenDefaultAppsSettings();
            UpdateAssociationStatus();
            AssociationStatus.Text = "C'est presque fini : dans la fenêtre Windows qui vient de s'ouvrir, choisissez VideoFly " +
                                     "(« Définir par défaut », ou pour chaque type de fichier, ex. .mp4).";
        }
        catch (Exception ex)
        {
            AssociationStatus.Text = "Impossible d'enregistrer VideoFly : " + ex.Message;
        }
    }

    private void Unregister_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociations.Unregister();
            UpdateAssociationStatus();
            AssociationStatus.Text = "VideoFly n'est plus proposé pour les fichiers vidéo.";
        }
        catch (Exception ex)
        {
            AssociationStatus.Text = "Impossible de retirer VideoFly : " + ex.Message;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoResizeWindowToVideo = AutoResizeCheck.IsChecked == true;
        DialogResult = true;
    }
}
