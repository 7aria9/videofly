using System.IO;

namespace VideoFly;

/// <summary>Types de fichiers pris en charge : vidéo, audio et playlists.</summary>
public static class MediaFiles
{
    public static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v",
        ".mpg", ".mpeg", ".ts", ".m2ts", ".3gp", ".ogv",
    };

    public static readonly string[] AudioExtensions =
    {
        ".mp3", ".flac", ".wav", ".ogg", ".oga", ".opus", ".m4a", ".aac",
        ".wma", ".aif", ".aiff", ".ape", ".wv", ".mka", ".mid", ".midi",
    };

    public static readonly string[] PlaylistExtensions = { ".m3u", ".m3u8" };

    public static IEnumerable<string> AllExtensions => VideoExtensions.Concat(AudioExtensions).Concat(PlaylistExtensions);

    private static bool HasExtension(string path, string[] extensions)
    {
        string ext = Path.GetExtension(path);
        return extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsAudio(string path) => HasExtension(path, AudioExtensions);

    public static bool IsPlaylist(string path) => HasExtension(path, PlaylistExtensions);

    /// <summary>Nom affiché : nom du fichier sans extension (ou l'adresse pour un flux réseau).</summary>
    public static string DisplayName(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) return path;
        return Path.GetFileNameWithoutExtension(path);
    }

    public static Uri ToUri(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri : new Uri(Path.GetFullPath(path));

    private static string Pattern(IEnumerable<string> extensions) => string.Join(";", extensions.Select(e => "*" + e));

    /// <summary>Filtre de la boîte « Ouvrir » pour tous les médias.</summary>
    public static string OpenFilter =>
        $"Tous les médias|{Pattern(AllExtensions.Append(SessionFile.Extension))}|Vidéos|{Pattern(VideoExtensions)}" +
        $"|Audio|{Pattern(AudioExtensions)}|Playlists|{Pattern(PlaylistExtensions)}|{SessionFile.Filter}|Tous les fichiers|*.*";

    /// <summary>Filtre pour ajouter des fichiers à une playlist (vidéo et audio).</summary>
    public static string AddToPlaylistFilter =>
        $"Vidéos et audio|{Pattern(VideoExtensions.Concat(AudioExtensions))}|Tous les fichiers|*.*";

    public const string PlaylistFilter = "Playlist M3U (UTF-8)|*.m3u8|Playlist M3U|*.m3u";
}
