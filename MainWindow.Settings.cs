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
        var enabled = IsAutoStartEnabled();
        _settings.AutoStart = enabled;
        ChkAutoStart.IsChecked = enabled;
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) != null;
        }
        catch (Exception ex)
        {
            Log.Warn("读自启注册表失败：" + ex.Message);
            return false;
        }
    }

    private static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;
            if (enabled) key.SetValue(RunValueName, "\"" + Environment.ProcessPath + "\"");
            else key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn("写自启注册表失败：" + ex.Message);
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

    private async void BtnInstallHelper_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }
        if (!File.Exists(HelperApkPath)) { Toast("找不到助手 APK：" + HelperApkPath, true); return; }

        BtnInstallHelper.IsEnabled = false;
        Busy("正在安装助手…", true);
        try
        {
            var r = await Adb.InstallApkAsync(_serial, HelperApkPath, allowDowngrade: true, grantPermissions: true);
            if (!r.Success)
            {
                Toast("安装失败：" + r.Message, true);
                return;
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
                Toast("助手已就绪");
            }
            else
            {
                Toast("助手已安装，但连接失败：检查通知监听权限", true);
            }
        }
        finally
        {
            Busy("", false);
            BtnInstallHelper.IsEnabled = true;
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
