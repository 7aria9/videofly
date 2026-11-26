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

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, (_, _) => ShowOpenDialog()));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Open, Key.O, ModifierKeys.Control));
    }

    // ---------------------------------------------------------------- Ouverture

    /// <summary>Ouvre une vidéo dans une nouvelle vignette.</summary>
    /// <param name="resume">Reprendre là où on s'était arrêté (ouverture depuis l'historique).</param>
    public void OpenFile(string path, Point? position = null, bool resume = false)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"Fichier introuvable :\n{path}", "VideoFly", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var history = _settings.AddToHistory(path);
        _settings.Save();

        var tile = new VideoTile(path, _settings, resume ? history.ResumePositionMs : 0)
        {
            Width = Math.Min(DefaultTileWidth, Math.Max(400, Desktop.ActualWidth)),
            Height = Math.Min(DefaultTileHeight, Math.Max(240, Desktop.ActualHeight)),
        };
        tile.CloseRequested += (_, _) => CloseTile(tile);
        tile.Activated += (_, _) => Activate(tile);
        tile.ManuallyArranged += (_, _) => _settings.AutoLayout = false; // l'utilisateur reprend la main

        Point p = position ?? NextCascadePosition(tile.Width, tile.Height);
        Canvas.SetLeft(tile, Math.Max(0, Math.Min(p.X, Desktop.ActualWidth - tile.Width)));
        Canvas.SetTop(tile, Math.Max(0, Math.Min(p.Y, Desktop.ActualHeight - tile.Height)));

        Desktop.Children.Add(tile);
        _tiles.Add(tile);
        Activate(tile);
        UpdateEmptyHint();
        ApplyAutoLayout();
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
            Title = "Ouvrir une vidéo",
            Multiselect = true,
            Filter = "Vidéos|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.flv;*.m4v;*.mpg;*.mpeg;*.ts;*.m2ts;*.3gp;*.ogv|Tous les fichiers|*.*",
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

    private void Activate(VideoTile tile)
    {
        Panel.SetZIndex(tile, ++_zOrder);
        if (_activeTile == tile) return;
        _activeTile?.SetActive(false);
        _activeTile = tile;
        tile.SetActive(true);
    }

    private void RememberPosition(VideoTile tile)
    {
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
            if (next != null) Activate(next);
        }
        if (_tiles.Count == 0) _cascadeIndex = 0;
        UpdateEmptyHint();
        ApplyAutoLayout();
    }

    private void UpdateEmptyHint() => EmptyHint.Visibility = _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Desktop_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var tile in _tiles) tile.FitToDesktop();
        ApplyAutoLayout();
    }

    // ---------------------------------------------------------------- Organisation automatique

    private Rect DesktopArea => new(0, 0, Desktop.ActualWidth, Desktop.ActualHeight);

    /// <summary>Vignettes concernées par l'organisation (une vignette agrandie reste agrandie).</summary>
    private List<VideoTile> ArrangeableTiles => _tiles.Where(t => !t.IsMaximized).ToList();

    /// <summary>Applique une disposition et active l'organisation automatique.</summary>
    private void ApplyLayout(string id)
    {
        _settings.LayoutId = id;
        _settings.AutoLayout = true;
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
