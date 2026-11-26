using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoFly;

/// <summary>
/// Aperçu au survol de la barre de progression : un second lecteur LibVLC, muet et en pause,
/// qui se place au moment demandé et affiche une petite image.
/// Les demandes sont regroupées (une recherche au plus toutes les 120 ms) pour ne pas surcharger le décodage,
/// et le lecteur est libéré quelques secondes après que la souris a quitté la barre.
/// </summary>
public sealed class SeekPreview : IDisposable
{
    public const int ThumbnailWidth = 192;

    private readonly Dispatcher _dispatcher;
    private readonly MediaPlayer _player;
    private readonly VideoFrameRenderer _renderer;
    private readonly DispatcherTimer _seekTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _unloadTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private Media? _media;
    private string? _path;
    private bool _ready;      // lecteur démarré puis mis en pause : il accepte les recherches
    private long _pendingMs = -1;
    private long _shownMs = -1;
    private bool _disposed;

    /// <summary>Nouvelle image d'aperçu (thread UI).</summary>
    public event Action<ImageSource>? SourceChanged;

    public SeekPreview(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _player = new MediaPlayer(App.LibVlc) { Mute = true };
        _renderer = new VideoFrameRenderer(_player, dispatcher, ThumbnailWidth);
        _renderer.SourceChanged += source => SourceChanged?.Invoke(source);

        // On ne rappelle jamais LibVLC depuis ses propres évènements : on repasse par le thread UI.
        _player.Playing += (_, _) => _dispatcher.BeginInvoke(OnPlaying);
        _player.EndReached += (_, _) => _dispatcher.BeginInvoke(Unload);
        _player.EncounteredError += (_, _) => _dispatcher.BeginInvoke(Unload);

        _seekTimer.Tick += (_, _) => SeekToPending();
        _unloadTimer.Tick += (_, _) => Unload();
    }

    /// <summary>Demande l'image de <paramref name="path"/> à <paramref name="timeMs"/>.</summary>
    public void Request(string path, long timeMs)
    {
        if (_disposed) return;
        _unloadTimer.Stop();
        if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) Load(path);
        _pendingMs = Math.Max(0, timeMs);
        if (!_seekTimer.IsEnabled)
        {
            SeekToPending(); // première demande : tout de suite
            _seekTimer.Start();
        }
    }

    /// <summary>La souris a quitté la barre : on libère le lecteur un peu plus tard.</summary>
    public void Release()
    {
        if (_disposed) return;
        _seekTimer.Stop();
        _unloadTimer.Stop();
        _unloadTimer.Start();
    }

    private void Load(string path)
    {
        Unload();
        _path = path;
        _media = new Media(App.LibVlc, MediaFiles.ToUri(path), ":no-audio", ":no-spu", ":no-sub-autodetect-file");
        _player.Play(_media);
    }

    private void OnPlaying()
    {
        if (_disposed || _ready || _media == null) return;
        _ready = true;
        _player.SetPause(true);
        _shownMs = -1;
        SeekToPending();
    }

    private void SeekToPending()
    {
        if (!_ready || _pendingMs < 0 || _pendingMs == _shownMs) return;
        long length = _player.Length;
        // Pas tout à fait à la fin : le lecteur s'arrêterait.
        long target = length > 0 ? Math.Min(_pendingMs, Math.Max(0, length - 1000)) : _pendingMs;
        _player.Time = target;
        _shownMs = _pendingMs;
    }

    private void Unload()
    {
        _seekTimer.Stop();
        _unloadTimer.Stop();
        _ready = false;
        _shownMs = -1;
        _path = null;
        if (_media == null) return;
        _player.Stop();
        _media.Dispose();
        _media = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Unload();
        _disposed = true;
        _renderer.Dispose();
        _player.Dispose();
    }
}
