using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NetPrio.Models;
using NetPrio.Services;
using WinRT.Interop;

namespace NetPrio;

public sealed partial class MainWindow : Window
{
    private readonly PriorityConfig _config;
    private readonly SpeedTestService _speedTest = new();
    private readonly TrafficShaperService _shaper;
    private readonly TrayIconService _tray;
    private bool _forceClose;
    private bool _isInitializing = true;

    public MainWindow(bool startMinimized = false)
    {
        InitializeComponent();
        _config = PriorityConfig.Load();
        _shaper = new TrafficShaperService(_config);

        var hwnd = WindowNative.GetWindowHandle(this);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        _tray = new TrayIconService(hwnd, iconPath, "NetPrio");
        _tray.MenuTextProvider = () => (
            T("TrayOpen"),
            _shaper.IsRunning ? T("StopShaper") : T("StartShaper"),
            T("TrayExit")
        );
        _tray.RestoreRequested += () => DispatcherQueue.TryEnqueue(() =>
        {
            AppWindow.Show();
            if (AppWindow.Presenter is OverlappedPresenter p) p.Restore();
        });
        _tray.ToggleShaperRequested += () => DispatcherQueue.TryEnqueue(() => ToggleShaper_Click(this, new RoutedEventArgs()));
        _tray.ExitRequested += () => DispatcherQueue.TryEnqueue(() =>
        {
            _forceClose = true;
            _shaper.Stop();
            _tray.Dispose();
            Close();
        });

        AppWindow.Closing += (_, args) =>
        {
            if (!_forceClose)
            {
                args.Cancel = true;
                HideAndTrimToTray();
            }
        };
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPresenterChange && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
                HideAndTrimToTray();
        };

        _shaper.StatusChanged += _ => DispatcherQueue.TryEnqueue(() =>
        {
            StatusBanner.Title = _shaper.IsRunning ? T("StatusActive") : T("StatusStopped");
            ToggleShaperBtn.Content = _shaper.IsRunning ? T("StopShaper") : T("StartShaper");
        });
        _shaper.StatsUpdated += stats => { if (!AppWindow.IsVisible) return; DispatcherQueue.TryEnqueue(() =>
        {
            string mbps = T("Mbps");
            string lowLimitStr = stats.IsLowCapped ? $"{T("Limit")} {stats.LowLimitMbps} {mbps}" : T("NoLimit");
            string medLimitStr = stats.IsMedCapped ? $" ({T("Limit")} {stats.MedLimitMbps})" : "";
            StatusBanner.Message =
                $"{T("TierHigh")}: {stats.HighMbps} {mbps}  |  " +
                $"{T("TierMed")}: {stats.MedMbps} {mbps}{medLimitStr}  |  " +
                $"{T("TierLow")}: {stats.LowMbps} {mbps} ({lowLimitStr})";
        }); };

        HighestList.ItemsSource = _config.HighestApps;
        MediumList.ItemsSource = _config.MediumApps;
        LowList.ItemsSource = _config.LowApps;

        AutoStartToggle.IsOn = StartupTaskService.IsEnabled();
        AutoSpeedToggle.IsOn = _config.AutoMeasureSpeed;
        DownloadBox.Value = _config.ChannelDownloadMbps;
        UploadBox.Value = _config.ChannelUploadMbps;
        MediumCapSlider.Value = _config.MediumCapPercent;
        LowCapSlider.Value = _config.LowCapPercent;
        ExcludeVpnToggle.IsOn = _config.ExcludeVpnTunnelProcess;

        LanguageBox.SelectedIndex = _config.Language switch { "ru" => 1, "en" => 2, _ => 0 };
        _isInitializing = false;

        ApplyLocalization();

