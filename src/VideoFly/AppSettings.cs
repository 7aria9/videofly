using System.IO;
using System.Text.Json;

namespace VideoFly;

/// <summary>Réglages de l'application, enregistrés dans %AppData%\VideoFly\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Adapter la vignette à la taille de la vidéo à l'ouverture d'un fichier.</summary>
    public bool AutoResizeWindowToVideo { get; set; } = true;

    /// <summary>Organisation automatique des vignettes : réappliquée à chaque ouverture, fermeture ou redimensionnement.</summary>
    public bool AutoLayout { get; set; }

    /// <summary>Disposition choisie (voir <see cref="TileLayouts"/>).</summary>
    public string? LayoutId { get; set; }

    /// <summary>Volume des nouvelles vignettes (0 à 100), mémorisé d'une session à l'autre.</summary>
    public int Volume { get; set; } = 80;

    /// <summary>Que faire des vidéos ouvertes quand on ferme VideoFly.</summary>
    public SessionCloseMode OnClose { get; set; } = SessionCloseMode.Ask;

    /// <summary>Vidéos ouvertes à la dernière fermeture (si l'utilisateur a choisi de les garder).</summary>
    public List<SavedTile>? Session { get; set; }

    /// <summary>Historique des vidéos ouvertes, la plus récente en premier.</summary>
    public List<HistoryEntry> History { get; set; } = new();

    public const int MaxHistory = 20;

    /// <summary>Place la vidéo en tête de l'historique (sans perdre sa dernière position).</summary>
    public HistoryEntry AddToHistory(string path)
    {
        var entry = FindInHistory(path) ?? new HistoryEntry { Path = path };
        History.Remove(entry);
        entry.LastOpened = DateTime.Now;
        History.Insert(0, entry);
        if (History.Count > MaxHistory) History.RemoveRange(MaxHistory, History.Count - MaxHistory);
        return entry;
    }

    public HistoryEntry? FindInHistory(string path) =>
        History.FirstOrDefault(h => string.Equals(h.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Mémorise où on s'est arrêté dans la vidéo.</summary>
    public void UpdatePosition(string path, long positionMs, long lengthMs)
    {
        var entry = FindInHistory(path);
        if (entry == null) return;
        entry.PositionMs = positionMs;
        if (lengthMs > 0) entry.LengthMs = lengthMs;
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoFly", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Fichier illisible : on repart des valeurs par défaut.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Pas bloquant si l'enregistrement échoue.
        }
    }
}

/// <summary>Une vidéo de l'historique.</summary>
public sealed class HistoryEntry
{
    public string Path { get; set; } = "";
    public DateTime LastOpened { get; set; }

    /// <summary>Dernière position de lecture (0 si la vidéo a été vue jusqu'au bout).</summary>
    public long PositionMs { get; set; }

    public long LengthMs { get; set; }

    /// <summary>Position où reprendre, ou 0 si ça ne vaut pas la peine (tout début ou toute fin).</summary>
    public long ResumePositionMs =>
        PositionMs > 5000 && (LengthMs <= 0 || PositionMs < LengthMs - 10000) ? PositionMs : 0;
}

/// <summary>À la fermeture : garder les vidéos ouvertes pour le prochain lancement ?</summary>
public enum SessionCloseMode
{
    Ask,
    AlwaysKeep,
    NeverKeep,
}

/// <summary>Une vignette mémorisée à la fermeture : contenu, place dans la fenêtre et moment de lecture.</summary>
public sealed class SavedTile
{
    public List<string> Items { get; set; } = new();
    public int CurrentIndex { get; set; }
    public long PositionMs { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
    public int Volume { get; set; } = 80;
    public bool Muted { get; set; }
    public bool Repeat { get; set; } = true;
}
