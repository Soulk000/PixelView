using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace PixelView;

public partial class MainWindow : Window
{
    private readonly WindowPreferences prefs = WindowPreferences.Load();
    private readonly DispatcherTimer animationTimer = new();
    private readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer navigationTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
    private readonly SemaphoreSlim decoder = new(1);
    private ImageDocument? document;
    private string? currentPath;
    private string[] folder = [];
    private int navigationIndex = -1;
    private int shownIndex = -1;
    private int frame;
    private long openRequest;
    private bool playing;
    private bool closed;
    private bool loaded;
    private bool fullscreen;
    private bool changingMode;
    private Point? drag;
    private WindowRect? lastNormal;
    private WindowRect? beforeFull;
    private WindowState beforeFullState;
    private int wheelRemainder;

    public MainWindow() : this(null) { }

    public MainWindow(string? initialPath)
    {
        InitializeComponent();
        animationTimer.Tick += (_, _) => ShowFrame(frame + 1);
        noticeTimer.Tick += (_, _) => HideNotice();
        navigationTimer.Tick += (_, _) => HideNavigation();
        Surface.ZoomChanged += UpdateInfo;
        ApplyPreferences();
        UpdateInfo();
        SourceInitialized += (_, _) =>
        {
            var source = HwndSource.FromHwnd(Handle);
            source.AddHook(WindowMessage);
            NativeWindow.UseDarkCaption(source.Handle);
        };
        Loaded += async (_, _) =>
        {
            if (loaded) return;
            if (prefs.Remember && prefs.Bounds is { } bounds && NativeWindow.IsVisible(bounds))
            {
                NativeWindow.SetBounds(Handle, bounds);
                if (prefs.Maximized) WindowState = WindowState.Maximized;
            }
            lastNormal = prefs.Remember && prefs.Bounds is { } previous && NativeWindow.IsVisible(previous)
                ? previous : NativeWindow.Bounds(Handle);
            loaded = true;
            Surface.Focus();
            if (!string.IsNullOrWhiteSpace(initialPath)) await OpenPath(initialPath);
        };
        LocationChanged += (_, _) => RememberNormalBounds();
        SizeChanged += (_, _) => RememberNormalBounds();
        StateChanged += (_, _) => RememberNormalBounds();
        Closing += (_, _) =>
        {
            RememberNormalBounds();
            prefs.Bounds = prefs.Remember ? lastNormal : null;
            prefs.Maximized = (fullscreen ? beforeFullState : WindowState) == WindowState.Maximized;
            prefs.Save();
            closed = true;
            animationTimer.Stop();
            noticeTimer.Stop();
            navigationTimer.Stop();
            document?.Dispose();
        };
    }

    private nint Handle => new WindowInteropHelper(this).Handle;
    private Point CanvasCenter => new(Surface.ActualWidth / 2, Surface.ActualHeight / 2);

    private async Task OpenPath(string path, bool fromFolder = false)
    {
        long request = Interlocked.Increment(ref openRequest);
        try
        {
            path = Path.GetFullPath(path);
            ShowNotice("正在打开 " + Path.GetFileName(path), sticky: true);
            await decoder.WaitAsync();
            try
            {
                if (closed || request != openRequest) return;
                var result = await Task.Run(() =>
                {
                    var decoded = ImageDocument.Open(path);
                    return (Image: decoded, Neighbors: fromFolder ? null : FolderImages(path));
                });
                if (closed || request != openRequest)
                {
                    result.Image.Dispose();
                    return;
                }
                if (result.Neighbors != null) folder = result.Neighbors;
                shownIndex = navigationIndex = Array.FindIndex(folder, file => StringComparer.OrdinalIgnoreCase.Equals(file, path));
                SetDocument(result.Image, path);
                HideNotice();
            }
            finally { decoder.Release(); }
        }
        catch (Exception error)
        {
            if (closed || request != openRequest) return;
            string reason = error is IOException or UnauthorizedAccessException ? error.Message : "当前解码器无法读取这张图片。";
            ShowNotice($"打不开 {Path.GetFileName(path)}。{reason}");
        }
    }