        if (startMinimized)
        {
            HideAndTrimToTray();
            _shaper.Start();
        }
    }

    private string T(string key) => LocalizationService.Get(key, _config.Language);

    private void ApplyLocalization()
    {
        Title = T("AppTitle");
        NavPrioritiesItem.Content = T("NavPriorities");
        NavSettingsItem.Content = T("NavSettings");
        TierHighTitle.Text = T("TierHigh");
        TierMedTitle.Text = T("TierMed");
        TierLowTitle.Text = T("TierLow");
        AddHighBtn.Content = T("AddExe");
        AddMedBtn.Content = T("AddExe");
        AddLowBtn.Content = T("AddExe");

        StatusBanner.Title = _shaper.IsRunning ? T("StatusActive") : T("StatusStopped");
        if (!_shaper.IsRunning) StatusBanner.Message = T("StatusHint");
        ToggleShaperBtn.Content = _shaper.IsRunning ? T("StopShaper") : T("StartShaper");

        LanguageBox.Header = T("LangHeader");
        LangAutoItem.Content = T("LangAuto");
        AutoStartToggle.Header = T("AutoStartHeader");
        AutoSpeedToggle.Header = T("AutoSpeedHeader");
        DownloadBox.Header = T("DownloadHeader");
        UploadBox.Header = T("UploadHeader");
        MeasureNowBtn.Content = T("MeasureNow");
        MediumCapSlider.Header = T("MedCapHeader");
        LowCapSlider.Header = T("LowCapHeader");
        ExcludeVpnToggle.Header = T("ExcludeVpnHeader");
        SaveSettingsBtn.Content = T("SaveSettings");
        _tray.UpdateTooltip(T("AppTitle"));
    }

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        StartupTaskService.SetEnabled(AutoStartToggle.IsOn);
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (LanguageBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            _config.Language = tag;
            _config.Save();
            ApplyLocalization();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string tag)
        {
            PrioritiesPanel.Visibility = tag == "priorities" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ToggleShaper_Click(object sender, RoutedEventArgs e)
    {
        if (_shaper.IsRunning) _shaper.Stop();
        else _shaper.Start();
        ToggleShaperBtn.Content = _shaper.IsRunning ? T("StopShaper") : T("StartShaper");
    }

    private void DeleteFromHigh_Click(object sender, RoutedEventArgs e) => RemoveItem(_config.HighestApps, sender);
    private void DeleteFromMed_Click(object sender, RoutedEventArgs e) => RemoveItem(_config.MediumApps, sender);
    private void DeleteFromLow_Click(object sender, RoutedEventArgs e) => RemoveItem(_config.LowApps, sender);

    private void MoveFromHighToMed_Click(object sender, RoutedEventArgs e) => MoveItem(_config.HighestApps, _config.MediumApps, sender);
    private void MoveFromMedToHigh_Click(object sender, RoutedEventArgs e) => MoveItem(_config.MediumApps, _config.HighestApps, sender);
    private void MoveFromMedToLow_Click(object sender, RoutedEventArgs e) => MoveItem(_config.MediumApps, _config.LowApps, sender);
    private void MoveFromLowToMed_Click(object sender, RoutedEventArgs e) => MoveItem(_config.LowApps, _config.MediumApps, sender);

    private void RemoveItem(ObservableCollection<string> list, object sender)
    {
        if (sender is Button { Tag: string exe }) { list.Remove(exe); _config.Save(); }
    }

    private void MoveItem(ObservableCollection<string> from, ObservableCollection<string> to, object sender)
    {
        if (sender is Button { Tag: string exe })
        {
            from.Remove(exe);
            if (!to.Contains(exe, StringComparer.OrdinalIgnoreCase)) to.Add(exe);
            _config.Save();
        }
    }

    private async void AddHighest_Click(object sender, RoutedEventArgs e) => await PromptAddExeAsync(_config.HighestApps);
    private async void AddMedium_Click(object sender, RoutedEventArgs e) => await PromptAddExeAsync(_config.MediumApps);
    private async void AddLow_Click(object sender, RoutedEventArgs e) => await PromptAddExeAsync(_config.LowApps);

    private async Task PromptAddExeAsync(ObservableCollection<string> targetList)
    {
        var running = Process.GetProcesses()
            .Select(p => { try { return p.ProcessName + ".exe"; } catch { return ""; } })
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        var input = new AutoSuggestBox
        {
            PlaceholderText = T("AddDialogPlaceholder"),
            ItemsSource = running
        };
        input.TextChanged += (s, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                s.ItemsSource = running.Where(x => x.Contains(s.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        };

        var dialog = new ContentDialog
        {
            Title = T("AddDialogTitle"),
            Content = input,
            PrimaryButtonText = T("AddBtn"),
            CloseButtonText = T("CancelBtn"),
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            var exe = input.Text.Trim();
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
            if (!targetList.Contains(exe, StringComparer.OrdinalIgnoreCase))
            {
                targetList.Add(exe);
                _config.Save();
            }
        }
    }

    private async void MeasureNow_Click(object sender, RoutedEventArgs e)
    {
        StatusBanner.Message = T("Measuring");
        try
        {
            var mbps = await _speedTest.MeasureDownloadMbpsAsync();
            DownloadBox.Value = mbps;
            _config.ChannelDownloadMbps = mbps;
            _config.Save();
            StatusBanner.Message = $"{T("MeasureDone")}: {mbps} {T("Mbps")}";
        }
        catch (Exception ex)
        {
            StatusBanner.Message = $"{T("MeasureErr")}: {ex.Message}";
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _config.AutoMeasureSpeed = AutoSpeedToggle.IsOn;
        _config.ChannelDownloadMbps = DownloadBox.Value;
        _config.ChannelUploadMbps = UploadBox.Value;
        _config.MediumCapPercent = (int)MediumCapSlider.Value;
        _config.LowCapPercent = (int)LowCapSlider.Value;
        _config.ExcludeVpnTunnelProcess = ExcludeVpnToggle.IsOn;
        _config.Save();
        StatusBanner.Message = $"{T("Saved")} ({_config.ChannelDownloadMbps} {T("Mbps")})";
    }

    private void HideAndTrimToTray()
    {
        AppWindow.Hide();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);
}