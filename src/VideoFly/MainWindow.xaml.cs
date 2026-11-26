using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace VideoFly;

/// <summary>Fenêtre principale : contient les vignettes vidéo.</summary>
public partial class MainWindow : Window
{
    private const double DefaultTileWidth = 640;
    private const double DefaultTileHeight = 420;
    private const double CascadeStep = 30;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly List<VideoTile> _tiles = new();
    private VideoTile? _activeTile;
    private int _zOrder;
    private int _cascadeIndex;

    public MainWindow()
    {
        InitializeComponent();
        UpdateEmptyHint();

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, (_, _) => ShowOpenDialog()));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Open, Key.O, ModifierKeys.Control));
    }

    // ---------------------------------------------------------------- Ouverture

    /// <summary>Ouvre plusieurs vidéos (arguments de lancement, double-clic dans l'Explorateur).</summary>
    public void OpenFiles(IEnumerable<string> paths, bool bringToFront = false)
    {
        foreach (var path in paths)
            OpenFile(path);

        if (bringToFront)
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            // Windows n'autorise pas toujours une application à passer devant : petite astuce.
            Topmost = true;
            Topmost = false;
            Focus();
        }
    }

    /// <summary>Ouvre un fichier vidéo, audio ou une playlist (M3U) dans une nouvelle vignette.</summary>
    /// <param name="resume">Reprendre là où on s'était arrêté (ouverture depuis l'historique).</param>
    public void OpenFile(string path, Point? position = null, bool resume = false)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"Fichier introuvable :\n{path}", "VideoFly", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MediaFiles.IsPlaylist(path))
        {
            List<string> items;
            try
            {
                items = Playlist.Load(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Impossible de lire la playlist :\n{ex.Message}", "VideoFly", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            AddTile(items, position);
            return;
        }

        long resumeAt = resume ? _settings.FindInHistory(path)?.ResumePositionMs ?? 0 : 0;
        AddTile(new[] { path }, position, resumeAt);
    }

    /// <summary>Nouvelle vignette avec une playlist vide, panneau playlist ouvert.</summary>
    private void NewPlaylist_Click(object sender, RoutedEventArgs e) => AddTile(Array.Empty<string>(), null);

    private void OpenPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Ouvrir une playlist", Filter = MediaFiles.PlaylistFilter + "|Tous les fichiers|*.*" };
        if (dialog.ShowDialog(this) == true) OpenFile(dialog.FileName);
    }

    private void AddTile(IEnumerable<string> paths, Point? position, long resumeAtMs = 0)
    {
        var tile = new VideoTile(paths, _settings)
        {
            Width = Math.Min(DefaultTileWidth, Math.Max(400, Desktop.ActualWidth)),
            Height = Math.Min(DefaultTileHeight, Math.Max(240, Desktop.ActualHeight)),
        };
        tile.CloseRequested += (_, _) => CloseTile(tile);
        tile.Activated += (_, _) => ActivateTile(tile);
        tile.ManuallyArranged += (_, _) => _settings.AutoLayout = false; // l'utilisateur reprend la main
        tile.LayoutDragMoved += (_, _) => ShowDropTarget(tile);
        tile.LayoutDragDropped += (_, _) => DropTileOnSlot(tile);
        tile.MoveToMainRequested += (_, _) => MoveToMainSlot(tile);
        tile.ItemStarted += path =>
        {
            // Chaque élément lu (vidéo ou audio) rejoint l'historique.
            if (!File.Exists(path)) return;
            _settings.AddToHistory(path);
            _settings.Save();
        };

        Point p = position ?? NextCascadePosition(tile.Width, tile.Height);
        Canvas.SetLeft(tile, Math.Max(0, Math.Min(p.X, Desktop.ActualWidth - tile.Width)));
        Canvas.SetTop(tile, Math.Max(0, Math.Min(p.Y, Desktop.ActualHeight - tile.Height)));

        Desktop.Children.Add(tile);
        _tiles.Add(tile);
        ActivateTile(tile);
        UpdateEmptyHint();
        ApplyAutoLayout();
        tile.Start(0, resumeAtMs);
    }

    private Point NextCascadePosition(double width, double height)
    {
        double x = _cascadeIndex * CascadeStep, y = _cascadeIndex * CascadeStep;
        if (x + width > Desktop.ActualWidth || y + height > Desktop.ActualHeight)
        {
            _cascadeIndex = 0;
            x = y = 0;
        }
        _cascadeIndex++;
        return new Point(x, y);
    }

    private void ShowOpenDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Ouvrir",
            Multiselect = true,
            Filter = MediaFiles.OpenFilter,
        };
        if (dialog.ShowDialog(this) == true)
            foreach (var file in dialog.FileNames)
                OpenFile(file);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var drop = e.GetPosition(Desktop);
        for (int i = 0; i < files.Length; i++)
            OpenFile(files[i], new Point(drop.X + i * CascadeStep, drop.Y + i * CascadeStep));
    }

    // ---------------------------------------------------------------- Gestion des vignettes

    private void ActivateTile(VideoTile tile)
    {
        Panel.SetZIndex(tile, ++_zOrder);
        if (_activeTile == tile) return;
        _activeTile?.SetActive(false);
        _activeTile = tile;
        tile.SetActive(true);
    }

    private void RememberPosition(VideoTile tile)
    {
        if (tile.FilePath.Length == 0) return;
        var (position, length) = tile.GetPlaybackPosition();
        _settings.UpdatePosition(tile.FilePath, position, length);
    }

    private void CloseTile(VideoTile tile)
    {
        RememberPosition(tile);
        _settings.Save();
        tile.Shutdown();
        Desktop.Children.Remove(tile);
        _tiles.Remove(tile);
        if (_activeTile == tile)
        {
            _activeTile = null;
            var next = _tiles.OrderByDescending(Panel.GetZIndex).FirstOrDefault();
            if (next != null) ActivateTile(next);
        }
        if (_tiles.Count == 0) _cascadeIndex = 0;
        UpdateEmptyHint();
        ApplyAutoLayout();
    }

    private void UpdateEmptyHint()
    {
        bool empty = _tiles.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        // Sans vignette, le Canvas laisse passer les clics vers le bouton « Ouvrir une vidéo ».
        Desktop.IsHitTestVisible = !empty;
    }

    private void Desktop_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var tile in _tiles) tile.FitToDesktop();
        ApplyAutoLayout();
    }

    // ---------------------------------------------------------------- Organisation automatique

    private Rect DesktopArea => new(0, 0, Desktop.ActualWidth, Desktop.ActualHeight);

    /// <summary>Vignettes concernées par l'organisation (une vignette agrandie reste agrandie).</summary>
    private List<VideoTile> ArrangeableTiles => _tiles.Where(t => !t.IsMaximized).ToList();

    /// <summary>
    /// Applique une disposition et active l'organisation automatique.
    /// Chaque vignette va à la place la plus proche de l'endroit où elle se trouve :
    /// on peut donc déplacer les vignettes à la main avant de choisir la disposition.
    /// </summary>
    private void ApplyLayout(string id)
    {
        _settings.LayoutId = id;
        _settings.AutoLayout = true;

        var tiles = ArrangeableTiles;
        var layout = TileLayouts.Find(id, tiles.Count, DesktopArea);
        if (layout != null) SetArrangeableOrder(AssignByProximity(tiles, layout.Rects));
        ApplyAutoLayout();
    }

    /// <summary>Associe chaque vignette à une place, la plus proche d'abord (centre à centre).</summary>
    private static List<VideoTile> AssignByProximity(List<VideoTile> tiles, IReadOnlyList<Rect> slots)
    {
        static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
        var pairs = from t in tiles.Select((tile, i) => (tile, i))
                    from s in slots.Select((rect, j) => (rect, j))
                    let tc = Center(t.tile.Bounds)
                    let sc = Center(s.rect)
                    orderby (tc - sc).LengthSquared, t.i
                    select (tile: t.tile, slot: s.j);

        var result = new VideoTile?[slots.Count];
        var used = new HashSet<VideoTile>();
        foreach (var (tile, slot) in pairs)
        {
            if (result[slot] != null || used.Contains(tile)) continue;
            result[slot] = tile;
            used.Add(tile);
        }
        return result.Select(t => t!).ToList();
    }

    /// <summary>Range les vignettes non agrandies dans cet ordre (ordre des places de la disposition).</summary>
    private void SetArrangeableOrder(List<VideoTile> ordered)
    {
        var positions = _tiles.Select((t, i) => (t, i)).Where(x => !x.t.IsMaximized).Select(x => x.i).ToList();
        for (int k = 0; k < positions.Count && k < ordered.Count; k++)
            _tiles[positions[k]] = ordered[k];
    }

    private void SwapTiles(VideoTile a, VideoTile b)
    {
        int ia = _tiles.IndexOf(a), ib = _tiles.IndexOf(b);
        if (ia < 0 || ib < 0 || ia == ib) return;
        (_tiles[ia], _tiles[ib]) = (_tiles[ib], _tiles[ia]);
    }

    /// <summary>Place de la disposition actuelle sous le pointeur, ou -1.</summary>
    private int SlotUnderMouse(out Rect slotRect)
    {
        slotRect = Rect.Empty;
        var tiles = ArrangeableTiles;
        var layout = TileLayouts.Find(_settings.LayoutId, tiles.Count, DesktopArea);
        if (layout == null) return -1;
        var mouse = Mouse.GetPosition(Desktop);
        for (int i = 0; i < layout.Rects.Count; i++)
            if (layout.Rects[i].Contains(mouse))
            {
                slotRect = layout.Rects[i];
                return i;
            }
        return -1;
    }

    private Border? _dropTarget;

    /// <summary>Pendant le glissement d'une vignette, montre la place où elle ira.</summary>
    private void ShowDropTarget(VideoTile tile)
    {
        if (!_settings.AutoLayout) return;
        if (_dropTarget == null)
        {
            _dropTarget = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0x3D, 0x8B, 0xFF)),
                BorderBrush = (Brush)FindResource("AccentBrush"),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                IsHitTestVisible = false,
            };
            Panel.SetZIndex(_dropTarget, int.MaxValue);
            Desktop.Children.Add(_dropTarget);
        }

        if (SlotUnderMouse(out var rect) < 0)
        {
            _dropTarget.Visibility = Visibility.Collapsed;
            return;
        }
        _dropTarget.Visibility = Visibility.Visible;
        Canvas.SetLeft(_dropTarget, rect.X + 4);
        Canvas.SetTop(_dropTarget, rect.Y + 4);
        _dropTarget.Width = Math.Max(0, rect.Width - 8);
        _dropTarget.Height = Math.Max(0, rect.Height - 8);
    }

    /// <summary>Vignette lâchée sur une place : elle échange sa place avec la vignette qui l'occupait.</summary>
    private void DropTileOnSlot(VideoTile tile)
    {
        if (_dropTarget != null) _dropTarget.Visibility = Visibility.Collapsed;
        if (!_settings.AutoLayout) return;

        int slot = SlotUnderMouse(out _);
        var tiles = ArrangeableTiles;
        if (slot >= 0 && slot < tiles.Count) SwapTiles(tile, tiles[slot]);
        ApplyAutoLayout(); // lâchée hors d'une place : elle revient à la sienne
    }

    /// <summary>Met la vignette à la première place (la grande, dans « 1 à gauche… » ou « 1 en haut… »).</summary>
    private void MoveToMainSlot(VideoTile tile)
    {
        var tiles = ArrangeableTiles;
        if (tiles.Count == 0 || tile.IsMaximized) return;
        SwapTiles(tile, tiles[0]);
        if (!_settings.AutoLayout)
        {
            // Pas encore de disposition : on active la dernière choisie (ou « 1 à gauche, le reste à droite »).
            _settings.LayoutId ??= TileLayouts.MainLeft;
            _settings.AutoLayout = true;
        }
        ApplyAutoLayout();
    }

    /// <summary>Si l'organisation automatique est active, replace les vignettes pour remplir toute la fenêtre.</summary>
    private void ApplyAutoLayout()
    {
        if (!_settings.AutoLayout) return;
        var tiles = ArrangeableTiles;
        var layout = TileLayouts.Find(_settings.LayoutId, tiles.Count, DesktopArea);
        if (layout == null) return;
        for (int i = 0; i < tiles.Count; i++)
        {
            tiles[i].ArrangedByLayout = true;
            tiles[i].SetBounds(layout.Rects[i]);
        }
    }

    private void LayoutButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = LayoutButton.ContextMenu!;
        BuildLayoutItems(menu.Items);
        menu.PlacementTarget = LayoutButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void LayoutButton_ContextMenuOpening(object sender, ContextMenuEventArgs e) => BuildLayoutItems(LayoutButton.ContextMenu!.Items);

    private void LayoutMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource == LayoutMenu) BuildLayoutItems(LayoutMenu.Items);
    }

    /// <summary>Propose les dispositions possibles pour le nombre de vignettes ouvertes, avec un aperçu.</summary>
    private void BuildLayoutItems(ItemCollection items)
    {
        items.Clear();
        var tiles = ArrangeableTiles;
        var layouts = TileLayouts.For(tiles.Count, DesktopArea);

        if (layouts.Count == 0)
        {
            items.Add(new MenuItem { Header = "Aucune vignette ouverte", IsEnabled = false });
        }
        else
        {
            var current = _settings.AutoLayout ? TileLayouts.Find(_settings.LayoutId, tiles.Count, DesktopArea) : null;
            foreach (var layout in layouts)
            {
                var item = new MenuItem
                {
                    Header = layout.Name,
                    Icon = CreatePreview(layout, DesktopArea),
                    IsChecked = current?.Id == layout.Id,
                };
                string id = layout.Id;
                item.Click += (_, _) => ApplyLayout(id);
                items.Add(item);
            }
        }

        items.Add(new Separator());
        var auto = new MenuItem
        {
            Header = "Organisation automatique",
            IsCheckable = true,
            IsChecked = _settings.AutoLayout,
            ToolTip = "Réorganise les vignettes à chaque ouverture, fermeture ou changement de taille de la fenêtre",
        };
        auto.Click += (_, _) =>
        {
            _settings.AutoLayout = auto.IsChecked;
            ApplyAutoLayout();
        };
        items.Add(auto);

        var cascade = new MenuItem { Header = "Cascade", IsEnabled = _tiles.Count > 0 };
        cascade.Click += Cascade_Click;
        items.Add(cascade);
    }

    /// <summary>Petit dessin de la disposition, affiché à côté de son nom dans le menu.</summary>
    private static FrameworkElement CreatePreview(TileLayout layout, Rect area)
    {
        const double w = 36, h = 22;
        var canvas = new Canvas { Width = w, Height = h };
        double sx = w / area.Width, sy = h / area.Height;
        var fill = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        var stroke = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
        foreach (var r in layout.Rects)
        {
            var rect = new Rectangle
            {
                Width = Math.Max(2, r.Width * sx - 1),
                Height = Math.Max(2, r.Height * sy - 1),
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1,
            };
            Canvas.SetLeft(rect, r.X * sx);
            Canvas.SetTop(rect, r.Y * sy);
            canvas.Children.Add(rect);
        }
        return canvas;
    }

    /// <summary>Range les vignettes en cascade.</summary>
    private void Cascade_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoLayout = false;
        _cascadeIndex = 0;
        foreach (var tile in _tiles.OrderBy(Panel.GetZIndex))
        {
            var p = NextCascadePosition(DefaultTileWidth, DefaultTileHeight);
            tile.ArrangedByLayout = false;
            tile.SetBounds(new Rect(p.X, p.Y, DefaultTileWidth, DefaultTileHeight));
        }
    }

    // ---------------------------------------------------------------- Menu

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = MenuButton.ContextMenu!;
        menu.PlacementTarget = MenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e) => ShowOpenDialog();

    // ---------------------------------------------------------------- Historique

    private void HistoryMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource != HistoryMenu) return;

        // Positions à jour pour les vidéos en cours de lecture.
        foreach (var tile in _tiles) RememberPosition(tile);

        HistoryMenu.Items.Clear();
        if (_settings.History.Count == 0)
        {
            HistoryMenu.Items.Add(new MenuItem { Header = "Aucune vidéo récente", IsEnabled = false });
            return;
        }

        foreach (var entry in _settings.History)
        {
            bool exists = File.Exists(entry.Path);
            long resume = entry.ResumePositionMs;
            string detail = !exists ? "fichier introuvable"
                : resume > 0 ? $"reprise à {FormatTime(resume)}" + (entry.LengthMs > 0 ? $" / {FormatTime(entry.LengthMs)}" : "")
                : entry.LastOpened.ToString("dd/MM/yyyy HH:mm");

            var item = new MenuItem
            {
                Header = new TextBlock
                {
                    Inlines =
                    {
                        new System.Windows.Documents.Run(System.IO.Path.GetFileName(entry.Path)),
                        new System.Windows.Documents.Run("   " + detail) { Foreground = Brushes.Gray },
                    },
                },
                ToolTip = entry.Path,
                IsEnabled = exists,
            };
            string path = entry.Path;
            item.Click += (_, _) => OpenFile(path, resume: true);
            HistoryMenu.Items.Add(item);
        }

        HistoryMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Effacer l'historique" };
        clear.Click += (_, _) =>
        {
            _settings.History.Clear();
            _settings.Save();
        };
        HistoryMenu.Items.Add(clear);
    }

    private static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
    }

    private void CloseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var tile in _tiles.ToList()) CloseTile(tile);
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => Close();

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OptionsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
            _settings.Save();
    }

    // ---------------------------------------------------------------- Barre de titre

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        // Sans cadre Windows, une fenêtre agrandie déborde de l'écran de la largeur de la bordure : on compense.
        var t = SystemParameters.WindowResizeBorderThickness;
        RootBorder.Margin = maximized ? new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4) : new Thickness(0);
        RootBorder.BorderThickness = new Thickness(maximized ? 0 : 1);
        MaximizeWindowButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeWindowButton.ToolTip = maximized ? "Restaurer" : "Agrandir";
    }

    // ---------------------------------------------------------------- Clavier / fermeture

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _activeTile != null)
        {
            _activeTile.TogglePlayPause();
            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        foreach (var tile in _tiles) RememberPosition(tile);
        _settings.Save();
        foreach (var tile in _tiles.ToList()) tile.Shutdown();
        _tiles.Clear();
    }
}
