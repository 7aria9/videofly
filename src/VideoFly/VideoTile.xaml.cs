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
    internal const string GlyphPlay = "";
    internal const string GlyphPause = "";
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
    private bool _repeat = true; // relecture automatique en fin de média (activée par défaut)
    private bool _startPaused;   // session restaurée : on se place au bon moment puis on reste en pause
    private long _restoreTargetMs = -1; // moment à atteindre (reprise) tant qu'il n'est pas confirmé
    private int _restoreTicks;          // tentatives de vérification de la reprise
    private bool? _pendingMute;  // le son ne peut être coupé qu'une fois la lecture lancée   // reprise à la dernière position (historique), appliquée au premier démarrage

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

    /// <summary>Vignette placée par une disposition, en train d'être glissée vers une autre place.</summary>
    public event EventHandler? LayoutDragMoved;

    /// <summary>Vignette placée par une disposition, lâchée : la fenêtre principale l'échange avec la place visée.</summary>
    public event EventHandler? LayoutDragDropped;

    /// <summary>Demande à mettre cette vignette à la place principale de la disposition.</summary>
    public event EventHandler? MoveToMainRequested;

    /// <summary>Fichier en cours de lecture (vide si la playlist est vide).</summary>
    public string FilePath => _current?.Path ?? "";

    /// <summary>Un nouvel élément de la playlist commence (pour l'historique).</summary>
    public event Action<string>? ItemStarted;

    /// <summary>L'état lecture/pause a changé (pour le bouton global).</summary>
    public event EventHandler? PlayStateChanged;

    /// <summary>Vrai si la vignette est en train de lire.</summary>
    public bool IsPlaying => !_closed && _player.IsPlaying;

    /// <summary>Met en pause (utilisé par le bouton global). Sans effet si rien ne joue.</summary>
    public void Pause()
    {
        CancelStartPaused();
        if (!_player.IsPlaying) return;
        ResetSpeed();
        _player.SetPause(true);
    }

    /// <summary>Reprend la lecture (utilisé par le bouton global). Ne fait rien si la playlist est vide.</summary>
    public void Resume()
    {
        if (_player.IsPlaying) return;
        if (_media == null && _playlist.Items.Count == 0) return;
        TogglePlayPause();
    }

    /// <summary>Demande de plein écran (bouton, double-clic sur l'image) : la fenêtre principale s'en occupe.</summary>
    public event EventHandler? FullScreenRequested;

    /// <summary>La vignette occupe tout l'écran.</summary>
    public bool IsFullScreen { get; private set; }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => FullScreenRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Affichage plein écran : sans cadre ni coins arrondis, bouton « Quitter le plein écran ».</summary>
    public void SetFullScreen(bool fullScreen)
    {
        IsFullScreen = fullScreen;
        Frame.CornerRadius = new CornerRadius(fullScreen ? 0 : 10);
        Frame.BorderThickness = new Thickness(fullScreen ? 0 : 1);
        MaximizeButton.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        FullScreenButton.Content = fullScreen ? "\uE73F" : "\uE740";
        FullScreenButton.ToolTip = fullScreen ? "Quitter le plein écran (Échap)" : "Plein écran (F11)";
        double r = fullScreen ? 0 : 9;
        Surface.Clip = new RectangleGeometry(new Rect(0, 0, Surface.ActualWidth, Surface.ActualHeight), r, r);
    }

    /// <summary>Remet la vignette à sa taille d'avant l'agrandissement.</summary>
    public void Unmaximize() => SetMaximized(false);

    /// <summary>Fenêtre réduite : on arrête de dessiner les images (le son et la lecture continuent).</summary>
    public void SetRenderingSuspended(bool suspended) => _renderer.SetSuspended(suspended);

    /// <summary>Placée par l'organisation automatique : on ne l'adapte pas à la taille de la vidéo.</summary>
    public bool ArrangedByLayout { get; set; }

    public bool IsMaximized { get; private set; }

    /// <param name="paths">Fichiers de la playlist (peut être vide : nouvelle playlist).</param>
    public VideoTile(IEnumerable<string> paths, AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        // Décodage logiciel : les images passent de toute façon par la mémoire (rappels vidéo) pour WPF,
        // et le décodage matériel (D3D11/DXVA) y renvoie souvent des images vertes après une recherche
        // ou une reprise de lecture.
        _player = new MediaPlayer(App.LibVlc) { EnableHardwareDecoding = false };
        _renderer = new VideoFrameRenderer(_player, Dispatcher);
        _renderer.SourceChanged += source => VideoImage.Source = source;
        _renderer.VideoSizeChanged += OnVideoSizeKnown;

        // Les évènements LibVLC arrivent sur un autre thread : on repasse sur le thread UI.
        // Après la fermeture, le lecteur est libéré : on ignore les évènements encore en attente.
        _player.Playing += (_, _) => OnUi(OnPlaying);
        _player.Paused += (_, _) => OnUi(OnPausedOrStopped);
        _player.Stopped += (_, _) => OnUi(OnPausedOrStopped);
        _player.EndReached += (_, _) => OnUi(OnEndReached);
        _player.EncounteredError += (_, _) => OnUi(OnPlaybackError);

        _hideTimer.Tick += (_, _) => HideControlsIfIdle();
        _rewindTimer.Tick += RewindTimer_Tick;
        _uiTimer.Tick += (_, _) => UpdateProgress();
        _uiTimer.Start();

        VolumeSlider.Value = Math.Clamp(settings.Volume, 0, (int)VolumeSlider.Maximum);
        UpdateVolumeText();
        _autoSizePending = settings.AutoResizeWindowToVideo;

        InitSeekDrag();
        InitPlaylist(paths);
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
        // Audio ou playlist ouverte : rien à regarder derrière, on laisse les commandes.
        if (_isAudio || PlaylistPanel.Visibility == Visibility.Visible) return;
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
        double r = IsFullScreen ? 0 : 9;
        Surface.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), r, r);
    }

    /// <summary>Vignette étroite : on masque le temps et le curseur de volume pour garder les boutons lisibles.</summary>
    private void Tile_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        TimeText.Visibility = e.NewSize.Width < 430 ? Visibility.Collapsed : Visibility.Visible;
        VolumeSlider.Visibility = e.NewSize.Width < 380 ? Visibility.Collapsed : Visibility.Visible;
        VolumeText.Visibility = e.NewSize.Width < 500 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- Activation

    public void SetActive(bool active)
    {
        Frame.BorderBrush = (Brush)FindResource(active ? "AccentBrush" : "BorderBrush");
    }

    private void Tile_PreviewMouseDown(object sender, MouseButtonEventArgs e) => Activated?.Invoke(this, EventArgs.Empty);

    private void VideoHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Double-clic sur l'image : plein écran / sortie du plein écran.
        if (e.ClickCount == 2) FullScreenRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- Évènements lecteur

    private void OnPlaying()
    {
        if (_pendingMute is bool pending) _muted = pending;
        _pendingMute = null;
        // Session restaurée : on reste silencieux le temps d'atteindre le bon moment, puis on met en pause.
        _silentWhileRestoring = _startPaused;
        ApplyAudio();
        CheckRestorePosition();
        UpdatePlayPauseGlyph();
        UpdateAudioInfo();
        ShowControls(); // relance le délai avant de les effacer
    }

    private void OnPausedOrStopped()
    {
        UpdatePlayPauseGlyph();
        ShowControls();
    }

    private void OnEndReached()
    {
        // Playlist : on enchaîne sur l'élément suivant.
        if (PlayNext()) return;
        // Fin de la vidéo (ou de la playlist) : on recommence au début si la relecture est activée.
        if (_repeat && _playlist.Items.Count > 0)
        {
            PlayItem(_playlist.Items[0]);
            return;
        }
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

    /// <summary>Position et taille actuelles dans la zone principale.</summary>
    public Rect Bounds => new(Left, Top, CurrentWidth, CurrentHeight);

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
        Left += e.HorizontalChange;
        Top += e.VerticalChange;
        KeepInside(desktop);
        // Dans une disposition automatique, déplacer une vignette sert à choisir sa place (échange au lâcher).
        if (ArrangedByLayout) LayoutDragMoved?.Invoke(this, EventArgs.Empty);
    }

    private void TitleThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ArrangedByLayout && !IsMaximized) LayoutDragDropped?.Invoke(this, EventArgs.Empty);
    }

    private void MoveToMain_Click(object sender, RoutedEventArgs e) => MoveToMainRequested?.Invoke(this, EventArgs.Empty);

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

    private void ToggleMaximize()
    {
        if (!IsFullScreen) SetMaximized(!IsMaximized);
    }

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

    /// <summary>L'utilisateur prend la main pendant une restauration : on ne mettra plus en pause de nous-mêmes.</summary>
    private void CancelStartPaused()
    {
        if (!_startPaused) return;
        _startPaused = false;
        _silentWhileRestoring = false;
        ApplyAudio();
    }

    public void TogglePlayPause()
    {
        CancelStartPaused();
        if (_media == null)
        {
            // Rien en cours : on lance la playlist (ou on l'affiche si elle est vide).
            if (_playlist.Items.Count > 0) PlayItem(_playlist.Items[0]);
            else ShowPlaylistPanel(true);
            return;
        }

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
                RestartCurrentMedia();
                break;
            default:
                _player.Play();
                break;
        }
    }

    private void UpdatePlayPauseGlyph()
    {
        PlayPauseButton.Content = _player.IsPlaying ? GlyphPause : GlyphPlay;
        PlayStateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- Relecture automatique

    private void RepeatButton_Click(object sender, RoutedEventArgs e) => SetRepeat(!_repeat);

    private void SetRepeat(bool repeat)
    {
        _repeat = repeat;
        RepeatButton.Foreground = (Brush)FindResource(_repeat ? "AccentBrush" : "FaintTextBrush");
        RepeatButton.ToolTip = _repeat ? "Relecture automatique : activée" : "Relecture automatique : désactivée";
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
            RestartCurrentMedia();
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

    /// <summary>Précédent : revient au début si on a passé 3 s, sinon passe à l'élément précédent.</summary>
    private void PreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_media == null) return;
        int index = CurrentIndex;
        if (_player.Time > 3000 || index <= 0)
        {
            if (_player.State is VLCState.Ended or VLCState.Stopped) PlayItem(_current!);
            else _player.Time = 0;
            return;
        }
        PlayItem(_playlist.Items[index - 1]);
    }

    private void NextButton_Click(object sender, RoutedEventArgs e) => PlayNext();

    // ---------------------------------------------------------------- Volume

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int volume = (int)Math.Round(e.NewValue);
        if (_player != null)
        {
            if (_muted && volume > 0 && !_restoringVolume) _muted = false; // monter le volume rétablit le son
            ApplyAudio();
        }
        if (_settings != null) _settings.Volume = volume;
        UpdateVolumeText();
    }

    private void UpdateVolumeText()
    {
        if (VolumeText == null) return;
        int volume = (int)Math.Round(VolumeSlider.Value);
        // Au-delà de 100 %, le son est amplifié (il peut saturer) : on le signale en couleur d'accent.
        VolumeText.Text = $"{volume} %";
        VolumeText.Foreground = (Brush)FindResource(volume > 100 ? "AccentBrush" : "SubtleTextBrush");
    }

    private void VolumeSlider_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        AdjustVolume(e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// Molette n'importe où sur la vignette : règle son volume (la liste de la playlist, elle, défile normalement).
    /// </summary>
    private void Tile_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        AdjustVolume(e.Delta);
        ShowVolumeBadge();
        e.Handled = true;
    }

    /// <summary>±5 % par cran de molette, avec un arrêt à 100 % pour retrouver facilement le volume normal.</summary>
    private void AdjustVolume(int wheelDelta)
    {
        double current = VolumeSlider.Value;
        double next = current + (wheelDelta > 0 ? 5 : -5);
        if ((current < 100 && next > 100) || (current > 100 && next < 100)) next = 100;
        VolumeSlider.Value = Math.Clamp(next, VolumeSlider.Minimum, VolumeSlider.Maximum);
        if (_muted && wheelDelta > 0) _muted = false;
        ApplyAudio();
    }

    private readonly DispatcherTimer _volumeBadgeTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };

    private void ShowVolumeBadge()
    {
        int volume = (int)Math.Round(VolumeSlider.Value);
        bool muted = _muted || volume == 0;
        VolumeBadgeIcon.Text = muted ? GlyphMute : GlyphVolume;
        VolumeBadgeText.Text = $"{volume} %";
        VolumeBadgeText.Foreground = (Brush)FindResource(volume > 100 ? "AccentBrush" : "TextBrush");
        VolumeBadge.Visibility = Visibility.Visible;
        _volumeBadgeTimer.Stop();
        _volumeBadgeTimer.Tick -= HideVolumeBadge;
        _volumeBadgeTimer.Tick += HideVolumeBadge;
        _volumeBadgeTimer.Start();
    }

    private void HideVolumeBadge(object? sender, EventArgs e)
    {
        _volumeBadgeTimer.Stop();
        VolumeBadge.Visibility = Visibility.Collapsed;
    }

    // Son coupé : c'est NOTRE état qui fait foi, pas _player.Mute (LibVLC répond « non muet » tant qu'aucune
    // sortie audio n'est ouverte, avant la lecture ou après un arrêt : basculer à partir de là ne coupait rien).
    private bool _muted;
    private bool _silentWhileRestoring;   // restauration de session : silencieux jusqu'à la mise en pause
    private bool _restoringVolume;        // volume réglé par la restauration : ne rétablit pas le son

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        ApplyAudio();
    }

    /// <summary>Applique au lecteur l'état voulu (muet ou volume du curseur) et met à jour l'icône.</summary>
    private void ApplyAudio()
    {
        if (_closed) return;
        bool silent = _muted || _silentWhileRestoring;
        _player.Mute = silent;
        _player.Volume = silent ? 0 : (int)Math.Round(VolumeSlider.Value); // double sécurité si le muet est ignoré
        UpdateMuteGlyph();
    }

    private void UpdateMuteGlyph()
    {
        bool muted = _muted || VolumeSlider.Value <= 0;
        MuteButton.Content = muted ? GlyphMute : GlyphVolume;
        MuteButton.ToolTip = _muted ? "Rétablir le son" : "Couper le son";
    }

    // ---------------------------------------------------------------- Barre de progression

    /// <summary>
    /// Reprise au moment enregistré (session, .flyv, historique). L'option start-time ne suffit pas toujours :
    /// on vérifie la position réelle et on relance la recherche tant qu'elle n'est pas atteinte
    /// (toutes les 750 ms, 15 s au plus). Une vignette restaurée n'est mise en pause qu'une fois arrivée au bon moment.
    /// </summary>
    private void CheckRestorePosition()
    {
        if (_closed || _media == null) return;
        if (_restoreTargetMs >= 0)
        {
            var state = _player.State;
            if (state is VLCState.Opening or VLCState.Buffering or VLCState.NothingSpecial) return; // pas encore prêt
            if (state is VLCState.Ended or VLCState.Error or VLCState.Stopped)
            {
                _restoreTargetMs = -1;
            }
            else if (_player.IsSeekable && _player.Length > 0)
            {
                long target = Math.Min(_restoreTargetMs, Math.Max(0, _player.Length - 2000));
                if (Math.Abs(_player.Time - target) <= 3000)
                {
                    _restoreTargetMs = -1; // atteint
                }
                else if (_restoreTicks++ >= 60)
                {
                    _restoreTargetMs = -1; // on abandonne au bout de 15 s
                }
                else if (_restoreTicks % 3 == 1)
                {
                    _player.Time = target; // la recherche précédente n'a pas pris : on recommence
                    return;
                }
                else
                {
                    return; // on laisse le temps à la recherche d'aboutir
                }
            }
            else
            {
                if (_restoreTicks++ >= 60) _restoreTargetMs = -1;
                else return;
            }
        }

        if (_startPaused && _player.State == VLCState.Playing)
        {
            _startPaused = false;
            _player.SetPause(true);
            _silentWhileRestoring = false;
            ApplyAudio();
        }
    }

    private void UpdateProgress()
    {
        if (_restoreTargetMs >= 0 || _startPaused) CheckRestorePosition();
        if (_isSeeking) return; // pendant le glisser, le curseur et le temps suivent la souris
        long length = _player.Length;
        long time = Math.Max(0, _player.Time);
        TimeText.Text = $"{Format(time)} / {Format(Math.Max(0, length))}";

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

    // ---------------------------------------------------------------- Aperçu au survol

    private SeekPreview? _seekPreview;

    private void SeekSlider_MouseMove(object sender, MouseEventArgs e)
    {
        long length = _player.Length;
        // Aperçu au simple survol seulement : pas pendant qu'on fait glisser le curseur de position.
        bool dragging = _isSeeking || e.LeftButton == MouseButtonState.Pressed;
        if (dragging || _closed || _media == null || _current == null || length <= 0 || SeekSlider.ActualWidth <= 0)
        {
            SeekPreviewBox.Visibility = Visibility.Collapsed;
            return;
        }

        double x = Math.Clamp(e.GetPosition(SeekSlider).X, 0, SeekSlider.ActualWidth);
        long time = (long)(x / SeekSlider.ActualWidth * length);
        SeekPreviewTime.Text = Format(time);

        // Image seulement pour les vidéos (pour l'audio, le temps suffit).
        bool showImage = !_isAudio;
        SeekPreviewFrame.Visibility = showImage ? Visibility.Visible : Visibility.Collapsed;
        if (showImage)
        {
            if (_seekPreview == null)
            {
                _seekPreview = new SeekPreview(Dispatcher);
                _seekPreview.SourceChanged += source => SeekPreviewImage.Source = source;
            }
            _seekPreview.Request(_current.Path, time);
        }

        // Placé dans la vignette, centré sur le pointeur, au-dessus de la barre, sans dépasser des bords.
        SeekPreviewBox.Visibility = Visibility.Visible;
        SeekPreviewBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = SeekPreviewBox.DesiredSize;
        Point sliderTopLeft = SeekSlider.TranslatePoint(new Point(0, 0), SeekPreviewLayer);
        double layerWidth = SeekPreviewLayer.ActualWidth;
        double left = sliderTopLeft.X + x - size.Width / 2;
        left = Math.Max(4, Math.Min(left, layerWidth - size.Width - 4));
        double top = Math.Max(4, sliderTopLeft.Y - size.Height - 6);
        Canvas.SetLeft(SeekPreviewBox, left);
        Canvas.SetTop(SeekPreviewBox, top);
    }

    private void SeekSlider_MouseLeave(object sender, MouseEventArgs e)
    {
        SeekPreviewBox.Visibility = Visibility.Collapsed;
        _seekPreview?.Release();
    }

    // Glisser sur la barre de progression.
    // Le Slider WPF ne convient pas ici : avec IsMoveToPointEnabled, son propre gestionnaire marque le clic
    // comme traité (nos gestionnaires XAML ne le voyaient même pas) et il ne suit la souris que si l'on attrape
    // exactement la petite pastille, invisible au repos. On gère donc tout nous-mêmes : gestionnaires abonnés
    // même aux évènements déjà traités, capture de la souris, et position calculée depuis la piste.
    private readonly System.Diagnostics.Stopwatch _sinceDragSeek = new();

    private void InitSeekDrag()
    {
        SeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(SeekSlider_DragStart), true);
        SeekSlider.AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(SeekSlider_DragMove), true);
        SeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(SeekSlider_DragEnd), true);
        SeekSlider.AddHandler(LostMouseCaptureEvent, new MouseEventHandler(SeekSlider_LostMouseCapture), true);
    }

    private void SeekSlider_DragStart(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // ni la piste, ni la pastille, ni les boutons de la piste ne s'en mêlent
        if (_closed || _media == null) return;
        _isSeeking = true;
        SeekPreviewBox.Visibility = Visibility.Collapsed;
        Mouse.Capture(SeekSlider, CaptureMode.Element);
        DragTo(e, force: true);
    }

    private void SeekSlider_DragMove(object sender, MouseEventArgs e)
    {
        if (!_isSeeking) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndSeekDrag(); return; } // relâché hors de la fenêtre
        e.Handled = true;
        DragTo(e, force: false);
    }

    private void SeekSlider_DragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_isSeeking) return;
        e.Handled = true;
        DragTo(e, force: true);
        EndSeekDrag();
    }

    private void SeekSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // Évènement remonté depuis la pastille ou la piste : ce n'est pas notre capture.
        if (!ReferenceEquals(e.OriginalSource, SeekSlider)) return;
        _isSeeking = false;
    }

    private void EndSeekDrag()
    {
        _isSeeking = false;
        if (Mouse.Captured == SeekSlider) SeekSlider.ReleaseMouseCapture();
    }

    /// <summary>Le curseur suit la souris ; la vidéo est déplacée au plus toutes les 150 ms (et à coup sûr au relâchement).</summary>
    private void DragTo(MouseEventArgs e, bool force)
    {
        double max = SeekSlider.Maximum;
        double value;
        if (SeekSlider.Template?.FindName("PART_Track", SeekSlider) is Track track && track.ActualWidth > 0)
            value = track.ValueFromPoint(e.GetPosition(track));
        else if (SeekSlider.ActualWidth > 0)
            value = e.GetPosition(SeekSlider).X / SeekSlider.ActualWidth * max;
        else
            return;
        if (double.IsNaN(value) || double.IsInfinity(value)) return;
        value = Math.Clamp(value, 0, max);

        _updatingSeekSlider = true;
        SeekSlider.Value = value;
        _updatingSeekSlider = false;

        long length = _player.Length;
        if (length > 0)
            TimeText.Text = $"{Format((long)(value / max * length))} / {Format(length)}";

        if (!force && _sinceDragSeek.IsRunning && _sinceDragSeek.ElapsedMilliseconds < 150) return;
        _sinceDragSeek.Restart();
        if (!_player.IsSeekable) return;
        float position = (float)(value / max);
        _player.Position = position;
        if (_trickMode == TrickMode.Rewind)
            _rewindPosMs = position * length;
    }

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
        // Reprise pas encore confirmée : le moment voulu reste celui à enregistrer.
        long time = _restoreTargetMs >= 0 ? _restoreTargetMs
                  : _player.State == VLCState.Ended ? 0 : Math.Max(0, _player.Time);
        return (time, length);
    }

    // ---------------------------------------------------------------- Session (vidéos gardées à la fermeture)

    /// <summary>État à mémoriser pour rouvrir la vignette au prochain lancement.</summary>
    public SavedTile ToSaved()
    {
        var bounds = IsMaximized ? _restoreBounds : Bounds;
        return new SavedTile
        {
            Items = _playlist.Items.Select(i => i.Path).ToList(),
            CurrentIndex = Math.Max(0, CurrentIndex),
            PositionMs = GetPlaybackPosition().Position,
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = IsMaximized,
            Volume = (int)Math.Round(VolumeSlider.Value),
            Muted = _muted,
            Repeat = _repeat,
        };
    }

    /// <summary>Réglages d'une vignette restaurée, à appliquer avant <see cref="Start"/> (la lecture démarre en pause).</summary>
    public void ApplySaved(SavedTile saved)
    {
        _autoSizePending = false; // on garde la taille enregistrée
        _startPaused = true;
        _pendingMute = saved.Muted;
        _muted = saved.Muted;
        _restoringVolume = true;
        VolumeSlider.Value = Math.Clamp(saved.Volume, 0, (int)VolumeSlider.Maximum);
        _restoringVolume = false;
        SetRepeat(saved.Repeat);
    }

    /// <summary>Agrandit la vignette (une fois placée dans la zone principale).</summary>
    public void Maximize() => SetMaximized(true);

    /// <summary>Arrête la lecture et libère les ressources VLC.</summary>
    public void Shutdown()
    {
        if (_closed) return;
        _closed = true;
        _uiTimer.Stop();
        _rewindTimer.Stop();
        _hideTimer.Stop();
        _volumeBadgeTimer.Stop();
        _player.Stop();
        SeekPreviewBox.Visibility = Visibility.Collapsed;
        _seekPreview?.Dispose();
        _seekPreview = null;
        _renderer.Dispose();
        _player.Dispose();
        _media?.Dispose();
        _media = null;
    }
}
