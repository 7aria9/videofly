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
    private readonly object _sync = new();

    // LibVLC garde des pointeurs vers ces délégués : il faut les conserver pour éviter le ramasse-miettes.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    private IntPtr _decodeBuffer;  // écrit par LibVLC
    private IntPtr _frontBuffer;   // lu par le thread UI
    private int _width, _height, _stride, _size;
    private bool _framePending;
    private bool _disposed;
    private WriteableBitmap? _bitmap;

    /// <summary>Nouvelle source d'image (thread UI).</summary>
    public event Action<ImageSource>? SourceChanged;

    /// <summary>Taille de la vidéo en pixels, connue dès la première image (thread UI).</summary>
    public event Action<int, int>? VideoSizeChanged;

    public VideoFrameRenderer(MediaPlayer player, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
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
        int stride = w * 4;
        pitches = (uint)stride;
        lines = (uint)h;

        lock (_sync)
        {
            if (_disposed) return 0;
            FreeBuffers();
            _width = w;
            _height = h;
            _stride = stride;
            _size = stride * h;
            _decodeBuffer = Marshal.AllocHGlobal(_size);
            _frontBuffer = Marshal.AllocHGlobal(_size);
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
        lock (_sync) FreeBuffers();
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _decodeBuffer);
        return IntPtr.Zero;
    }

    private unsafe void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_sync)
        {
            if (_disposed || _decodeBuffer == IntPtr.Zero) return;
            Buffer.MemoryCopy((void*)_decodeBuffer, (void*)_frontBuffer, _size, _size);
            if (_framePending) return; // l'UI n'a pas encore affiché la précédente : on la remplace
            _framePending = true;
        }
        _dispatcher.BeginInvoke(DispatcherPriority.Render, UpdateBitmap);
    }

    // ---- Thread UI ----------------------------------------------------------------------

    private void UpdateBitmap()
    {
        lock (_sync)
        {
            _framePending = false;
            if (_disposed || _bitmap == null || _frontBuffer == IntPtr.Zero) return;
            if (_bitmap.PixelWidth != _width || _bitmap.PixelHeight != _height) return;
            _bitmap.WritePixels(new Int32Rect(0, 0, _width, _height), _frontBuffer, _size, _stride);
        }
    }

    private void FreeBuffers()
    {
        if (_decodeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_decodeBuffer);
        if (_frontBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_frontBuffer);
        _decodeBuffer = _frontBuffer = IntPtr.Zero;
    }

    /// <summary>À appeler après l'arrêt du lecteur.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            FreeBuffers();
        }
    }
}
