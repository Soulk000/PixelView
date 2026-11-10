using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace PixelView;

internal static class DefaultApps
{
    private const string Name = "PixelView";
    private const string ProgId = "PixelView.Image";
    private const string Capabilities = @"Software\PixelView\Capabilities";
    private const string Associations = Capabilities + @"\FileAssociations";
    private const string Classes = @"Software\Classes\";
    private const string SettingsUri = "ms-settings:defaultapps";

    internal static readonly string[] Common =
        [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif", ".heic", ".heif", ".jxl", ".tif", ".tiff", ".ico", ".svg", ".svgz", ".apng"];

    internal static string InstalledExecutable => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name, "PixelView.exe");

    internal static string[]? ReadSelection(RegistryKey root)
    {
        using var key = root.OpenSubKey(Associations);
        return key?.GetValueNames().Where(extension => Equals(key.GetValue(extension), ProgId)).ToArray();
    }

    internal static void Prepare(IEnumerable<string> extensions)
    {
        string[] selected = extensions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected.Length == 0 || selected.Any(extension => !ImageDocument.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("请至少勾选一个受支持的图片格式。");
        using var mutex = new Mutex(false, @"Local\PixelView.DefaultApps");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(8)))
                throw new IOException("另一个 PixelView 正在更新文件关联，请稍后重试。");
        }
        catch (AbandonedMutexException)
        {
            // The previous process stopped mid-update. Registering the selection again repairs it.
        }
        try
        {
            string source = GetPortableExecutable();
            InstallExecutable(source, InstalledExecutable);
            Register(Registry.CurrentUser, InstalledExecutable, selected);
        }
        finally { mutex.ReleaseMutex(); }
        SHChangeNotify(0x08000000, 0x2000, 0, 0); // SHCNE_ASSOCCHANGED, SHCNF_FLUSHNOWAIT
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty Location identifies the portable single-file build.")]
    private static string GetPortableExecutable()
    {
        if (typeof(App).Assembly.Location.Length != 0 || Environment.ProcessPath is not { } path)
            throw new IOException("请运行压缩包中的 PixelView.exe，再设置默认打开方式。");
        return path;
    }

    internal static void InstallExecutable(string source, string destination)
    {
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(destination))
        {
            if (ReadVersion(destination) > ReadVersion(source) || SameFileContents(source, destination))
                return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        catch (IOException error) when (error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) or unchecked((int)0x80070005))
        {
            throw new IOException("无法更新关联使用的程序副本。请关闭其他 PixelView 窗口后重试。", error);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static Version ReadVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        return new(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    private static bool SameFileContents(string source, string destination)
    {
        using var first = File.OpenRead(source);
        using var second = File.OpenRead(destination);
        return first.Length == second.Length && SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }

    internal static void Register(RegistryKey root, string executable, IEnumerable<string> extensions)
    {
        var selected = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string icon = $"\"{executable}\",0";
        string command = $"\"{executable}\" \"%1\"";
        using (var handler = root.CreateSubKey(Classes + ProgId))
        {
            handler.SetValue("", "PixelView 图片");
            handler.SetValue("AllowSilentDefaultTakeOver", "");
            using var defaultIcon = handler.CreateSubKey("DefaultIcon");
            defaultIcon.SetValue("", icon);
            using var open = handler.CreateSubKey(@"shell\open\command");
            open.SetValue("", command);
        }
        using (var capabilities = root.CreateSubKey(Capabilities))
        {
            capabilities.SetValue("ApplicationName", Name);
            capabilities.SetValue("ApplicationDescription", "PixelView 图片查看器，支持像素画最近邻缩放。");
            capabilities.SetValue("ApplicationIcon", icon);
        }
        using (var types = root.CreateSubKey(Associations))
        {
            foreach (string extension in selected)
            {
                types.SetValue(extension, ProgId);
                using var openWith = root.CreateSubKey(Classes + extension + @"\OpenWithProgids");
                openWith.SetValue(ProgId, "");
            }
            foreach (string extension in types.GetValueNames())
            {
                if (selected.Contains(extension) || !Equals(types.GetValue(extension), ProgId)) continue;
                using var openWith = root.OpenSubKey(Classes + extension + @"\OpenWithProgids", writable: true);
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
                types.DeleteValue(extension, throwOnMissingValue: false);
            }
        }
        using var registered = root.CreateSubKey(@"Software\RegisteredApplications");
        registered.SetValue(Name, Capabilities);
    }

    internal static void OpenSettings()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            try { Launch(SettingsUri + "?registeredAppUser=" + Uri.EscapeDataString(Name)); return; }
            catch (System.ComponentModel.Win32Exception) { }
        }
        Launch(SettingsUri);

        static void Launch(string uri) =>
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
    }

    internal static HashSet<string> QueryDefaults(IEnumerable<string> extensions)
    {
        var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("591209C7-767B-42B2-9FBA-44EE4615F2C7"), throwOnError: true)!;
            var registration = (IAssociationRegistration)Activator.CreateInstance(type)!;
            try
            {
                foreach (string extension in extensions)
                {
                    const int fileExtension = 0, effective = 1;
                    int result = registration.QueryCurrentDefault(extension, fileExtension, effective, out string? handler);
                    if (result >= 0 && (string.Equals(handler, ProgId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(handler, @"Applications\PixelView.exe", StringComparison.OrdinalIgnoreCase)))
                        defaults.Add(extension);
                }
            }
            finally { Marshal.ReleaseComObject(registration); }
        }
        catch (COMException) { } // Display only verified defaults when the Shell is available.
        return defaults;
    }

    [ComImport, Guid("4E530B0A-E611-4C77-A3AC-9031D022281B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAssociationRegistration
    {
        [PreserveSig]
        int QueryCurrentDefault([MarshalAs(UnmanagedType.LPWStr)] string extension, int type, int level,
            [MarshalAs(UnmanagedType.LPWStr)] out string? handler);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
