using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LengJiaoConnect.Services;
using Microsoft.Win32;

namespace LengJiaoConnect;

public partial class MainWindow
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LengJiaoConnect";
    private const string RepoUrl = "https://github.com/error-10/LengJiaoConnect";
    private const string VersionUrl = "https://biu.miqwq.com/index.html";
    private const string ExpectedPoem = "北冥有鱼，其名为鲲，鲲之大，一锅炖不下";
    private const string CurrentVersion = "3.2.0";

    private void WireHelperEvents()
    {
        if (_helper == null) return;
        _helper.ClipboardReceived += OnClipboardReceived;
        _helper.NotificationReceived += OnNotificationReceived;
        _helper.PermissionChanged += OnPermissionChanged;
        _helper.MediaChanged += OnMediaChanged;
        _helper.AppReceived += OnAppReceived;
        _helper.AppsCompleted += OnAppsCompleted;
        _helper.SmsReceived += OnSmsReceived;
        _helper.Disconnected += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            TxtHelperStatus.Text = "助手连接已断开";
            TxtSideHint.Text = "助手未连接";
        });
    }

    private void LoadSettingsUi()
    {
        ChkAutoReconnect.IsChecked = _settings.AutoReconnect;
        ChkSyncClipboard.IsChecked = _settings.SyncClipboard;
        ChkSyncNotifications.IsChecked = _settings.SyncNotifications;
        ChkSyncApps.IsChecked = _settings.SyncApps;
        ChkSyncSms.IsChecked = _settings.SyncSms;
        ChkShowHidden.IsChecked = _settings.ShowHiddenFiles;
        ChkUseRoot.IsChecked = _settings.UseRoot;

        ChkCastAudio.IsChecked = _settings.CastAudio;
        ChkCastControl.IsChecked = _settings.CastControl;
        ChkCastUhid.IsChecked = _settings.CastUhidKeyboard;
        ChkCastScreenOff.IsChecked = _settings.CastScreenOff;
        (Q0.IsChecked, Q1.IsChecked, Q2.IsChecked) = (_settings.Quality == 0, _settings.Quality == 1, _settings.Quality == 2);

        TxtLastEndpoint.Text = string.IsNullOrWhiteSpace(_settings.LastEndpoint) ? "上次设备：—" : "上次设备：" + _settings.LastEndpoint;
        TxtVersion.Text = "版本 " + CurrentVersion;
        SyncAutoStartCheckbox();
    }

    private void Setting_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;

        _settings.AutoReconnect = ChkAutoReconnect.IsChecked == true;
        _settings.SyncClipboard = ChkSyncClipboard.IsChecked == true;
        _settings.SyncNotifications = ChkSyncNotifications.IsChecked == true;
        _settings.SyncApps = ChkSyncApps.IsChecked == true;
        _settings.SyncSms = ChkSyncSms.IsChecked == true;
        SettingsStore.Save();

        if (ReferenceEquals(sender, ChkAutoStart))
        {
            var enabled = ChkAutoStart.IsChecked == true;
            _settings.AutoStart = enabled;
            SetAutoStart(enabled);
            SettingsStore.Save();
        }

        if (ReferenceEquals(sender, ChkSyncApps) && _settings.SyncApps) _ = RequestAppsAsync();
        if (ReferenceEquals(sender, ChkSyncSms) && _settings.SyncSms) _ = RequestSmsAsync();
    }

    private void SyncAutoStartCheckbox()
    {
        var stored = ReadAutoStartValue();
        var enabled = SamePath(stored, Environment.ProcessPath);

        // 换了新版本、或者把目录挪了之后，旧的自启项还指着老路径：
        // 直接改写成本次运行的 exe，不然开关显示“已开”，开机跑的却是老版本。
        if (!enabled && !string.IsNullOrWhiteSpace(stored) && SetAutoStart(true)) enabled = true;

        _settings.AutoStart = enabled;
        ChkAutoStart.IsChecked = enabled;
    }

    private static string? ReadAutoStartValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) as string;
        }
        catch (Exception ex)
        {
            Log.Warn("读自启注册表失败：" + ex.Message);
            return null;
        }
    }

    private static bool SamePath(string? stored, string? exe)
    {
        if (string.IsNullOrWhiteSpace(stored) || string.IsNullOrWhiteSpace(exe)) return false;
        return string.Equals(stored.Trim().Trim('"'), exe.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAutoStartEnabled() => SamePath(ReadAutoStartValue(), Environment.ProcessPath);

    private static bool SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return false;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) return false;
                key.SetValue(RunValueName, "\"" + exe + "\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("写自启注册表失败：" + ex.Message);
            return false;
        }
    }

    private async Task RefreshHelperStatusAsync()
    {
        if (string.IsNullOrEmpty(_serial))
        {
            TxtHelperStatus.Text = "助手：未连接设备";
            return;
        }

        try
        {
            var installed = await Adb.IsInstalledAsync(_serial, "com.lengjiao.helper");
            if (!installed)
            {
                TxtHelperStatus.Text = "助手未安装，点下面按钮安装。";
                return;
            }

            var version = await Adb.ShellTextAsync(_serial, "dumpsys package com.lengjiao.helper | grep versionName");
            var listeners = await Adb.ShellTextAsync(_serial, "settings get secure enabled_notification_listeners");
            var granted = listeners.Contains("com.lengjiao.helper");
            TxtHelperStatus.Text = $"助手已安装{(version.Contains("versionName") ? " · " + version.Split('=').Last().Trim() : "")} · 通知监听{(granted ? "已授权" : "未授权")}";
        }
        catch (Exception ex)
        {
            TxtHelperStatus.Text = "读取助手状态失败：" + ex.Message;
        }
    }

    private string HelperApkPath => Path.Combine(AppContext.BaseDirectory, "HelperApk", "LengJiaoHelper.apk");

    private readonly HashSet<string> _helperOffered = new(StringComparer.OrdinalIgnoreCase);

    private async void BtnInstallHelper_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }
        await InstallHelperAsync(interactive: true);
    }

    /// <summary>
    /// 第一次连上一台手机时问一次：要不要装助手。装过的不再唠叨。
    /// </summary>
    private async Task MaybeOfferHelperAsync()
    {
        if (string.IsNullOrEmpty(_serial)) return;
        if (!_helperOffered.Add(_serial)) return;

        try
        {
            if (await Adb.IsInstalledAsync(_serial, "com.lengjiao.helper"))
            {
                if (_helper is { IsConnected: false }) await _helper.ConnectAsync();
                await EnsureHelperPermissionsAsync(interactive: false);
                return;
            }

            var install = await ConfirmAsync(
                "安装互联助手？",
                "剪贴板同步、通知转发、短信读取和应用列表，都靠手机上的这个小助手（约 200KB，装一次就行）。\n\n装好后它会用到两项权限：读取短信、通知使用权。",
                "安装",
                "以后再说");

            if (install) await InstallHelperAsync(interactive: true);
        }
        catch (Exception ex)
        {
            Log.Warn("提示安装助手失败：" + ex.Message);
        }
    }

    private async Task<bool> InstallHelperAsync(bool interactive)
    {
        if (string.IsNullOrEmpty(_serial)) { if (interactive) Toast("先连接设备", true); return false; }
        if (!File.Exists(HelperApkPath)) { if (interactive) Toast("找不到助手 APK：" + HelperApkPath, true); return false; }

        BtnInstallHelper.IsEnabled = false;
        if (interactive) Busy("正在安装助手…", true);
        try
        {
            var r = await Adb.InstallApkAsync(_serial, HelperApkPath, allowDowngrade: true, grantPermissions: true);
            if (!r.Success)
            {
                if (interactive) Toast("安装失败：" + r.Message, true);
                Log.Warn("助手安装失败：" + r.Message);
                return false;
            }

            await Adb.ShellAsync(_serial, "cmd notification allow_listener com.lengjiao.helper/com.lengjiao.helper.HelperService");
            await Adb.ShellAsync(_serial, "pm grant com.lengjiao.helper android.permission.READ_SMS");
            await RefreshHelperStatusAsync();

            if (_helper != null)
            {
                _helper.Dispose();
                _helper = null;
            }
            _helper = new HelperClient(_serial);
            WireHelperEvents();

            if (await _helper.ConnectAsync())
            {
                await _helper.CheckPermissionAsync();
            }
            else if (interactive)
            {
                Toast("助手已安装，但还没连上——多半是通知监听没开。", true);
            }

            await EnsureHelperPermissionsAsync(interactive);
            return true;
        }
        finally
        {
            if (interactive) Busy("", false);
            BtnInstallHelper.IsEnabled = true;
        }
    }

    /// <summary>
    /// 装完之后把缺的权限补齐：短信能自动授，通知使用权有的系统得手动勾。
    /// </summary>
    private async Task EnsureHelperPermissionsAsync(bool interactive)
    {
        if (string.IsNullOrEmpty(_serial)) return;

        try
        {
            var listeners = await Adb.ShellTextAsync(_serial, "settings get secure enabled_notification_listeners");
            if (listeners.Contains("com.lengjiao.helper")) return;
            if (!interactive) return;

            var open = await ConfirmAsync(
                "还差一个通知权限",
                "在手机的「通知使用权」里勾选「棱角互联助手」，剪贴板和通知才会同步。要我现在把那个设置页打开吗？",
                "打开设置",
                "稍后我自己弄");

            if (!open) return;

            await Adb.ShellAsync(_serial, "am start -a android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS");
            Toast("在手机上勾选「棱角互联助手」，回来就生效");
        }
        catch (Exception ex)
        {
            Log.Warn("检查助手权限失败：" + ex.Message);
        }
    }

    private async void BtnRevokeHelper_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }
        if (!await ConfirmAsync("停用助手", "会卸载手机上的 com.lengjiao.helper，剪贴板/通知/短信/应用同步都会停。", "卸载", "取消")) return;

        Busy("正在卸载…", true);
        try
        {
            DetachHelper();
            var r = await Adb.RunAsync(new[] { "-s", _serial, "uninstall", "com.lengjiao.helper" });
            Toast(r.Best.Contains("Success") ? "助手已卸载" : "卸载失败：" + r.Best, !r.Best.Contains("Success"));
            await RefreshHelperStatusAsync();
        }
        finally { Busy("", false); }
    }

    private async Task CheckUpdatesAsync(bool silent)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var html = await http.GetStringAsync(VersionUrl);

            var version = Regex.Match(html, @"<div id=""version"">\s*(?<v>[^<]+)\s*</div>");
            var poem = Regex.Match(html, @"<div id=""poem"">\s*(?<p>[^<]+)\s*</div>");

            if (!poem.Success || !poem.Groups["p"].Value.Contains(ExpectedPoem))
            {
                TxtUpdateStatus.Text = "校验失败：返回内容被篡改，已停止更新检查。";
                return;
            }

            if (!version.Success)
            {
                if (!silent) TxtUpdateStatus.Text = "没有解析到版本号。";
                return;
            }

            var remote = version.Groups["v"].Value.Trim();
            if (Version.TryParse(remote, out var rv) && Version.TryParse(CurrentVersion, out var cv) && rv > cv)
                TxtUpdateStatus.Text = $"发现新版本 {remote}（当前 {CurrentVersion}），点“检查更新”去下载。";
            else
                TxtUpdateStatus.Text = silent ? "" : $"已是最新版本（{CurrentVersion}）。";
        }
        catch (Exception ex)
        {
            if (!silent) TxtUpdateStatus.Text = "检查更新失败：" + ex.Message;
            Log.Warn("检查更新失败：" + ex.Message);
        }
    }

    private async void BtnCheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        await CheckUpdatesAsync(silent: false);
        if (TxtUpdateStatus.Text?.StartsWith("发现新版本") == true) OpenUrl(RepoUrl + "/releases");
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("打开链接失败：" + ex.Message); }
    }

    private void BtnOpenGithub_Click(object? sender, RoutedEventArgs e) => OpenUrl(RepoUrl);

    private void BtnOpenLogs_Click(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", SettingsStore.DirectoryPath) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("打开日志目录失败：" + ex.Message); }
    }

    private void BtnOpenTerminal_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/K cd /d \"" + AppContext.BaseDirectory + "\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Log.Warn("打开终端失败：" + ex.Message); }
    }
}