    private static string[] FolderImages(string path)
    {
        var files = new List<string>();
        try { files.AddRange(Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Where(ImageDocument.IsImagePath)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        if (!files.Contains(path, StringComparer.OrdinalIgnoreCase)) files.Add(path);
        files.Sort((first, second) => NativeWindow.StrCmpLogicalW(Path.GetFileName(first), Path.GetFileName(second)));
        return files.ToArray();
    }

    private void SetDocument(ImageDocument image, string? path)
    {
        animationTimer.Stop();
        document?.Dispose();
        document = image;
        currentPath = path;
        frame = 0;
        playing = image.Animated;
        Welcome.Visibility = Visibility.Collapsed;
        Surface.SetImage(image.First, prefs.KeepZoom);
        Title = path == null ? "剪贴板 — PixelView" : Path.GetFileName(path) + " — PixelView";
        UpdateInfo();
        ScheduleFrame();
    }

    private async void OpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开图片",
            CheckFileExists = true,
            Filter = "图片|" + string.Join(';', ImageDocument.Extensions.Order().Select(extension => "*" + extension))
                + "|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) == true) await OpenPath(dialog.FileName);
    }

    private async void Paste()
    {
        try
        {
            if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().OfType<string>().FirstOrDefault(File.Exists) is { } file)
            {
                await OpenPath(file);
                return;
            }
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
            {
                Interlocked.Increment(ref openRequest);
                folder = [];
                navigationIndex = shownIndex = -1;
                SetDocument(ImageDocument.FromBitmap(bitmap), null);
                HideNotice();
                return;
            }
            if (Clipboard.ContainsText() && Clipboard.GetText().Trim().Trim('"') is { Length: > 0 } path && File.Exists(path))
                await OpenPath(path);
            else
                ShowNotice("剪贴板中没有图片或本地图片路径。");
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or IOException)
        {
            ShowNotice("暂时无法读取剪贴板。");
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.FirstOrDefault(File.Exists) is { } first)
            await OpenPath(first);
    }

    private async void Navigate(int delta)
    {
        if (folder.Length < 2) return;
        navigationIndex = (navigationIndex + delta + folder.Length) % folder.Length;
        await OpenPath(folder[navigationIndex], fromFolder: true);
    }

    private void PreviousClick(object sender, RoutedEventArgs e) => Navigate(-1);
    private void NextClick(object sender, RoutedEventArgs e) => Navigate(1);
    private void ZoomOutClick(object sender, RoutedEventArgs e) => Zoom(1 / 1.25);
    private void ZoomInClick(object sender, RoutedEventArgs e) => Zoom(1.25);
    private void ActualClick(object sender, RoutedEventArgs e) => Surface.Actual();
    private void FitClick(object sender, RoutedEventArgs e) => Surface.Fit();
    private void LockClick(object sender, RoutedEventArgs e) => ToggleLock();
    private void ViewLockClick(object sender, RoutedEventArgs e) => ToggleViewLock();
    private void FullClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void Zoom(double scale) => Surface.ZoomAt(scale, CanvasCenter, prefs.IntegerSteps);

    private void WheelZoom(object sender, MouseWheelEventArgs e)
    {
        if (!Surface.HasImage) return;
        e.Handled = true;
        Point anchor = e.GetPosition(Surface);
        if (!prefs.IntegerSteps)
        {
            Surface.ZoomAt(Math.Pow(1.25, e.Delta / 120d), anchor, false);
            return;
        }
        wheelRemainder += e.Delta;
        int steps = wheelRemainder / 120;
        wheelRemainder %= 120;
        for (int i = 0; i < Math.Abs(steps); i++)
            Surface.ZoomAt(steps > 0 ? 1.25 : 1 / 1.25, anchor, true);
    }

    private void SurfaceDown(object sender, MouseButtonEventArgs e)
    {
        if (!Surface.HasImage) return;
        HideNavigation();
        Surface.Focus();
        if (e.ClickCount == 2)
        {
            if (Math.Abs(Surface.Zoom - 1) < 0.00001) Surface.Fit();
            else Surface.Actual();
        }
        else
        {
            drag = e.GetPosition(Surface);
            Surface.CaptureMouse();
            Surface.Cursor = Cursors.SizeAll;
        }
        e.Handled = true;
    }

    private void SurfaceMove(object sender, MouseEventArgs e)
    {
        if (drag is not { } previous) return;
        var next = e.GetPosition(Surface);
        if (next == previous) return;
        Surface.Pan(next - previous);
        drag = next;
    }

    private void SurfaceUp(object sender, MouseButtonEventArgs e) => Surface.ReleaseMouseCapture();

    private void SurfaceLostCapture(object sender, MouseEventArgs e)
    {
        drag = null;
        Surface.Cursor = null;
    }

    private void ShowFrame(int index)
    {
        animationTimer.Stop();
        if (document == null || document.Count < 2) return;
        int next = (index + document.Count) % document.Count;
        try
        {
            Surface.SetFrame(document.GetFrame(next));
            frame = next;
            UpdateInfo();
            ScheduleFrame();
        }
        catch (Exception)
        {
            playing = false;
            UpdateInfo();
            ShowNotice("这一帧无法显示。");
        }
    }

