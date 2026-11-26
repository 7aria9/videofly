using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoFly;

/// <summary>
/// Récupère les images décodées par LibVLC et les affiche dans un <see cref="WriteableBitmap"/> WPF.
/// Contrairement à une fenêtre native (VideoView), l'image est un élément WPF normal :
/// les vignettes peuvent donc se superposer, se déplacer et se redimensionner sans problème d'affichage.
/// </summary>
public sealed class VideoFrameRenderer : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly object _sync = new();        // échanges de pointeurs (très court)
    private readonly object _bufferLock = new();  // allocation / libération des tampons vs copie vers le bitmap

    // LibVLC garde des pointeurs vers ces délégués : il faut les conserver pour éviter le ramasse-miettes.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    // Triple tampon : LibVLC écrit dans « decode », la dernière image complète attend dans « ready »,
    // et le thread UI copie « front » dans le bitmap. On échange des pointeurs au lieu de copier les images.
    private IntPtr _decodeBuffer;
    private IntPtr _readyBuffer;
    private IntPtr _frontBuffer;
    private int _width, _height, _stride, _size;
    private bool _frameReady;    // une nouvelle image attend dans _readyBuffer
    private bool _updateQueued;  // une mise à jour du bitmap est déjà en file sur le thread UI
    private bool _suspended;     // fenêtre réduite : inutile de dessiner
    private bool _disposed;
    private WriteableBitmap? _bitmap;

    /// <summary>Nouvelle source d'image (thread UI).</summary>
    public event Action<ImageSource>? SourceChanged;

    /// <summary>Taille de la vidéo en pixels, connue dès la première image (thread UI).</summary>
    public event Action<int, int>? VideoSizeChanged;

    private readonly int _maxWidth;

    /// <param name="maxWidth">Si &gt; 0, LibVLC réduit l'image à cette largeur (aperçus : moins de mémoire et de calcul).</param>
    public VideoFrameRenderer(MediaPlayer player, Dispatcher dispatcher, int maxWidth = 0)
    {
        _dispatcher = dispatcher;
        _maxWidth = maxWidth;
        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _displayCb = OnDisplay;
        player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        player.SetVideoCallbacks(_lockCb, null, _displayCb);
    }

    // ---- Threads LibVLC -----------------------------------------------------------------

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // BGRA 32 bits : le format natif de WPF (Bgr32).
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);

        int w = (int)width, h = (int)height;
        if (_maxWidth > 0 && w > _maxWidth && w > 0)
        {
            // LibVLC met l'image à l'échelle si on lui demande une autre taille.
            h = Math.Max(2, (int)Math.Round((double)h * _maxWidth / w) & ~1);
            w = _maxWidth & ~1;
            width = (uint)w;
            height = (uint)h;
        }
        int stride = w * 4;
        pitches = (uint)stride;
        lines = (uint)h;

        lock (_bufferLock)
        lock (_sync)
        {
            if (_disposed) return 0;
            FreeBuffers();
            _width = w;
            _height = h;
            _stride = stride;
            _size = stride * h;
            _decodeBuffer = Marshal.AllocHGlobal(_size);
            _readyBuffer = Marshal.AllocHGlobal(_size);
            _frontBuffer = Marshal.AllocHGlobal(_size);
            _frameReady = false;
        }

        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                SourceChanged?.Invoke(_bitmap);
            }
            VideoSizeChanged?.Invoke(w, h);
        });

        return 1; // un seul tampon d'image
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_bufferLock)
        lock (_sync) FreeBuffers();
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        // _decodeBuffer n'est modifié que par les rappels LibVLC (même thread) : pas besoin de verrou.
        Marshal.WriteIntPtr(planes, _decodeBuffer);
        return IntPtr.Zero;
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_sync)
        {
            if (_disposed || _decodeBuffer == IntPtr.Zero) return;
            // L'image décodée devient la prochaine à afficher ; on ne fait qu'échanger les pointeurs.
            (_decodeBuffer, _readyBuffer) = (_readyBuffer, _decodeBuffer);
            _frameReady = true;
            if (_suspended || _updateQueued) return; // l'UI prendra la plus récente quand elle sera libre
            _updateQueued = true;
        }
        QueueUpdate();
    }

    // ---- Thread UI ----------------------------------------------------------------------

    /// <summary>
    /// Priorité inférieure aux entrées clavier / souris : même avec plusieurs vidéos lourdes,
    /// les clics restent traités en premier (les images en trop sont simplement sautées).
    /// </summary>
    private void QueueUpdate() => _dispatcher.BeginInvoke(DispatcherPriority.Background, UpdateBitmap);

    private void UpdateBitmap()
    {
        lock (_bufferLock)
        {
            IntPtr front;
            lock (_sync)
            {
                _updateQueued = false;
                if (_disposed || _suspended || !_frameReady || _bitmap == null || _frontBuffer == IntPtr.Zero) return;
                if (_bitmap.PixelWidth != _width || _bitmap.PixelHeight != _height) return;
                (_frontBuffer, _readyBuffer) = (_readyBuffer, _frontBuffer);
                _frameReady = false;
                front = _frontBuffer;
            }
            // Copie hors du verrou d'échange : LibVLC continue de décoder sans attendre l'interface.
            // _bufferLock empêche seulement la libération des tampons (changement de format) pendant la copie.
            _bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), front, _size, _stride);
        }
    }

    /// <summary>Suspend l'affichage (fenêtre réduite) ; à la reprise, la dernière image est affichée.</summary>
    public void SetSuspended(bool suspended)
    {
        bool queue;
        lock (_sync)
        {
            _suspended = suspended;
            queue = !suspended && _frameReady && !_updateQueued;
            if (queue) _updateQueued = true;
        }
        if (queue) QueueUpdate();
    }

    private void FreeBuffers()
    {
        if (_decodeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_decodeBuffer);
        if (_readyBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_readyBuffer);
        if (_frontBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_frontBuffer);
        _decodeBuffer = _readyBuffer = _frontBuffer = IntPtr.Zero;
        _frameReady = false;
    }

    /// <summary>À appeler après l'arrêt du lecteur.</summary>
    public void Dispose()
    {
        lock (_bufferLock)
        lock (_sync)
        {
            _disposed = true;
            FreeBuffers();
        }
    }
}
