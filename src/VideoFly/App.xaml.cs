using System.Windows;
using LibVLCSharp.Shared;

namespace VideoFly;

public partial class App : Application
{
    /// <summary>Instance LibVLC partagée par toutes les vignettes.</summary>
    public static LibVLC LibVlc { get; private set; } = null!;

    private SingleInstance? _instance;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // VideoFly est déjà ouvert (ex. double-clic sur une 2e vidéo) : on lui passe les fichiers et on s'arrête.
        _instance = new SingleInstance();
        if (!_instance.IsFirst && SingleInstance.SendToFirstInstance(e.Args))
        {
            Shutdown();
            return;
        }

        Core.Initialize();
        LibVlc = new LibVLC("--no-video-title-show");

        FileAssociations.RefreshIfRegistered();

        var window = new MainWindow();
        window.Show();

        // Une fois la fenêtre mise en page (taille de la zone connue) : vidéos gardées à la dernière fermeture,
        // puis fichiers passés en argument (double-clic, "Ouvrir avec…"), une vignette chacun.
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            window.RestoreSession();
            window.OpenFiles(e.Args);
        });

        if (_instance.IsFirst)
            _instance.StartListening(files => Dispatcher.BeginInvoke(() => window.OpenFiles(files, bringToFront: true)));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        LibVlc?.Dispose();
        base.OnExit(e);
    }
}