    private void ScheduleFrame()
    {
        if (document == null || !playing) return;
        animationTimer.Interval = TimeSpan.FromMilliseconds(document.Delay(frame));
        animationTimer.Start();
    }

    private void TogglePlayback()
    {
        if (document?.Animated != true) return;
        playing = !playing;
        if (playing) ScheduleFrame();
        else animationTimer.Stop();
        UpdateInfo();
    }

    private void StepFrame(int delta)
    {
        playing = false;
        ShowFrame(frame + delta);
    }

    private void UpdateInfo()
    {
        bool hasImage = document != null;
        PreviousButton.IsEnabled = NextButton.IsEnabled = folder.Length > 1;
        PreviousOverlay.IsEnabled = NextOverlay.IsEnabled = folder.Length > 1;
        if (folder.Length < 2) HideNavigation();
        ZoomButton.IsEnabled = FitButton.IsEnabled = ZoomInButton.IsEnabled = ZoomOutButton.IsEnabled = hasImage;
        ZoomButton.Content = hasImage ? Surface.Zoom.ToString("0.##%", CultureInfo.InvariantCulture) : "—";
        if (document == null)
        {
            FileInfo.Text = "等待打开图片";
            FrameInfo.Text = prefs.Nearest ? "最近邻" : "平滑";
            return;
        }
        string position = folder.Length > 1 && shownIndex >= 0 ? $"    {shownIndex + 1} / {folder.Length}" : "";
        FileInfo.Text = $"{Path.GetFileName(currentPath) ?? "剪贴板"}    ·    {Surface.PixelWidth} × {Surface.PixelHeight}    ·    {document.Format}{position}";
        FileInfo.ToolTip = currentPath;
        FrameInfo.Text = document.Count > 1 ? $"{(playing ? "播放" : "帧")} {frame + 1} / {document.Count}" : prefs.Nearest ? "最近邻" : "平滑";
    }

    private void ShowNotice(string message, bool sticky = false)
    {
        noticeTimer.Stop();
        NoticeText.Text = message;
        Notice.Visibility = Visibility.Visible;
        if (!sticky) noticeTimer.Start();
    }

    private void HideNotice()
    {
        noticeTimer.Stop();
        Notice.Visibility = Visibility.Collapsed;
    }

    private void ToggleLock()
    {
        prefs.Locked = !prefs.Locked;
        ApplyPreferences();
    }

    private void ToggleViewLock()
    {
        prefs.KeepZoom = !prefs.KeepZoom;
        ApplyPreferences();
    }

    private void ViewportMove(object sender, MouseEventArgs e) => UpdateEdgeNavigation(e.GetPosition(Viewport));
    private void ViewportLeave(object sender, MouseEventArgs e) => HideNavigation();

    private void UpdateEdgeNavigation(Point point)
    {
        bool atEdge = point.X >= 0 && point.X <= Viewport.ActualWidth &&
            (point.X <= 90 || point.X >= Viewport.ActualWidth - 90);
        if (!atEdge || drag != null || folder.Length < 2)
        {
            HideNavigation();
            return;
        }
        SetNavigationVisible(true);
        navigationTimer.Stop();
        navigationTimer.Start();
    }

    private void HideNavigation()
    {
        navigationTimer.Stop();
        SetNavigationVisible(false);
    }

