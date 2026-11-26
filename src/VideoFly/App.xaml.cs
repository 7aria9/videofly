using System.Windows;
using LibVLCSharp.Shared;

namespace VideoFly;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        Core.Initialize();

        var window = new MainWindow();
        window.Show();

        // Permet d'ouvrir une vidéo passée en argument (ex. "Ouvrir avec…").
        if (e.Args.Length > 0)
            window.OpenFile(e.Args[0]);
    }
}
