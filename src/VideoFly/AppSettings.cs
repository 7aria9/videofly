using System.IO;
using System.Text.Json;

namespace VideoFly;

/// <summary>Réglages de l'application, enregistrés dans %AppData%\VideoFly\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Adapter la fenêtre à la taille de la vidéo à l'ouverture d'un fichier.</summary>
    public bool AutoResizeWindowToVideo { get; set; } = true;

    /// <summary>Volume (0 à 100).</summary>
    public int Volume { get; set; } = 80;

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
