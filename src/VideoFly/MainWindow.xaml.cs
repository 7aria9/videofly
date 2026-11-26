using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoFly;

public partial class MainWindow : Window
{
    private const string GlyphPlay = "";
    private const string GlyphPause = "";
    private const string GlyphVolume = "";
    private const string GlyphMute = "";

    private static readonly int[] Speeds = { 2, 4, 8, 16 };

    private enum TrickMode { None, Forward, Rewind }

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private Media? _media;

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

    // Redimensionnement automatique : demandé à l'ouverture, fait quand la vidéo est prête.
    private bool _resizePending;

    public MainWindow()
    {
        InitializeComponent();

        _libVlc = new LibVLC("--no-video-title-show");
        _player = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true };
        VideoView.MediaPlayer = _player;

        // Les évènements LibVLC arrivent sur un autre thread : on repasse sur le thread UI.
        _player.Playing += (_, _) => Dispatcher.BeginInvoke(OnPlaying);
        _player.Paused += (_, _) => Dispatcher.BeginInvoke(UpdatePlayPauseGlyph);
        _player.Stopped += (_, _) => Dispatcher.BeginInvoke(UpdatePlayPauseGlyph);
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(OnEndReached);
        _player.Vout += (_, e) => { if (e.Count > 0) Dispatcher.BeginInvoke(OnVideoOutputReady); };

        _rewindTimer.Tick += RewindTimer_Tick;
        _uiTimer.Tick += (_, _) => UpdateProgress();
        _uiTimer.Start();

        VolumeSlider.Value = _settings.Volume;

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, (_, _) => ShowOpenDialog()));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Open, Key.O, ModifierKeys.Control));
    }

    // ---------------------------------------------------------------- Ouverture

    public void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"Fichier introuvable :\n{path}", "VideoFly", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResetSpeed();

        var old = _media;
        _media = new Media(_libVlc, new Uri(path));
        _resizePending = _settings.AutoResizeWindowToVideo;
        _player.Play(_media);
        old?.Dispose();

        Title = $"{Path.GetFileName(path)} - VideoFly";
        SeekSlider.IsEnabled = true;
    }

    private void ShowOpenDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Ouvrir une vidéo",
            Filter = "Vidéos|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.flv;*.m4v;*.mpg;*.mpeg;*.ts;*.m2ts;*.3gp;*.ogv|Tous les fichiers|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            OpenFile(dialog.FileName);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            OpenFile(files[0]);
    }

    // ---------------------------------------------------------------- Évènements lecteur

    private void OnPlaying()
    {
        _player.Volume = (int)VolumeSlider.Value;
        UpdatePlayPauseGlyph();
    }

    private void OnEndReached()
    {
        ResetSpeed();
        UpdatePlayPauseGlyph();
        UpdateProgress();
    }

    private void OnVideoOutputReady()
    {
        if (!_resizePending) return;
        _resizePending = false;

        uint width = 0, height = 0;
        if (_player.Size(0, ref width, ref height) && width > 0 && height > 0)
            ResizeWindowToVideo(width, height);
    }

    /// <summary>Adapte la fenêtre pour que la zone vidéo ait la taille native de la vidéo (limitée à l'écran).</summary>
    private void ResizeWindowToVideo(uint pixelWidth, uint pixelHeight)
    {
        if (WindowState != WindowState.Normal) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double videoW = pixelWidth / dpi.DpiScaleX;
        double videoH = pixelHeight / dpi.DpiScaleY;

        // Place occupée par les bordures, la barre de titre et les commandes.
        double extraW = ActualWidth - VideoView.ActualWidth;
        double extraH = ActualHeight - VideoView.ActualHeight;

        // Limiter à la zone de travail de l'écran en gardant les proportions.
        var work = SystemParameters.WorkArea;
        double maxW = work.Width - extraW;
        double maxH = work.Height - extraH;
        double scale = Math.Min(1.0, Math.Min(maxW / videoW, maxH / videoH));
        videoW *= scale;
        videoH *= scale;

        Width = Math.Max(MinWidth, videoW + extraW);
        Height = Math.Max(MinHeight, videoH + extraH);

        // Garder la fenêtre dans l'écran.
        Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
        Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
    }

    // ---------------------------------------------------------------- Lecture / pause

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void TogglePlayPause()
    {
        if (_media == null)
        {
            ShowOpenDialog();
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

        SpeedText.Text = (mode == TrickMode.Forward ? "▶▶ x" : "◀◀ x") + speed;
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
        _settings.Volume = volume;
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
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    // ---------------------------------------------------------------- Menu

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = MenuButton.ContextMenu!;
        menu.PlacementTarget = MenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e) => ShowOpenDialog();

    private void Quit_Click(object sender, RoutedEventArgs e) => Close();

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OptionsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
            _settings.Save();
    }

    // ---------------------------------------------------------------- Clavier / fermeture

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            TogglePlayPause();
            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _settings.Save();
        _uiTimer.Stop();
        _rewindTimer.Stop();
        VideoView.MediaPlayer = null;
        _player.Stop();
        _player.Dispose();
        _media?.Dispose();
        _libVlc.Dispose();
    }
}
