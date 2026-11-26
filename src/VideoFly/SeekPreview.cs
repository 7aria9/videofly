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
/// <para>
/// En pause, LibVLC n'affiche pas toujours l'image après une recherche (l'aperçu restait figé) :
/// après chaque recherche on demande explicitement l'image suivante (NextFrame), et si aucune image
/// n'arrive dans les 600 ms, on relance le lecteur d'aperçu directement au bon moment.
/// </para>
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
    private long _framesAtSeek;     // images reçues au moment de la dernière recherche
    private readonly System.Diagnostics.Stopwatch _sinceSeek = new();
    private bool _awaitingFrame;    // une recherche attend encore son image
    private int _reloads;           // relances successives sans succès (on évite de boucler)
    private long _startedAtMs;      // moment de départ demandé au chargement (start-time)

    /// <summary>Nouvelle image d'aperçu (thread UI).</summary>
    public event Action<ImageSource>? SourceChanged;

    public SeekPreview(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _player = new MediaPlayer(App.LibVlc) { Mute = true };
        _renderer = new VideoFrameRenderer(_player, dispatcher, ThumbnailWidth, DispatcherPriority.Normal);
        _renderer.SourceChanged += source => SourceChanged?.Invoke(source);

        // On ne rappelle jamais LibVLC depuis ses propres évènements : on repasse par le thread UI.
        _player.Playing += (_, _) => _dispatcher.BeginInvoke(OnPlaying);
        _player.EndReached += (_, _) => _dispatcher.BeginInvoke(Unload);
        _player.EncounteredError += (_, _) => _dispatcher.BeginInvoke(Unload);

        _seekTimer.Tick += (_, _) => OnSeekTimer();
        _unloadTimer.Tick += (_, _) => Unload();
    }

    /// <summary>Demande l'image de <paramref name="path"/> à <paramref name="timeMs"/>.</summary>
    public void Request(string path, long timeMs)
    {
        if (_disposed) return;
        _unloadTimer.Stop();
        if (Math.Abs(timeMs - _pendingMs) > 500) _reloads = 0; // nouvel endroit : on peut retenter
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

    private void Load(string path, long startMs = 0)
    {
        Unload();
        _path = path;
        _startedAtMs = startMs;
        // input-fast-seek : on saute à l'image clé la plus proche, bien plus rapide (largement assez précis pour un aperçu).
        _media = new Media(App.LibVlc, MediaFiles.ToUri(path), ":no-audio", ":no-spu", ":no-sub-autodetect-file", ":input-fast-seek");
        if (startMs > 0)
            _media.AddOption(":start-time=" + (startMs / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        _player.Play(_media);
    }

    private void OnSeekTimer()
    {
        // Image toujours pas arrivée après une recherche : le lecteur est coincé, on le relance au bon moment.
        if (_ready && _awaitingFrame && _sinceSeek.ElapsedMilliseconds > 600 && _path != null)
        {
            if (_renderer.FramesDisplayed != _framesAtSeek)
            {
                _awaitingFrame = false;
                _reloads = 0;
            }
            else if (_reloads < 3)
            {
                _reloads++;
                long target = _pendingMs >= 0 ? _pendingMs : _shownMs;
                string path = _path;
                Load(path, target);
                _pendingMs = target;
                _seekTimer.Start();
                return;
            }
        }
        SeekToPending();
    }

    private void OnPlaying()
    {
        if (_disposed || _ready || _media == null) return;
        _ready = true;
        _player.SetPause(true);
        // Relancé directement au bon moment (start-time) : l'image arrive d'elle-même, pas besoin de rechercher.
        _shownMs = _startedAtMs > 0 ? _startedAtMs : -1;
        _awaitingFrame = false;
        SeekToPending();
    }

    private void SeekToPending()
    {
        if (!_ready || _pendingMs < 0 || _pendingMs == _shownMs) return;
        long length = _player.Length;
        // Pas tout à fait à la fin : le lecteur s'arrêterait.
        long target = length > 0 ? Math.Min(_pendingMs, Math.Max(0, length - 1000)) : _pendingMs;
        _framesAtSeek = _renderer.FramesDisplayed;
        _player.Time = target;
        _player.NextFrame(); // en pause : force l'affichage d'une image à la nouvelle position
        _shownMs = _pendingMs;
        _awaitingFrame = true;
        _sinceSeek.Restart();
    }

    private void Unload()
    {
        _seekTimer.Stop();
        _unloadTimer.Stop();
        _ready = false;
        _shownMs = -1;
        _awaitingFrame = false;
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
