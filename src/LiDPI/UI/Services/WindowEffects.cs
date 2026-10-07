using System.Runtime.InteropServices;

namespace LiDPI.UI.Services;

/// <summary>DWM tweaks: immersive dark frame, Win11 rounded corners and border colour. No-ops where unsupported.</summary>
internal static class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var on = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));

            var corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            var border = 0x00271F1F; // COLORREF 0x00BBGGRR for #1F1F27
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch
        {
            // dwmapi missing / attribute unsupported: purely cosmetic
        }
    }

    /// <summary>True when DWM draws rounded corners itself (Windows 11+).</summary>
    public static bool SupportsRoundedCorners => Environment.OSVersion.Version.Build >= 22000;
}
