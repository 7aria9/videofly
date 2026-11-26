using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace VideoFly;

/// <summary>Partie « playlist » et « audio » de la vignette.</summary>
public partial class VideoTile
{
    private const string GlyphAudio = "";
    private const string GlyphList = "";

    private readonly Playlist _playlist = new();
    private PlaylistItem? _current;
    private bool _isAudio;

    // Glisser-déposer dans la liste
    private Point _dragStart;
    private PlaylistItem? _dragItem;

    public Playlist Playlist => _playlist;

    private int CurrentIndex => _current == null ? -1 : _playlist.Items.IndexOf(_current);

    private void InitPlaylist(IEnumerable<string> paths)
    {
        PlaylistList.ItemsSource = _playlist.Items;
        _playlist.Items.CollectionChanged += (_, _) => { UpdateTitle(); UpdateViewMode(); };
        _playlist.AddRange(paths);

        // Le panneau s'arrête au-dessus des commandes.
        BottomOverlay.SizeChanged += (_, e) =>
            PlaylistPanel.Margin = new Thickness(0, 44, 8, e.NewSize.Height);
        SizeChanged += (_, e) => PlaylistPanel.Width = Math.Max(180, Math.Min(290, e.NewSize.Width - 24));

        UpdateTitle();
        UpdateViewMode();
    }

    /// <summary>Lance la lecture (appelé par la fenêtre principale une fois la vignette branchée).</summary>
    public void Start(int index = 0, long resumeAtMs = 0)
    {
        if (_playlist.Items.Count == 0)
        {
            ShowPlaylistPanel(true);
            return;
        }
        PlayItem(_playlist.Items[Math.Clamp(index, 0, _playlist.Items.Count - 1)], resumeAtMs);
    }

    // ---------------------------------------------------------------- Lecture d'un élément

    private void PlayItem(PlaylistItem item, long resumeAtMs = 0)
    {
        ResetSpeed();
        if (_current != null) _current.IsCurrent = false;
        _current = item;
        item.IsCurrent = true;
        _isAudio = item.IsAudio;

        var old = _media;
        _media = new Media(App.LibVlc, MediaFiles.ToUri(item.Path));
        _media.MetaChanged += (_, _) => OnUi(UpdateAudioInfo);
        _resumeAtMs = resumeAtMs;

        CoverImage.Source = null;
        UpdateViewMode();
        UpdateTitle();
        _player.Play(_media);
        old?.Dispose();

        PlaylistList.ScrollIntoView(item);
        ShowControls();
        ItemStarted?.Invoke(item.Path);
    }

    /// <summary>Passe à l'élément suivant s'il existe.</summary>
    private bool PlayNext()
    {
        int index = CurrentIndex;
        if (index < 0 || index + 1 >= _playlist.Items.Count) return false;
        PlayItem(_playlist.Items[index + 1]);
        return true;
    }

    private void OnPlaybackError()
    {
        // Fichier illisible ou introuvable : on le signale et on passe au suivant.
        TitleText.Text = (_current?.Title ?? "") + "  (erreur de lecture)";
        PlayNext();
    }

    private void StopPlayback()
    {
        ResetSpeed();
        _player.Stop();
        if (_current != null) _current.IsCurrent = false;
        _current = null;
        _media?.Dispose();
        _media = null;
        _isAudio = false;
        UpdateViewMode();
        UpdateTitle();
        UpdatePlayPauseGlyph();
    }

    // ---------------------------------------------------------------- Affichage

