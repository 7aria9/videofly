using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoFly;

/// <summary>
/// Une "vignette" : fenêtre interne qui contient une vidéo et ses propres commandes.
/// Elle se place dans le Canvas de la fenêtre principale, se déplace et se redimensionne.
/// </summary>
public partial class VideoTile : UserControl
{
    private const string GlyphPlay = "";
    private const string GlyphPause = "";
    private const string GlyphVolume = "";
    private const string GlyphMute = "";
    private const string GlyphMaximize = "";
    private const string GlyphRestore = "";

    private static readonly int[] Speeds = { 2, 4, 8, 16 };

    private enum TrickMode { None, Forward, Rewind }

    private readonly AppSettings _settings;
    private readonly MediaPlayer _player;
    private readonly VideoFrameRenderer _renderer;
    private Media? _media;
    private bool _closed;
    private long _resumeAtMs;   // reprise à la dernière position (historique), appliquée au premier démarrage

    // Avance / retour rapide
    private TrickMode _trickMode = TrickMode.None;
    private int _speedIndex = -1;
    private readonly DispatcherTimer _rewindTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Stopwatch _rewindClock = new();
    private double _rewindPosMs;

    // Barre de progression
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _isSeeking;
    private bool _updatingSeekSlider;

    // Commandes en surimpression : elles s'effacent après quelques secondes sans bouger la souris pendant la lecture.
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _controlsVisible = true;

    // Taille / position
    private bool _autoSizePending;
    private Rect _restoreBounds;

    /// <summary>La vignette demande à être fermée.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>La vignette a été cliquée : elle devient la vignette active.</summary>
    public event EventHandler? Activated;

    /// <summary>L'utilisateur a déplacé ou redimensionné la vignette à la main.</summary>
    public event EventHandler? ManuallyArranged;

    public string FilePath { get; }

    /// <summary>Placée par l'organisation automatique : on ne l'adapte pas à la taille de la vidéo.</summary>
    public bool ArrangedByLayout { get; set; }

    public bool IsMaximized { get; private set; }

    public VideoTile(string path, AppSettings settings, long resumeAtMs = 0)
    {
        InitializeComponent();
        _settings = settings;
        FilePath = path;
        TitleText.Text = Path.GetFileName(path);
        ToolTipService.SetToolTip(TitleThumb, path);

        _player = new MediaPlayer(App.LibVlc) { EnableHardwareDecoding = true };
        _renderer = new VideoFrameRenderer(_player, Dispatcher);
        _renderer.SourceChanged += source => VideoImage.Source = source;
        _renderer.VideoSizeChanged += OnVideoSizeKnown;

        // Les évènements LibVLC arrivent sur un autre thread : on repasse sur le thread UI.
        // Après la fermeture, le lecteur est libéré : on ignore les évènements encore en attente.
        _player.Playing += (_, _) => OnUi(OnPlaying);
        _player.Paused += (_, _) => OnUi(OnPausedOrStopped);
        _player.Stopped += (_, _) => OnUi(OnPausedOrStopped);
        _player.EndReached += (_, _) => OnUi(OnEndReached);
        _player.EncounteredError += (_, _) => OnUi(() => TitleText.Text = $"{Path.GetFileName(path)} (erreur de lecture)");

        _hideTimer.Tick += (_, _) => HideControlsIfIdle();
        _rewindTimer.Tick += RewindTimer_Tick;
        _uiTimer.Tick += (_, _) => UpdateProgress();
        _uiTimer.Start();

        VolumeSlider.Value = settings.Volume;

        _autoSizePending = settings.AutoResizeWindowToVideo;
        _resumeAtMs = resumeAtMs;
        _media = new Media(App.LibVlc, new Uri(path));
        _player.Play(_media);
    }

    private void OnUi(Action action) => Dispatcher.BeginInvoke(() => { if (!_closed) action(); });

    // ---------------------------------------------------------------- Commandes en surimpression

    private void Tile_MouseMove(object sender, MouseEventArgs e) => ShowControls();

    private void Tile_MouseLeave(object sender, MouseEventArgs e)
    {
        // La souris quitte la vignette : on efface rapidement les commandes si la vidéo joue.
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(0.6);
        _hideTimer.Start();
    }

    private void ShowControls()
    {
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(2.5);
        if (!_controlsVisible)
        {
            _controlsVisible = true;
            FadeOverlays(1, 150);
            VideoHost.Cursor = null;
        }
        if (!_closed && _player.IsPlaying) _hideTimer.Start();
    }

