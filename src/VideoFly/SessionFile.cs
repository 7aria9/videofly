using System.IO;
using System.Text.Json;

namespace VideoFly;

/// <summary>
/// Fichier de session VideoFly (.flyv) : les vignettes ouvertes (vidéos, playlist, place, taille, moment de lecture,
/// volume…) et la disposition, pour tout rouvrir plus tard. Format JSON lisible.
/// </summary>
public sealed class SessionFile
{
    public const string Extension = ".flyv";
    public const string Filter = "Session VideoFly|*.flyv";

    public int Version { get; set; } = 1;
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public bool AutoLayout { get; set; }
    public string? LayoutId { get; set; }

    /// <summary>Taille de la zone des vignettes à l'enregistrement : si la fenêtre a changé, on remet les places à l'échelle.</summary>
    public double DesktopWidth { get; set; }
    public double DesktopHeight { get; set; }
    public List<SavedTile> Tiles { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options));

    /// <exception cref="InvalidDataException">Le fichier n'est pas une session VideoFly valide.</exception>
    public static SessionFile Load(string path)
    {
        try
        {
            var session = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(path));
            if (session?.Tiles != null) return session;
        }
        catch (JsonException)
        {
        }
        throw new InvalidDataException("Ce fichier n'est pas une session VideoFly valide.");
    }

    public static bool IsSessionFile(string path) =>
        string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);
}
