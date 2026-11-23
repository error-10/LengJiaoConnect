using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LengJiaoConnect.Models;
using LengJiaoConnect.Services;

namespace LengJiaoConnect;

public partial class MainWindow
{
    private readonly List<AppInfo> _tempApps = new();
    private readonly object _tempAppsGate = new();
    private readonly Dictionary<string, int> _launchCounts = new();
    private DispatcherTimer? _searchTimer;
    private string _searchText = "";

    private static string RecentFilePath => Path.Combine(SettingsStore.DirectoryPath, "recent-apps.json");

    private void OnAppReceived(string package, string name, string icon)
    {
        lock (_tempAppsGate)
        {
            if (_tempApps.Any(a => a.PackageName == package)) return;
            _tempApps.Add(new AppInfo { PackageName = package, AppName = name, IconBase64 = icon });
        }
    }

    private void OnAppsCompleted(int count)
    {
        Dispatcher.UIThread.Post(async () => await ApplyAppsAsync(count));
    }

    private async Task ApplyAppsAsync(int reported)
    {
        List<AppInfo> snapshot;
        lock (_tempAppsGate)
        {
            snapshot = _tempApps.ToList();
            _tempApps.Clear();
        }

        if (snapshot.Count == 0)
        {
            TxtAppsCount.Text = "0 个应用";
            PanelAppsEmpty.IsVisible = true;
            return;
        }

        var ordered = await Task.Run(() =>
        {
            foreach (var app in snapshot)
            {
                app.Icon = DecodeIcon(app.IconBase64);
                app.LaunchCount = LaunchCount(app.PackageName);
            }
            return snapshot
                .OrderByDescending(a => a.LaunchCount)
                .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        });

        _apps.Clear();
        foreach (var app in ordered) _apps.Add(app);

        TxtAppsCount.Text = _apps.Count + " 个应用";
        PanelAppsEmpty.IsVisible = _apps.Count == 0;
        ApplyAppFilter();
        RebuildRecent();
    }

    private static IImage? DecodeIcon(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    private int LaunchCount(string package) => _launchCounts.TryGetValue(package, out var n) ? n : 0;

    private void ApplyAppFilter()
    {
        var keyword = _searchText.Trim();
        var view = string.IsNullOrEmpty(keyword)
            ? _apps.ToList()
            : _apps.Where(a => a.DisplayName.Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
                               a.PackageName.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        ListApps.ItemsSource = view;
        TxtAppsCount.Text = string.IsNullOrEmpty(keyword) ? _apps.Count + " 个应用" : view.Count + " / " + _apps.Count + " 个应用";
        PanelAppsEmpty.IsVisible = view.Count == 0;
    }

    private void TxtAppSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchText = TxtAppSearch.Text ?? "";
        _searchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _searchTimer.Stop();
        _searchTimer.Tick -= SearchTimerTick;
        _searchTimer.Tick += SearchTimerTick;
        _searchTimer.Start();
    }

    private void SearchTimerTick(object? sender, EventArgs e)
    {
        _searchTimer?.Stop();
        ApplyAppFilter();
    }

    private async void BtnRefreshApps_Click(object? sender, RoutedEventArgs e)
    {
        if (_helper is not { IsConnected: true }) { Toast("高级互联没连上，先在设置里装助手", true); return; }
        lock (_tempAppsGate) _tempApps.Clear();
        await _helper.RequestAppsAsync();
        Toast("正在同步应用列表…");
    }

    private async void AppTile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AppInfo app })
            await LaunchAppAsync(app);
    }

    private async void RecentApp_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AppInfo app })
            await LaunchAppAsync(app);
    }

    private async Task LaunchAppAsync(AppInfo app)
    {
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }
        try
        {
            await Adb.ShellAsync(_serial, $"monkey -p {Adb.Quote(app.PackageName)} -c android.intent.category.LAUNCHER 1", 15000);
            _launchCounts[app.PackageName] = LaunchCount(app.PackageName) + 1;
            app.LaunchCount = LaunchCount(app.PackageName);
            SaveLaunchCounts();
            RebuildRecent();
            Toast("已在手机上打开 " + app.DisplayName);
        }
        catch (Exception ex)
        {
            Toast("启动失败：" + ex.Message, true);
        }
    }

    private void RebuildRecent()
    {
        var top = _apps.Where(a => a.LaunchCount > 0)
            .OrderByDescending(a => a.LaunchCount)
            .Take(8)
            .ToList();

        _recent.Clear();
        foreach (var app in top) _recent.Add(app);
        ListRecent.IsVisible = _recent.Count > 0;
        TxtRecentLabel.IsVisible = _recent.Count > 0;
    }

    private void LoadLaunchCounts()
    {
        try
        {
            if (!File.Exists(RecentFilePath)) return;
            var dict = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(RecentFilePath));
            if (dict == null) return;
            _launchCounts.Clear();
            foreach (var kv in dict) _launchCounts[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            Log.Warn("读取常用应用失败：" + ex.Message);
        }
    }

    private void SaveLaunchCounts()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            File.WriteAllText(RecentFilePath, JsonSerializer.Serialize(_launchCounts));
        }
        catch (Exception ex)
        {
            Log.Warn("保存常用应用失败：" + ex.Message);
        }
    }

    private async void BtnInstallApk_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 APK",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Android 安装包") { Patterns = new[] { "*.apk" } } }
        });

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path)) await InstallApkAsync(path);
        }
    }

    private async Task InstallApkAsync(string apkPath)
    {
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }

        Busy("正在安装 " + Path.GetFileName(apkPath) + "…", true);
        try
        {
            var r = await Adb.InstallApkAsync(_serial, apkPath, allowDowngrade: true);
            Toast(r.Success ? "装好了：" + Path.GetFileName(apkPath) : "安装失败：" + r.Message, !r.Success);
            if (r.Success && _helper is { IsConnected: true }) await _helper.RequestAppsAsync();
        }
        finally { Busy("", false); }
    }
}