    private void HideControlsIfIdle()
    {
        _hideTimer.Stop();
        if (_closed || !_controlsVisible || !_player.IsPlaying || _isSeeking) return;
        // On ne cache pas sous le pointeur de l'utilisateur (sauf s'il a quitté la vignette).
        if (IsMouseOver && (BottomOverlay.IsMouseOver || TopOverlay.IsMouseOver)) return;
        _controlsVisible = false;
        FadeOverlays(0, 350);
        if (IsMouseOver) VideoHost.Cursor = Cursors.None;
    }

    private void FadeOverlays(double to, int ms)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
        TopOverlay.BeginAnimation(OpacityProperty, animation);
        BottomOverlay.BeginAnimation(OpacityProperty, animation);
        TopOverlay.IsHitTestVisible = BottomOverlay.IsHitTestVisible = to > 0;
    }

    /// <summary>Coins arrondis : on découpe le contenu à la forme du cadre.</summary>
    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Surface.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 9, 9);
    }

    /// <summary>Vignette étroite : on masque le temps et le curseur de volume pour garder les boutons lisibles.</summary>
    private void Tile_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        TimeText.Visibility = e.NewSize.Width < 430 ? Visibility.Collapsed : Visibility.Visible;
        VolumeSlider.Visibility = e.NewSize.Width < 360 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- Activation

    public void SetActive(bool active)
    {
        Frame.BorderBrush = (Brush)FindResource(active ? "AccentBrush" : "BorderBrush");
    }

    private void Tile_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Activated?.Invoke(this, EventArgs.Empty);

    private void VideoHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Double-clic sur l'image : agrandir / restaurer.
        if (e.ClickCount == 2) ToggleMaximize();
    }

    // ---------------------------------------------------------------- Évènements lecteur

    private void OnPlaying()
    {
        if (_resumeAtMs > 0)
        {
            _player.Time = _resumeAtMs;
            _resumeAtMs = 0;
        }
        _player.Volume = (int)VolumeSlider.Value;
        UpdatePlayPauseGlyph();
        ShowControls(); // relance le délai avant de les effacer
    }

    private void OnPausedOrStopped()
    {
        UpdatePlayPauseGlyph();
        ShowControls();
    }

    private void OnEndReached()
    {
        ShowControls();
        ResetSpeed();
        UpdatePlayPauseGlyph();
        UpdateProgress();
    }

    /// <summary>À l'ouverture : adapter la vignette à la taille native de la vidéo (limitée à la zone disponible).</summary>
    private void OnVideoSizeKnown(int pixelWidth, int pixelHeight)
    {
        if (!_autoSizePending || Parent is not Canvas desktop) return;
        _autoSizePending = false;
        if (IsMaximized || ArrangedByLayout) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double videoW = pixelWidth / dpi.DpiScaleX;
        double videoH = pixelHeight / dpi.DpiScaleY;

        // Place occupée par la barre de titre, les commandes et les bordures.
        double extraW = ActualWidth - VideoHost.ActualWidth;
        double extraH = ActualHeight - VideoHost.ActualHeight;

        double maxW = desktop.ActualWidth - extraW;
        double maxH = desktop.ActualHeight - extraH;
        double scale = Math.Min(1.0, Math.Min(maxW / videoW, maxH / videoH));
        if (scale <= 0) return;

        Width = Math.Max(MinWidth, videoW * scale + extraW);
        Height = Math.Max(MinHeight, videoH * scale + extraH);
        KeepInside(desktop);
    }

    // ---------------------------------------------------------------- Déplacement / redimensionnement

    private double Left
    {
        get => double.IsNaN(Canvas.GetLeft(this)) ? 0 : Canvas.GetLeft(this);
        set => Canvas.SetLeft(this, value);
    }

    private double Top
    {
        get => double.IsNaN(Canvas.GetTop(this)) ? 0 : Canvas.GetTop(this);
        set => Canvas.SetTop(this, value);
    }

    private double CurrentWidth => double.IsNaN(Width) ? ActualWidth : Width;
    private double CurrentHeight => double.IsNaN(Height) ? ActualHeight : Height;

    public void SetBounds(Rect bounds)
    {
        if (IsMaximized) SetMaximized(false);
        Left = bounds.X;
        Top = bounds.Y;
        Width = Math.Max(MinWidth, bounds.Width);
        Height = Math.Max(MinHeight, bounds.Height);
    }

    private void TitleThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (IsMaximized || Parent is not Canvas desktop) return;
        OnManuallyArranged();
        Left += e.HorizontalChange;
        Top += e.VerticalChange;
        KeepInside(desktop);
    }

    private void TitleThumb_DoubleClick(object sender, MouseButtonEventArgs e) => ToggleMaximize();

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (IsMaximized || sender is not Thumb { Tag: string edges }) return;
        OnManuallyArranged();

        double left = Left, top = Top, width = CurrentWidth, height = CurrentHeight;

        if (edges.Contains('R'))
            width = Math.Max(MinWidth, width + e.HorizontalChange);
        if (edges.Contains('B'))
            height = Math.Max(MinHeight, height + e.VerticalChange);
        if (edges.Contains('L'))
        {
            double newWidth = Math.Max(MinWidth, width - e.HorizontalChange);
            left += width - newWidth;
            width = newWidth;
        }
        if (edges.Contains('T'))
        {
            double newHeight = Math.Max(MinHeight, height - e.VerticalChange);
            top += height - newHeight;
            height = newHeight;
        }

        Left = Math.Max(0, left);
        Top = Math.Max(0, top);
        Width = width;
        Height = height;
    }

    private void OnManuallyArranged()
    {
        if (!ArrangedByLayout) return;
        ArrangedByLayout = false;
        ManuallyArranged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Garde au moins la barre de titre visible dans la zone principale.</summary>
    private void KeepInside(Canvas desktop)
    {
        double maxLeft = Math.Max(0, desktop.ActualWidth - 80);
        double maxTop = Math.Max(0, desktop.ActualHeight - 28);
        Left = Math.Clamp(Left, Math.Min(0, -(CurrentWidth - 80)), maxLeft);
        Top = Math.Clamp(Top, 0, maxTop);
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() => SetMaximized(!IsMaximized);

    private void SetMaximized(bool maximized)
    {
        if (maximized == IsMaximized) return;
        if (maximized)
        {
            _restoreBounds = new Rect(Left, Top, CurrentWidth, CurrentHeight);
            IsMaximized = true;
            FitToDesktop();
        }
        else
        {
            IsMaximized = false;
            Left = _restoreBounds.X;
            Top = _restoreBounds.Y;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
        }
        MaximizeButton.Content = IsMaximized ? GlyphRestore : GlyphMaximize;
        MaximizeButton.ToolTip = IsMaximized ? "Restaurer" : "Agrandir";
    }

    /// <summary>Une vignette agrandie occupe toute la zone principale (appelé aussi quand la fenêtre change de taille).</summary>
    public void FitToDesktop()
    {
        if (!IsMaximized || Parent is not Canvas desktop) return;
        Left = 0;
        Top = 0;
        Width = Math.Max(MinWidth, desktop.ActualWidth);
        Height = Math.Max(MinHeight, desktop.ActualHeight);
    }

    // ---------------------------------------------------------------- Lecture / pause

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    public void TogglePlayPause()
    {
        if (_media == null) return;

        // En avance/retour rapide, "Lecture" revient à la vitesse normale.
        if (_trickMode != TrickMode.None)
        {
            ResetSpeed();
            if (!_player.IsPlaying) _player.Play();
            return;
        }

        switch (_player.State)
        {
            case VLCState.Playing:
                _player.Pause();
                break;
            case VLCState.Ended:
            case VLCState.Stopped:
            case VLCState.Error:
                _player.Stop();
                _player.Play(_media);
                break;
            default:
                _player.Play();
                break;
        }
    }

    private void UpdatePlayPauseGlyph()
    {
        PlayPauseButton.Content = _player.IsPlaying ? GlyphPause : GlyphPlay;
    }

    // ---------------------------------------------------------------- Avance / retour rapide

    private void ForwardButton_Click(object sender, RoutedEventArgs e) => CycleSpeed(TrickMode.Forward);

    private void RewindButton_Click(object sender, RoutedEventArgs e) => CycleSpeed(TrickMode.Rewind);

    /// <summary>Chaque clic passe à la vitesse suivante : x2, x4, x8, x16, puis retour à la normale.</summary>
    private void CycleSpeed(TrickMode mode)
    {
        if (_media == null) return;

        if (_trickMode != mode)
        {
            ResetSpeed();
            _trickMode = mode;
            _speedIndex = 0;
        }
        else if (++_speedIndex >= Speeds.Length)
        {
            ResetSpeed();
            return;
        }

        if (_player.State is VLCState.Ended or VLCState.Stopped)
        {
            _player.Stop();
            _player.Play(_media);
        }
        else if (!_player.IsPlaying)
        {
            _player.Play();
        }

        int speed = Speeds[_speedIndex];
        if (mode == TrickMode.Forward)
        {
            _player.SetRate(speed);
        }
        else
        {
            // LibVLC ne sait pas lire à l'envers : on recule par petits sauts réguliers.
            _player.SetRate(1f);
            if (!_rewindTimer.IsEnabled)
            {
                _rewindPosMs = Math.Max(0, _player.Time);
                _rewindClock.Restart();
                _rewindTimer.Start();
            }
        }

        SpeedText.Text = (mode == TrickMode.Forward ? "▶▶  x" : "◀◀  x") + speed;
        SpeedBadge.Visibility = Visibility.Visible;
    }

    private void RewindTimer_Tick(object? sender, EventArgs e)
    {
        double elapsed = _rewindClock.Elapsed.TotalMilliseconds;
        _rewindClock.Restart();

        _rewindPosMs -= Speeds[_speedIndex] * elapsed;
        if (_rewindPosMs <= 0)
        {
            _player.Time = 0;
            ResetSpeed();
            return;
        }
        _player.Time = (long)_rewindPosMs;
    }

    private void ResetSpeed()
    {
        _rewindTimer.Stop();
        _rewindClock.Reset();
        _trickMode = TrickMode.None;
        _speedIndex = -1;
        if (_player.Rate != 1f) _player.SetRate(1f);
        SpeedText.Text = string.Empty;
        SpeedBadge.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- Précédent / suivant

    private void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        // Prévu pour plus tard (liste de lecture).
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        // Prévu pour plus tard (liste de lecture).
    }

    // ---------------------------------------------------------------- Volume

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int volume = (int)Math.Round(e.NewValue);
        if (_player != null)
        {
            _player.Volume = volume;
            if (_player.Mute && volume > 0) _player.Mute = false;
            UpdateMuteGlyph();
        }
        if (_settings != null) _settings.Volume = volume;
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _player.Mute = !_player.Mute;
        UpdateMuteGlyph();
    }

    private void UpdateMuteGlyph()
    {
        bool muted = _player.Mute || VolumeSlider.Value <= 0;
        MuteButton.Content = muted ? GlyphMute : GlyphVolume;
        MuteButton.ToolTip = _player.Mute ? "Rétablir le son" : "Couper le son";
    }

    // ---------------------------------------------------------------- Barre de progression

    private void UpdateProgress()
    {
        long length = _player.Length;
        long time = Math.Max(0, _player.Time);
        TimeText.Text = $"{Format(time)} / {Format(Math.Max(0, length))}";

        if (_isSeeking) return;
        _updatingSeekSlider = true;
        SeekSlider.Value = length > 0 ? _player.Position * SeekSlider.Maximum : 0;
        _updatingSeekSlider = false;
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSeekSlider || _player == null || !_player.IsSeekable) return;
        float position = (float)(e.NewValue / SeekSlider.Maximum);
        _player.Position = position;
        if (_trickMode == TrickMode.Rewind)
            _rewindPosMs = position * _player.Length;
    }

    private void SeekSlider_DragStart(object sender, MouseButtonEventArgs e) => _isSeeking = true;

    private void SeekSlider_DragEnd(object sender, MouseButtonEventArgs e) => _isSeeking = false;

    private static string Format(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    // ---------------------------------------------------------------- Fermeture

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Position actuelle et durée (ms). Position 0 si la vidéo est finie.</summary>
    public (long Position, long Length) GetPlaybackPosition()
    {
        if (_closed) return (0, 0);
        long length = _player.Length;
        long time = _player.State == VLCState.Ended ? 0 : Math.Max(0, _player.Time);
        return (time, length);
    }

    /// <summary>Arrête la lecture et libère les ressources VLC.</summary>
    public void Shutdown()
    {
        if (_closed) return;
        _closed = true;
        _uiTimer.Stop();
        _rewindTimer.Stop();
        _hideTimer.Stop();
        _player.Stop();
        _renderer.Dispose();
        _player.Dispose();
        _media?.Dispose();
        _media = null;
    }
}