    private void UpdateTitle()
    {
        int count = _playlist.Items.Count;
        if (_current == null)
            TitleText.Text = count == 0 ? "Nouvelle playlist" : $"Playlist · {count} éléments";
        else
            TitleText.Text = count > 1 ? $"{_current.Title}   ·   {CurrentIndex + 1}/{count}" : _current.Title;
        ToolTipService.SetToolTip(TitleThumb, _current?.Path);

        PlaylistCount.Text = count switch { 0 => "", 1 => "1 élément", _ => $"{count} éléments" };
        PlaylistEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Vidéo : l'image. Audio ou rien en cours : pochette / icône, titre et artiste.</summary>
    private void UpdateViewMode()
    {
        bool showAudioView = _current == null || _isAudio;
        AudioView.Visibility = showAudioView ? Visibility.Visible : Visibility.Collapsed;
        VideoImage.Visibility = showAudioView ? Visibility.Collapsed : Visibility.Visible;

        if (_current == null)
        {
            CoverImage.Source = null;
            AudioIcon.Text = GlyphList;
            AudioIcon.Visibility = Visibility.Visible;
            bool empty = _playlist.Items.Count == 0;
            AudioTitle.Text = empty ? "Playlist vide" : "Prêt à lire";
            AudioArtist.Text = empty ? "Ajoutez des fichiers avec + ou glissez-les dans la playlist" : "Appuyez sur lecture";
        }
        else if (_isAudio)
        {
            AudioIcon.Text = GlyphAudio;
            AudioIcon.Visibility = CoverImage.Source == null ? Visibility.Visible : Visibility.Collapsed;
            AudioTitle.Text = _current.Title;
            AudioArtist.Text = "";
        }
    }

    /// <summary>Titre, artiste, album et pochette lus dans les tags du fichier audio.</summary>
    private void UpdateAudioInfo()
    {
        if (!_isAudio || _media == null || _current == null) return;

        string? title = _media.Meta(MetadataType.Title);
        string? artist = _media.Meta(MetadataType.Artist);
        string? album = _media.Meta(MetadataType.Album);
        AudioTitle.Text = string.IsNullOrWhiteSpace(title) ? _current.Title : title;
        AudioArtist.Text = string.Join("  —  ", new[] { artist, album }.Where(x => !string.IsNullOrWhiteSpace(x)));

        string? artwork = _media.Meta(MetadataType.ArtworkURL);
        if (CoverImage.Source == null && !string.IsNullOrEmpty(artwork) && Uri.TryCreate(artwork, UriKind.Absolute, out var uri)
            && (!uri.IsFile || File.Exists(uri.LocalPath)))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.DecodePixelWidth = 360;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                CoverImage.Source = bitmap;
                AudioIcon.Visibility = Visibility.Collapsed;
            }
            catch
            {
                // Pochette illisible : on garde l'icône.
            }
        }
    }

    // ---------------------------------------------------------------- Panneau playlist

    private void PlaylistButton_Click(object sender, RoutedEventArgs e) =>
        ShowPlaylistPanel(PlaylistPanel.Visibility != Visibility.Visible);

    private void ShowPlaylistPanel(bool show)
    {
        PlaylistPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PlaylistButton.Foreground = (Brush)FindResource(show ? "AccentBrush" : "TextBrush");
        ShowControls();
    }

    private void PlaylistList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemFromElement(e.OriginalSource) is { } item) PlayItem(item);
    }

    private void PlaylistList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None)
        {
            TogglePlayPause(); // Espace reste Lecture / Pause, même dans la liste
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            RemoveSelected_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && PlaylistList.SelectedItem is PlaylistItem item)
        {
            PlayItem(item);
            e.Handled = true;
        }
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Ajouter à la playlist", Multiselect = true, Filter = MediaFiles.AddToPlaylistFilter };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            AddFiles(dialog.FileNames);
    }

    /// <summary>Ajoute des fichiers (les playlists M3U sont développées). Lance la lecture si rien ne joue.</summary>
    public void AddFiles(IEnumerable<string> paths, int insertAt = -1)
    {
        var expanded = ExpandPlaylists(paths).ToList();
        if (expanded.Count == 0) return;
        int firstIndex = insertAt < 0 || insertAt > _playlist.Items.Count ? _playlist.Items.Count : insertAt;
        _playlist.AddRange(expanded, insertAt);
        if (_current == null) PlayItem(_playlist.Items[firstIndex]);
    }

    private static IEnumerable<string> ExpandPlaylists(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (MediaFiles.IsPlaylist(path))
            {
                List<string> inner;
                try { inner = Playlist.Load(path); }
                catch { continue; }
                foreach (var p in inner) yield return p;
            }
            else
            {
                yield return path;
            }
        }
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PlaylistItem item }) RemoveItems(new[] { item });
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e) =>
        RemoveItems(PlaylistList.SelectedItems.Cast<PlaylistItem>().ToList());

    private void RemoveItems(IReadOnlyCollection<PlaylistItem> items)
    {
        if (items.Count == 0) return;
        bool removingCurrent = _current != null && items.Contains(_current);
        int currentIndex = CurrentIndex;

        foreach (var item in items) _playlist.Items.Remove(item);

        if (removingCurrent)
        {
            // L'élément en cours a été retiré : on enchaîne sur celui qui a pris sa place, s'il y en a un.
            _current = null;
            if (currentIndex >= 0 && currentIndex < _playlist.Items.Count) PlayItem(_playlist.Items[currentIndex]);
            else StopPlayback();
        }
        UpdateTitle();
    }

    private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        _playlist.Items.Clear();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        if (PlaylistList.SelectedItem is not PlaylistItem item) return;
        int from = _playlist.Items.IndexOf(item), to = from + delta;
        if (to < 0 || to >= _playlist.Items.Count) return;
        _playlist.Items.Move(from, to);
        PlaylistList.SelectedItem = item;
        PlaylistList.ScrollIntoView(item);
    }

    private void SavePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist.Items.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = "Enregistrer la playlist",
            Filter = MediaFiles.PlaylistFilter,
            DefaultExt = ".m3u8",
            FileName = "Playlist",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            _playlist.Save(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Impossible d'enregistrer la playlist :\n" + ex.Message, "VideoFly",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Charger une playlist", Filter = MediaFiles.PlaylistFilter + "|Tous les fichiers|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        List<string> paths;
        try
        {
            paths = Playlist.Load(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Impossible de lire la playlist :\n" + ex.Message, "VideoFly",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // La playlist chargée remplace la liste actuelle.
        StopPlayback();
        _playlist.Items.Clear();
        AddFiles(paths);
    }

    // ---------------------------------------------------------------- Glisser-déposer dans la liste

    private static PlaylistItem? ItemFromElement(object source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is ListBoxItem { DataContext: PlaylistItem item }) return item;
        return null;
    }

    private void PlaylistList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = ItemFromElement(e.OriginalSource);
    }

    private void PlaylistList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(PlaylistList, new DataObject(typeof(PlaylistItem), item), DragDropEffects.Move);
    }

    private void PlaylistList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(PlaylistItem)) ? DragDropEffects.Move
            : e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Lâcher un élément le déplace ; lâcher des fichiers de l'Explorateur les ajoute à cet endroit.</summary>
    private void PlaylistList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true; // la fenêtre principale n'ouvre pas de nouvelle vignette
        var target = ItemFromElement(e.OriginalSource);
        int targetIndex = target != null ? _playlist.Items.IndexOf(target) : _playlist.Items.Count;

        if (e.Data.GetData(typeof(PlaylistItem)) is PlaylistItem moved)
        {
            int from = _playlist.Items.IndexOf(moved);
            int to = Math.Min(targetIndex, _playlist.Items.Count - 1);
            if (from >= 0 && from != to) _playlist.Items.Move(from, to);
            PlaylistList.SelectedItem = moved;
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddFiles(files, targetIndex);
        }
    }
}
