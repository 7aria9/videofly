using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace VideoFly;

/// <summary>Un élément de playlist (fichier vidéo ou audio, ou adresse réseau).</summary>
public sealed class PlaylistItem : INotifyPropertyChanged
{
    private bool _isCurrent;
    private int _number;

    public PlaylistItem(string path)
    {
        Path = path;
        Title = MediaFiles.DisplayName(path);
    }

    public string Path { get; }

    public string Title { get; }

    public bool IsAudio => MediaFiles.IsAudio(Path);

    /// <summary>Position dans la liste, à partir de 1 (affichage).</summary>
    public int Number
    {
        get => _number;
        set { if (_number != value) { _number = value; OnChanged(); } }
    }

    /// <summary>Élément en cours de lecture.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (_isCurrent != value) { _isCurrent = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Liste de lecture d'une vignette.</summary>
public sealed class Playlist
{
    public ObservableCollection<PlaylistItem> Items { get; } = new();

    public Playlist()
    {
        Items.CollectionChanged += (_, _) => Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < Items.Count; i++) Items[i].Number = i + 1;
    }

    public void AddRange(IEnumerable<string> paths, int insertAt = -1)
    {
        foreach (var path in paths)
        {
            var item = new PlaylistItem(path);
            if (insertAt < 0 || insertAt >= Items.Count) Items.Add(item);
            else Items.Insert(insertAt++, item);
        }
    }

    // ---- Fichiers M3U ---------------------------------------------------------------------

    /// <summary>Lit une playlist M3U / M3U8. Les chemins relatifs sont résolus par rapport au dossier de la playlist.</summary>
    public static List<string> Load(string m3uPath)
    {
        string baseDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(m3uPath)) ?? "";
        var result = new List<string>();
        foreach (var raw in File.ReadAllLines(m3uPath, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme.Length > 1)
            {
                result.Add(line); // adresse réseau (http://…)
                continue;
            }
            if (line.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(line, UriKind.Absolute, out var fileUri))
                line = fileUri.LocalPath;

            result.Add(System.IO.Path.IsPathRooted(line) ? line : System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, line)));
        }
        return result;
    }

    /// <summary>Enregistre la playlist au format M3U étendu, en UTF-8 (lisible par VLC, Winamp, foobar2000…).</summary>
    public void Save(string m3uPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var item in Items)
        {
            sb.AppendLine($"#EXTINF:-1,{item.Title}");
            sb.AppendLine(item.Path);
        }
        File.WriteAllText(m3uPath, sb.ToString(), new UTF8Encoding(false));
    }
}
