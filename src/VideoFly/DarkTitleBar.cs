using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VideoFly;

/// <summary>Barre de titre Windows sombre (Windows 10 20H1+ / Windows 11) pour les fenêtres secondaires.</summary>
public static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                int enabled = 1;
                DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
            }
            catch
            {
                // Version de Windows trop ancienne : barre de titre classique.
            }
        };
    }
}
