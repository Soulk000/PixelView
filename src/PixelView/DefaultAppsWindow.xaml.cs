using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using Microsoft.Win32;

namespace PixelView;

public partial class DefaultAppsWindow : Window
{
    private readonly FormatChoice[] choices;
    private readonly ICollectionView visible;
    private bool busy;
    private bool updatingChoices;

    public DefaultAppsWindow()
    {
        InitializeComponent();
        var common = DefaultApps.Common.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = (DefaultApps.ReadSelection(Registry.CurrentUser) ?? DefaultApps.Common)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        choices = ImageDocument.Extensions.OrderByDescending(common.Contains).ThenBy(extension => extension, StringComparer.Ordinal)
            .Select(extension => new FormatChoice(extension, selected.Contains(extension))).ToArray();
        foreach (var choice in choices)
            choice.PropertyChanged += (_, args) =>
            {
                if (!updatingChoices && args.PropertyName == nameof(FormatChoice.Selected)) UpdateCount();
            };
        visible = CollectionViewSource.GetDefaultView(choices);
        visible.Filter = item => ((FormatChoice)item).Extension.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        Formats.ItemsSource = visible;
        Search.TextChanged += (_, _) => visible.Refresh();
        SourceInitialized += (_, _) => NativeWindow.UseDarkCaption(new WindowInteropHelper(this).Handle);
        Activated += (_, _) => RefreshDefaults();
        UpdateCount();
    }

    private void SelectCommon(object sender, RoutedEventArgs args)
    {
        var common = DefaultApps.Common.ToHashSet(StringComparer.OrdinalIgnoreCase);
        SetSelection(choices.Where(choice => common.Contains(choice.Extension)), true);
    }

    private void SelectVisible(object sender, RoutedEventArgs args) => SetSelection(visible.Cast<FormatChoice>(), true);
    private void ClearVisible(object sender, RoutedEventArgs args) => SetSelection(visible.Cast<FormatChoice>(), false);

    private void SetSelection(IEnumerable<FormatChoice> formats, bool selected)
    {
        updatingChoices = true;
        foreach (var choice in formats) choice.Selected = selected;
        updatingChoices = false;
        UpdateCount();
    }

    private void UpdateCount()
    {
        int selected = choices.Count(choice => choice.Selected);
        int effective = choices.Count(choice => choice.Selected && choice.IsDefault);
        SelectionInfo.Text = $"已勾选 {selected} 项 · 其中 {effective} 项已是默认";
        Apply.IsEnabled = !busy && selected > 0;
    }

    private void RefreshDefaults()
    {
        var defaults = DefaultApps.QueryDefaults(choices.Select(choice => choice.Extension));
        foreach (var choice in choices) choice.IsDefault = defaults.Contains(choice.Extension);
        UpdateCount();
    }

    private async void ApplyClick(object sender, RoutedEventArgs args)
    {
        string[] selected = choices.Where(choice => choice.Selected).Select(choice => choice.Extension).ToArray();
        if (busy || selected.Length == 0) return;
        SetBusy(true);
        Message.Text = "正在向 Windows 注册所选格式…";
        try
        {
            await Task.Run(() => DefaultApps.Prepare(selected));
            if (!IsVisible) return;
            Message.Text = "请在 Windows 设置中将所选扩展名的默认应用改成 PixelView。";
            DefaultApps.OpenSettings();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or Win32Exception)
        {
            Message.Text = error is Win32Exception
                ? "格式已注册。请手动打开 Windows 设置 → 应用 → 默认应用，并搜索 PixelView。"
                : "注册失败。 " + error.Message;
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        Filters.IsEnabled = Formats.IsEnabled = !value;
        UpdateCount();
    }

    private sealed class FormatChoice(string extension, bool selected) : INotifyPropertyChanged
    {
        private bool isSelected = selected;
        private bool isDefault;

        public string Extension { get; } = extension;
        public bool Selected
        {
            get => isSelected;
            set
            {
                if (isSelected == value) return;
                isSelected = value;
                Notify();
            }
        }
        public bool IsDefault
        {
            get => isDefault;
            set
            {
                if (isDefault == value) return;
                isDefault = value;
                Notify(nameof(Status));
            }
        }
        public string Status => IsDefault ? "已是默认" : "";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? property = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
