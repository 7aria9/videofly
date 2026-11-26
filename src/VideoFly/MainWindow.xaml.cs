using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    public void OpenFile(string path, Point? position = null)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"Fichier introuvable :\n{path}", "VideoFly", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var tile = new VideoTile(path, _settings)
        {
            Width = Math.Min(DefaultTileWidth, Math.Max(400, Desktop.ActualWidth)),
            Height = Math.Min(DefaultTileHeight, Math.Max(240, Desktop.ActualHeight)),
        };
        tile.CloseRequested += (_, _) => CloseTile(tile);
        tile.Activated += (_, _) => Activate(tile);

        Point p = position ?? NextCascadePosition(tile.Width, tile.Height);
        Canvas.SetLeft(tile, Math.Max(0, Math.Min(p.X, Desktop.ActualWidth - tile.Width)));
        Canvas.SetTop(tile, Math.Max(0, Math.Min(p.Y, Desktop.ActualHeight - tile.Height)));

        Desktop.Children.Add(tile);
        _tiles.Add(tile);
        Activate(tile);
        UpdateEmptyHint();
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

    private void CloseTile(VideoTile tile)
    {
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
    }

    private void UpdateEmptyHint() => EmptyHint.Visibility = _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Desktop_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var tile in _tiles) tile.FitToDesktop();
    }

    /// <summary>Range les vignettes en grille pour remplir la zone.</summary>
    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (_tiles.Count == 0) return;
        int cols = (int)Math.Ceiling(Math.Sqrt(_tiles.Count));
        int rows = (int)Math.Ceiling(_tiles.Count / (double)cols);
        double w = Desktop.ActualWidth / cols, h = Desktop.ActualHeight / rows;
        for (int i = 0; i < _tiles.Count; i++)
            _tiles[i].SetBounds(new Rect(i % cols * w, i / cols * h, w, h));
    }

    /// <summary>Range les vignettes en cascade.</summary>
    private void Cascade_Click(object sender, RoutedEventArgs e)
    {
        _cascadeIndex = 0;
        foreach (var tile in _tiles.OrderBy(Panel.GetZIndex))
        {
            var p = NextCascadePosition(DefaultTileWidth, DefaultTileHeight);
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
        _settings.Save();
        foreach (var tile in _tiles.ToList()) tile.Shutdown();
        _tiles.Clear();
    }
}
