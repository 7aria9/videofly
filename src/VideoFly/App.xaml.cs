using System.Windows;
using LibVLCSharp.Shared;

namespace VideoFly;

public partial class App : Application
{
    /// <summary>Instance LibVLC partagée par toutes les vignettes.</summary>
    public static LibVLC LibVlc { get; private set; } = null!;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        Core.Initialize();
        LibVlc = new LibVLC("--no-video-title-show");

        var window = new MainWindow();
        window.Show();

        // Fichiers passés en argument (ex. "Ouvrir avec…") : une vignette chacun.
        foreach (var path in e.Args)
            window.OpenFile(path);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LibVlc?.Dispose();
        base.OnExit(e);
    }
}
