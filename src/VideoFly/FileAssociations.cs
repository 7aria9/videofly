using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VideoFly;

/// <summary>
/// Déclare VideoFly auprès de Windows comme lecteur des fichiers vidéo, audio et playlists, pour l'utilisateur courant
/// (HKEY_CURRENT_USER : pas besoin de droits administrateur).
/// Windows 10/11 ne laisse pas un programme se mettre lui-même par défaut : après l'enregistrement,
/// on ouvre la page des applications par défaut où il suffit de choisir VideoFly.
/// </summary>
public static class FileAssociations
{
    /// <summary>Extensions déclarées à Windows : vidéo, audio et playlists.</summary>
    private static IEnumerable<string> Extensions => MediaFiles.AllExtensions;

    private const string ProgId = "VideoFly.Video";
    private const string SessionProgId = "VideoFly.Session";
    private const string AppName = "VideoFly";
    private const string CapabilitiesKey = @"Software\VideoFly\Capabilities";
    private const string Classes = @"Software\Classes\";

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "VideoFly.exe");
    private static string ExeName => Path.GetFileName(ExePath);
    private static string OpenCommand => $"\"{ExePath}\" \"%1\"";

    /// <summary>
    /// Associe l'extension .flyv (sessions VideoFly) à VideoFly : double-clic sur un fichier de session = l'ouvrir.
    /// Extension propre à VideoFly, donc on peut s'en déclarer le programme par défaut directement.
    /// Appelé à chaque lancement pour suivre le programme s'il a été déplacé.
    /// </summary>
    public static void RegisterSessionExtension()
    {
        try
        {
            var cu = Registry.CurrentUser;
            using (var cmd = cu.OpenSubKey(Classes + SessionProgId + @"\shell\open\command"))
                if (cmd?.GetValue("") as string == OpenCommand) return; // déjà à jour

            using (var progId = cu.CreateSubKey(Classes + SessionProgId))
            {
                progId.SetValue("", "Session VideoFly");
                using (var icon = progId.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{ExePath}\",0");
                using (var cmd = progId.CreateSubKey(@"shell\open\command")) cmd.SetValue("", OpenCommand);
            }
            using (var ext = cu.CreateSubKey(Classes + SessionFile.Extension))
            {
                ext.SetValue("", SessionProgId);
                using var openWith = ext.CreateSubKey("OpenWithProgids");
                openWith.SetValue(SessionProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
            NotifyShell();
        }
        catch
        {
            // Pas bloquant : on peut toujours ouvrir une session depuis le menu.
        }
    }

    /// <summary>VideoFly est-il déclaré auprès de Windows ?</summary>
    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Classes + ProgId);
        return key != null;
    }

    /// <summary>Déclare VideoFly pour toutes les extensions vidéo, audio et playlist.</summary>
    public static void Register()
    {
        var cu = Registry.CurrentUser;

        // Type de fichier « Vidéo VideoFly » et commande d'ouverture
        using (var progId = cu.CreateSubKey(Classes + ProgId))
        {
            progId.SetValue("", "Média VideoFly");
            using (var icon = progId.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{ExePath}\",0");
            using (var cmd = progId.CreateSubKey(@"shell\open\command")) cmd.SetValue("", OpenCommand);
        }

        // Entrée « Ouvrir avec… »
        using (var app = cu.CreateSubKey(Classes + @"Applications\" + ExeName))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", OpenCommand);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in Extensions) types.SetValue(ext, "");
        }

        // Chaque extension propose VideoFly
        foreach (var ext in Extensions)
        {
            using var openWith = cu.CreateSubKey(Classes + ext + @"\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // Déclaration dans la liste des applications par défaut de Windows
        using (var caps = cu.CreateSubKey(CapabilitiesKey))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", "Lecteur de média léger");
            using var assoc = caps.CreateSubKey("FileAssociations");
            foreach (var ext in Extensions) assoc.SetValue(ext, ProgId);
        }
        using (var registered = cu.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue(AppName, CapabilitiesKey);

        NotifyShell();
    }

    /// <summary>Retire tout ce que <see cref="Register"/> a ajouté.</summary>
    public static void Unregister()
    {
        var cu = Registry.CurrentUser;
        cu.DeleteSubKeyTree(Classes + ProgId, throwOnMissingSubKey: false);
        cu.DeleteSubKeyTree(Classes + SessionProgId, throwOnMissingSubKey: false);
        cu.DeleteSubKeyTree(Classes + SessionFile.Extension, throwOnMissingSubKey: false);
        cu.DeleteSubKeyTree(Classes + @"Applications\" + ExeName, throwOnMissingSubKey: false);
        foreach (var ext in Extensions)
        {
            using var openWith = cu.OpenSubKey(Classes + ext + @"\OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        cu.DeleteSubKeyTree(@"Software\VideoFly\Capabilities", throwOnMissingSubKey: false);
        using (var registered = cu.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            registered?.DeleteValue(AppName, throwOnMissingValue: false);
        NotifyShell();
    }

    /// <summary>
    /// Si VideoFly est déjà déclaré mais que le programme a été déplacé (zip extrait ailleurs, nouvelle version),
    /// on met à jour le chemin pour que le double-clic continue de marcher.
    /// </summary>
    public static void RefreshIfRegistered()
    {
        try
        {
            if (!IsRegistered()) return;
            using var cmd = Registry.CurrentUser.OpenSubKey(Classes + ProgId + @"\shell\open\command");
            if (cmd?.GetValue("") as string != OpenCommand) Register();
        }
        catch
        {
            // Pas bloquant.
        }
    }

    /// <summary>Ouvre les réglages Windows des applications par défaut, sur la page de VideoFly si possible.</summary>
    public static void OpenDefaultAppsSettings()
    {
        foreach (var uri in new[] { "ms-settings:defaultapps?registeredAppUser=" + AppName, "ms-settings:defaultapps" })
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return;
            }
            catch
            {
                // On essaie l'adresse suivante.
            }
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    /// <summary>Prévient l'Explorateur que les associations ont changé.</summary>
    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
}
