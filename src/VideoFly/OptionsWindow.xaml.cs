using System.Windows;

namespace VideoFly;

public partial class OptionsWindow : Window
{
    private readonly AppSettings _settings;

    public OptionsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        AutoResizeCheck.IsChecked = settings.AutoResizeWindowToVideo;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoResizeWindowToVideo = AutoResizeCheck.IsChecked == true;
        DialogResult = true;
    }
}