    private void SetNavigationVisible(bool visible)
    {
        if (PreviousOverlay.IsHitTestVisible == visible) return;
        foreach (var button in new[] { PreviousOverlay, NextOverlay })
        {
            button.IsHitTestVisible = visible;
            button.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(140)));
        }
    }

    private void ApplyPreferences()
    {
        Topmost = fullscreen || prefs.Topmost;
        Toolbar.Visibility = !fullscreen && prefs.Toolbar ? Visibility.Visible : Visibility.Collapsed;
        Status.Visibility = !fullscreen && prefs.Status ? Visibility.Visible : Visibility.Collapsed;
        WindowStyle = fullscreen || prefs.Borderless ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        ResizeMode = fullscreen ? ResizeMode.NoResize : ResizeMode.CanResize;
        bool useCustomChrome = !fullscreen && prefs.Borderless;
        if (useCustomChrome != (WindowChrome.GetWindowChrome(this) != null))
            WindowChrome.SetWindowChrome(this, useCustomChrome
                ? new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0) }
                : null);
        if (Handle != 0) NativeWindow.UseDarkCaption(Handle);
        LockGlyph.Text = prefs.Locked ? "\uE72E" : "\uE785";
        LockText.Text = prefs.Locked ? "窗口已固定" : "固定窗口";
        LockButton.Foreground = (Brush)FindResource(prefs.Locked ? "Accent" : "Muted");
        Surface.Configure(prefs.Background, prefs.Checker, prefs.Nearest);
        if (prefs.KeepZoom) Surface.HoldView();
        ViewLockGlyph.Text = prefs.KeepZoom ? "\uE72E" : "\uE785";
        ViewLockText.Text = prefs.KeepZoom ? "视图已固定" : "固定视图";
        ViewLockButton.Foreground = (Brush)FindResource(prefs.KeepZoom ? "Accent" : "Muted");
        ViewLockButton.Background = prefs.KeepZoom ? new SolidColorBrush(Color.FromRgb(41, 71, 63)) : Brushes.Transparent;
        UpdateInfo();
    }

    private void ToggleFullscreen()
    {
        changingMode = true;
        try
        {
            if (fullscreen)
            {
                fullscreen = false;
                ApplyPreferences();
                if (beforeFull is { } original) NativeWindow.SetBounds(Handle, original);
                WindowState = beforeFullState;
            }
            else if (NativeWindow.MonitorBounds(Handle, false) is { } monitor)
            {
                beforeFullState = WindowState;
                beforeFull = WindowState == WindowState.Normal ? NativeWindow.Bounds(Handle) : lastNormal;
                WindowState = WindowState.Normal;
                fullscreen = true;
                ApplyPreferences();
                NativeWindow.SetBounds(Handle, monitor);
            }
        }
        finally
        {
            changingMode = false;
            RememberNormalBounds();
        }
        Surface.Focus();
    }

    private void RememberNormalBounds()
    {
        if (loaded && !fullscreen && !changingMode && WindowState == WindowState.Normal && NativeWindow.Bounds(Handle) is { } rect)
            lastNormal = rect;
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int systemCommand = 0x112;
        int command = (int)wParam & 0xFFF0;
        if (message == systemCommand && (prefs.Locked || fullscreen) && !changingMode &&
            (command is 0xF000 or 0xF010 or 0xF030 || command == 0xF120 && WindowState == WindowState.Maximized))
            handled = true;
        return 0;
    }

    private void MoveFromToolbar(object sender, MouseButtonEventArgs e)
    {
        if (prefs.Borderless && !prefs.Locked && !fullscreen && e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void MenuClick(object sender, RoutedEventArgs e) => OpenMenu(MenuButton);
    private void SurfaceMenu(object sender, MouseButtonEventArgs e) => OpenMenu(null);

    private void OpenMenu(UIElement? target)
    {
        var menu = new ContextMenu
        {
            Placement = target == null ? PlacementMode.MousePoint : PlacementMode.Bottom,
            PlacementTarget = target ?? Surface,
            FontSize = 13
        };
        menu.Items.Add(Item("打开图片…", () => OpenClick(this, new RoutedEventArgs()), "Ctrl+O"));
        menu.Items.Add(Item("粘贴图片或路径", Paste, "Ctrl+V"));
        menu.Items.Add(Item("默认打开方式…", ChooseDefaultApps));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("适应画布", Surface.Fit, "0", enabled: Surface.HasImage));
        menu.Items.Add(Item("实际像素 100%", Surface.Actual, "1", enabled: Surface.HasImage));
        menu.Items.Add(Check("像素模式（最近邻）", prefs.Nearest, value => prefs.Nearest = value));
        menu.Items.Add(Check("整数倍率放大", prefs.IntegerSteps, value => { prefs.IntegerSteps = value; wheelRemainder = 0; }));
        menu.Items.Add(Check("固定视图（保留倍率与位置）", prefs.KeepZoom, value => prefs.KeepZoom = value, "Ctrl+K"));
        var background = new MenuItem { Header = "画布背景" };
        foreach (string color in new[] { "深灰", "纯黑", "中灰", "纯白" })
            background.Items.Add(Check(color, prefs.Background == color, _ => prefs.Background = color));
        background.Items.Add(new Separator());
        background.Items.Add(Check("透明区域显示棋盘格", prefs.Checker, value => prefs.Checker = value));
        menu.Items.Add(background);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(fullscreen ? "退出全屏" : "全屏", ToggleFullscreen, "F11"));
        menu.Items.Add(Check("固定窗口位置和大小", prefs.Locked, value => prefs.Locked = value, "Ctrl+L"));
        menu.Items.Add(Check("窗口置顶", prefs.Topmost, value => prefs.Topmost = value));
        menu.Items.Add(Check("无边框窗口", prefs.Borderless, value => prefs.Borderless = value));
        menu.Items.Add(Check("记住窗口位置和大小", prefs.Remember, value => prefs.Remember = value));
        menu.Items.Add(Check("显示工具栏", prefs.Toolbar, value => prefs.Toolbar = value, "Ctrl+T"));
        menu.Items.Add(Check("显示状态栏", prefs.Status, value => prefs.Status = value));
        menu.Items.Add(Item("自定义窗口尺寸…", ChooseSize, enabled: !prefs.Locked && !fullscreen));
        if (document?.Count > 1)
        {
            menu.Items.Add(new Separator());
            if (document.Animated) menu.Items.Add(Item(playing ? "暂停动画" : "播放动画", TogglePlayback, "Space"));
            menu.Items.Add(Item("上一帧 / 页", () => StepFrame(-1), "PgUp"));
            menu.Items.Add(Item("下一帧 / 页", () => StepFrame(1), "PgDn"));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("关闭 PixelView", Close, "Alt+F4"));
        menu.IsOpen = true;

        MenuItem Check(string label, bool value, Action<bool> set, string shortcut = "")
        {
            var item = new MenuItem { Header = label, InputGestureText = shortcut, IsCheckable = true, IsChecked = value };
            item.Click += (_, _) => { set(item.IsChecked); ApplyPreferences(); };
            return item;
        }
    }

    private static MenuItem Item(string label, Action action, string shortcut = "", bool enabled = true)
    {
        var item = new MenuItem { Header = label, InputGestureText = shortcut, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private void ChooseDefaultApps()
    {
        try { new DefaultAppsWindow { Owner = this }.ShowDialog(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowNotice("无法读取默认打开方式设置。 " + error.Message);
        }
    }

    private void ChooseSize()
    {
        var input = new TextBox { Text = $"{(int)ActualWidth} × {(int)ActualHeight}", Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(6) };
        var prompt = new TextBlock { Text = "输入宽 × 高（逻辑像素）" };
        var button = new Button { Content = "应用", IsDefault = true, MinWidth = 70, Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Right };
        var layout = new StackPanel { Margin = new Thickness(18) };
        layout.Children.Add(prompt);
        layout.Children.Add(input);
        layout.Children.Add(button);
        var dialog = new Window
        {
            Title = "自定义窗口尺寸", Content = layout, Width = 320, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ShowInTaskbar = false
        };
        double width = 0, height = 0;
        button.Click += (_, _) =>
        {
            string[] parts = input.Text.Split(['×', 'x', 'X', '*'], StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int requestedWidth) || !int.TryParse(parts[1], out int requestedHeight) ||
                requestedWidth < MinWidth || requestedHeight < MinHeight || requestedWidth > 8000 || requestedHeight > 8000)
            {
                prompt.Text = $"宽度 {MinWidth:0}–8000，高度 {MinHeight:0}–8000";
                return;
            }
            width = requestedWidth;
            height = requestedHeight;
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        if (dialog.ShowDialog() != true) return;
        WindowState = WindowState.Normal;
        Width = width;
        Height = height;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.O: OpenClick(this, e); break;
                case Key.V: Paste(); break;
                case Key.L: ToggleLock(); break;
                case Key.K: ToggleViewLock(); break;
                case Key.T: prefs.Toolbar = !prefs.Toolbar; ApplyPreferences(); break;
                default: return;
            }
        }
        else if (Keyboard.Modifiers == ModifierKeys.None)
        {
            switch (e.Key)
            {
                case Key.Left: Navigate(-1); break;
                case Key.Right: Navigate(1); break;
                case Key.PageUp: StepFrame(-1); break;
                case Key.PageDown: StepFrame(1); break;
                case Key.Space when document?.Animated == true: TogglePlayback(); break;
                case Key.D0 or Key.NumPad0 or Key.Home: Surface.Fit(); break;
                case Key.D1 or Key.NumPad1: Surface.Actual(); break;
                case Key.Add or Key.OemPlus: Zoom(1.25); break;
                case Key.Subtract or Key.OemMinus: Zoom(1 / 1.25); break;
                case Key.F11: ToggleFullscreen(); break;
                case Key.Escape when fullscreen: ToggleFullscreen(); break;
                case Key.Apps: OpenMenu(Surface); break;
                default: return;
            }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Shift && e.Key == Key.OemPlus)
            Zoom(1.25);
        else
            return;
        e.Handled = true;
    }
}
