using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PixelView;

internal sealed class WindowPreferences
{
    private static string SettingsPath => Environment.GetEnvironmentVariable("PIXELVIEW_SETTINGS") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PixelView", "settings.json");

    public bool Locked { get; set; }
    public bool Topmost { get; set; }
    public bool Borderless { get; set; }
    public bool Remember { get; set; } = true;
    public bool Toolbar { get; set; } = true;
    public bool Status { get; set; } = true;
    public bool Checker { get; set; } = true;
    public bool Nearest { get; set; } = true;
    public bool IntegerSteps { get; set; }
    public bool KeepZoom { get; set; }
    public string Background { get; set; } = "深灰";
    public WindowRect? Bounds { get; set; }
    public bool Maximized { get; set; }

    public static WindowPreferences Load()
    {
        try { return JsonSerializer.Deserialize<WindowPreferences>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        string temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct WindowRect(int left, int top, int right, int bottom)
{
    public int Left { get; set; } = left;
    public int Top { get; set; } = top;
    public int Right { get; set; } = right;
    public int Bottom { get; set; } = bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

internal static class NativeWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public WindowRect Monitor;
        public WindowRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref WindowRect rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint order, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int StrCmpLogicalW(string first, string second);

    public static WindowRect? Bounds(nint window) => GetWindowRect(window, out var bounds) ? bounds : null;

    public static void UseDarkCaption(nint window)
    {
        int enabled = 1;
        _ = DwmSetWindowAttribute(window, 20, ref enabled, sizeof(int));
    }

    public static WindowRect? MonitorBounds(nint window, bool useWorkArea)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(MonitorFromWindow(window, 2), ref info) ? (useWorkArea ? info.Work : info.Monitor) : null;
    }

    public static bool IsVisible(WindowRect rect)
    {
        if (rect.Width < 200 || rect.Height < 100) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromRect(ref rect, 0), ref info)) return false;
        return Math.Min(rect.Right, info.Work.Right) - Math.Max(rect.Left, info.Work.Left) >= 180 &&
               rect.Top >= info.Work.Top - 12 && rect.Top < info.Work.Bottom - 48;
    }

    public static void SetBounds(nint window, WindowRect bounds) =>
        SetWindowPos(window, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0014);
}
