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
    public string? TransparencyBackground { get; set; }
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
    private delegate nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);
    private static readonly SubclassProc SmallSizingProc = SmallSizingMessage;
    private const nuint SmallSizingId = 0x50565831;

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);

    public static void EnableSmallResizing(nint window) => SetWindowSubclass(window, SmallSizingProc, SmallSizingId, 0);

    private static nint SmallSizingMessage(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        // WPF caches the system tracking minimum before HwndSource hooks run.
        // Adjust the native message first so layout and actual resizing agree.
        if (message == 0x24) SetMinimumTrackSize(window, lParam);
        if (message == 0x82) RemoveWindowSubclass(window, SmallSizingProc, SmallSizingId);
        if (message == 0x46 && lParam != 0)
        {
            var requested = Marshal.PtrToStructure<WindowPosition>(lParam);
            nint result = DefSubclassProc(window, message, wParam, lParam);
            if ((requested.Flags & 1) == 0 && GetWindowRect(window, out var outer) && GetClientRect(window, out var client))
            {
                // DefWindowProc also applies a caption-button width floor here.
                var adjusted = Marshal.PtrToStructure<WindowPosition>(lParam);
                if (requested.Width < adjusted.Width)
                    adjusted.Width = Math.Max(requested.Width, Math.Max(0, outer.Width - client.Width) + 1);
                if (requested.Height < adjusted.Height)
                    adjusted.Height = Math.Max(requested.Height, Math.Max(0, outer.Height - client.Height) + 1);
                Marshal.StructureToPtr(adjusted, lParam, false);
            }
            return result;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public nint Window;
        public nint InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

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
    private static extern bool GetClientRect(nint window, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

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

    public static (double Width, double Height)? MinimumSize(nint window)
    {
        if (!GetWindowRect(window, out var outer) || !GetClientRect(window, out var client)) return null;
        double scale = Math.Max(96, GetDpiForWindow(window)) / 96.0;
        // Keep one physical client pixel in addition to the native frame and caption.
        return ((Math.Max(0, outer.Width - client.Width) + 1) / scale,
                (Math.Max(0, outer.Height - client.Height) + 1) / scale);
    }

    private static void SetMinimumTrackSize(nint window, nint parameter)
    {
        if (parameter == 0 || !GetWindowRect(window, out var outer) || !GetClientRect(window, out var client)) return;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(parameter);
        limits.MinTrackSize.X = Math.Max(0, outer.Width - client.Width) + 1;
        limits.MinTrackSize.Y = Math.Max(0, outer.Height - client.Height) + 1;
        Marshal.StructureToPtr(limits, parameter, false);
    }

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
        if (rect.Width < 1 || rect.Height < 1) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromRect(ref rect, 0), ref info)) return false;
        return Math.Min(rect.Right, info.Work.Right) - Math.Max(rect.Left, info.Work.Left) >= Math.Min(180, rect.Width) &&
               rect.Top >= info.Work.Top - 12 && rect.Top < info.Work.Bottom - Math.Min(48, rect.Height);
    }

    public static void SetBounds(nint window, WindowRect bounds) =>
        SetWindowPos(window, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0014);
}
