using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LengJiaoConnect.Services;
using Microsoft.Win32;

namespace LengJiaoConnect;

public partial class MainWindow
{
    private DateTime _lastUpdateCheckUtc = DateTime.MinValue;

    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LengJiaoConnect";
    private const string RepoUrl = "https://github.com/error-10/LengJiaoConnect";
    private const string VersionUrl = "https://biu.miqwq.com/index.html";
    private const string ExpectedPoem = "北冥有鱼，其名为鲲，鲲之大，一锅炖不下";
    private const string CurrentVersion = "3.2.4";

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
            UpdateHomeHelper();
            _ = TryReconnectHelperAsync();
        });
    }

    private async Task TryReconnectHelperAsync()
    {
        await Task.Delay(2000);
        var helper = _helper;
        for (var i = 0; i < 3 && helper is { IsConnected: false }; i++)
        {
            try
            {
                if (await helper.ConnectAsync())
                {
                    await helper.CheckPermissionAsync();
                    Dispatcher.UIThread.Post(UpdateHomeHelper);
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("助手重连失败：" + ex.Message);
            }

            await Task.Delay(2500);
        }
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
            TxtPhoneUpdate.Text = "";
            BtnPhoneUpdate.IsVisible = false;
            _helperInstalled = false;
            UpdateHomeHelper();
            return;
        }

        try
        {
            var installed = await Adb.IsInstalledAsync(_serial, "com.lengjiao.helper");
            if (!installed)
            {
                TxtHelperStatus.Text = "助手未安装，点下面按钮安装。";
                UpdatePhoneUpdateHint("");
                _helperInstalled = false;
                UpdateHomeHelper();
                return;
            }

            var version = await Adb.ShellTextAsync(_serial, "dumpsys package com.lengjiao.helper | grep versionName");
            var listeners = await Adb.ShellTextAsync(_serial, "settings get secure enabled_notification_listeners");
            var granted = listeners.Contains("com.lengjiao.helper");
            var installedVer = version.Contains("versionName=") ? version.Split('=').Last().Trim() : "";
            TxtHelperStatus.Text = $"助手已安装{(installedVer.Length > 0 ? " · " + installedVer : "")} · 通知监听{(granted ? "已授权" : "未授权")}";
            _helperInstalled = true;
            _helperNotif = granted;
            UpdatePhoneUpdateHint(installedVer);
            UpdateHomeHelper();
        }
        catch (Exception ex)
        {
            TxtHelperStatus.Text = "读取助手状态失败：" + ex.Message;
        }
    }

    private static string ReadApkVersionName()
    {
        try
        {
            var manifest = Path.Combine(AppContext.BaseDirectory, "HelperApk", "AndroidManifest.xml");
            if (!File.Exists(manifest)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(manifest), "android:versionName=\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    /// <summary>手机助手版本对照：装的比包旧就亮出“更新助手”。</summary>
    private void UpdatePhoneUpdateHint(string installedVer)
    {
        var apkVer = ReadApkVersionName();
        if (string.IsNullOrEmpty(apkVer))
        {
            TxtPhoneUpdate.Text = "";
            BtnPhoneUpdate.IsVisible = false;
            return;
        }
        if (string.IsNullOrEmpty(installedVer))
        {
            TxtPhoneUpdate.Text = $"手机助手未安装 · 可安装 {apkVer}";
            BtnPhoneUpdate.IsVisible = true;
            return;
        }
        var newer = Version.TryParse(installedVer, out var a) && Version.TryParse(apkVer, out var b) && b > a;
        TxtPhoneUpdate.Text = newer ? $"手机助手 {installedVer} · 可更新到 {apkVer}" : $"手机助手 {installedVer} · 已是最新";
        BtnPhoneUpdate.IsVisible = newer;
    }

    private string HelperApkPath => Path.Combine(AppContext.BaseDirectory, "HelperApk", "LengJiaoHelper.apk");

    private readonly HashSet<string> _helperOffered = new(StringComparer.OrdinalIgnoreCase);
    private bool _helperInstalled;
    private bool _helperSms;
    private bool _helperNotif;

    private async void BtnInstallHelper_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }
        await InstallHelperAsync(interactive: true);
    }

    /// <summary>
    /// 第一次连上一台手机时问一次：要不要装助手。装过的不再唠叨。
    /// </summary>
    private async Task MaybeOfferHelperAsync(bool force = false)
    {
        if (string.IsNullOrEmpty(_serial)) return;
        if (!force && !_helperOffered.Add(_serial)) return;

        try
        {
            if (await Adb.IsInstalledAsync(_serial, "com.lengjiao.helper"))
            {
                _helperInstalled = true;
                UpdateHomeHelper();
                await EnsureHelperPermissionsAsync(interactive: force);
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
            var notifOk = await ProbeNotificationPermissionAsync();
            var smsOk = await ProbeSmsPermissionAsync();

            _helperSms = smsOk;
            _helperNotif = notifOk;
            UpdateHomeHelper();

            if (!interactive || (smsOk && notifOk)) return;

            if (!notifOk)
            {
                var open = await ConfirmAsync(
                    "通知使用权还没开",
                    "在手机的「通知使用权」里勾选「棱角互联助手」，剪贴板和通知才会同步。要我现在把那个设置页打开吗？",
                    "打开设置",
                    "稍后我自己弄");

                if (open)
                {
                    await Adb.ShellAsync(_serial, "am start -a android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS");
                    notifOk = await WaitForPermissionAsync(ProbeNotificationPermissionAsync, 90);
                    _helperNotif = notifOk;
                    UpdateHomeHelper();
                    Toast(notifOk ? "通知使用权已开" : "还没检测到授权，稍后可在设置页重试", !notifOk);
                }
            }

            if (!smsOk)
            {
                var open = await ConfirmAsync(
                    "短信权限还没开",
                    "读取短信要单独给「棱角互联助手」开权限。要我现在打开它的应用详情页吗？",
                    "打开设置",
                    "稍后我自己弄");

                if (open)
                {
                    await Adb.ShellAsync(_serial, "am start -a android.settings.APPLICATION_DETAILS_SETTINGS -d package:com.lengjiao.helper");
                    smsOk = await WaitForPermissionAsync(ProbeSmsPermissionAsync, 90);
                    _helperSms = smsOk;
                    UpdateHomeHelper();
                    Toast(smsOk ? "短信权限已开" : "还没检测到授权，稍后可在设置页重试", !smsOk);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("检查助手权限失败：" + ex.Message);
        }
    }

    private async Task<bool> ProbeNotificationPermissionAsync()
    {
        if (string.IsNullOrEmpty(_serial)) return false;
        var listeners = await Adb.ShellTextAsync(_serial, "settings get secure enabled_notification_listeners");
        return listeners.Contains("com.lengjiao.helper");
    }

    private async Task<bool> ProbeSmsPermissionAsync()
    {
        if (string.IsNullOrEmpty(_serial)) return false;

        var dumpsys = await Adb.ShellTextAsync(_serial, "dumpsys package com.lengjiao.helper | grep READ_SMS");
        if (dumpsys.Contains("granted=true")) return true;

        // 有的 ROM 允许 shell 直接授，授完再查一次
        await Adb.ShellAsync(_serial, "pm grant com.lengjiao.helper android.permission.READ_SMS");
        dumpsys = await Adb.ShellTextAsync(_serial, "dumpsys package com.lengjiao.helper | grep READ_SMS");
        return dumpsys.Contains("granted=true");
    }

    /// <summary>用户去手机上点授权，这边每 2 秒看一次，最多等 timeoutSeconds 秒。</summary>
    private static async Task<bool> WaitForPermissionAsync(Func<Task<bool>> probe, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000);
            try
            {
                if (await probe()) return true;
            }
            catch
            {
                // 手机上正在切页面，adb 偶发失败不算异常
            }
        }

        return false;
    }

    /// <summary>主页那张“高级互联”状态条：安装/连接/权限三种状态都写在这里。</summary>
    private void UpdateHomeHelper()
    {
        if (TxtHelperHome is null || DotHelper is null) return;

        string text;
        string color;

        if (string.IsNullOrEmpty(_serial))
        {
            text = "高级互联 · 未连接设备";
            color = "#64758A";
        }
        else if (!_helperInstalled)
        {
            text = "高级互联 · 未安装，点这里安装";
            color = "#FFD166";
        }
        else if (_helper?.IsConnected != true)
        {
            text = "高级互联 · 未连接，点这里重试";
            color = "#FFD166";
        }
        else if (!_helperSms || !_helperNotif)
        {
            var missing = !_helperSms && !_helperNotif ? "短信、通知权限" : (!_helperSms ? "短信权限" : "通知权限");
            text = $"高级互联 · 已连接 · 缺{missing}，点这里授权";
            color = "#FFD166";
        }
        else
        {
            text = "高级互联 · 已连接";
            color = "#6FD3FF";
        }

        TxtHelperHome.Text = text;
        DotHelper.Fill = new SolidColorBrush(Color.Parse(color));
    }

    private async void BtnHomeHelper_Click(object? sender, PointerPressedEventArgs e)
    {
        if (string.IsNullOrEmpty(_serial))
        {
            Toast("先连接设备", true);
            return;
        }

        if (!_helperInstalled)
        {
            await MaybeOfferHelperAsync(force: true);
            return;
        }

        if (_helper?.IsConnected != true)
        {
            Busy("正在重连助手…", true);
            var ok = false;
            try { ok = _helper != null && await _helper.ConnectAsync(); }
            catch (Exception ex) { Log.Warn("助手重连失败：" + ex.Message); }
            Busy("", false);
            await RefreshHelperStatusAsync();
            Toast(ok ? "助手已重新连上" : "助手没连上：确认手机上的通知使用权还开着", !ok);
            return;
        }

        if (!_helperSms || !_helperNotif)
        {
            await EnsureHelperPermissionsAsync(interactive: true);
            return;
        }

        Toast("高级互联正常：剪贴板、通知、短信都在同步");
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

    private sealed record UpdatePage(string Html, DateTimeOffset ServerTime, string CacheState);

    /// <summary>拉更新页：加时间戳绕缓存，并判断拿到的是不是 CDN 缓存副本。</summary>
    private async Task<UpdatePage?> FetchUpdatePageAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
            MaxAge = TimeSpan.Zero
        };
        http.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var url = VersionUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + attempt;
            using var resp = await http.GetAsync(url);
            resp.EnsureSuccessStatusCode();

            var html = await resp.Content.ReadAsStringAsync();
            var serverTime = resp.Headers.Date ?? DateTimeOffset.UtcNow;
            var xcache = resp.Headers.TryGetValues("X-Cache", out var cacheValues)
                ? string.Join(",", cacheValues)
                : "";
            var age = resp.Headers.Age?.TotalSeconds ?? 0;

            var cached = xcache.Contains("HIT", StringComparison.OrdinalIgnoreCase) || age > 60;
            if (resp.Headers.TryGetValues("X-Swift-CacheTime", out var swiftValues)
                && int.TryParse(swiftValues.FirstOrDefault(), out var swiftSeconds)
                && swiftSeconds > 60)
            {
                cached = true;
            }

            if (!cached) return new UpdatePage(html, serverTime, xcache);

            Log.Warn($"更新检查：疑似 CDN 缓存（X-Cache={xcache}, Age={age:F0}s），重试一次");
        }

        return null;
    }

    private async Task<string?> CheckUpdatesAsync(bool silent)
    {
        try
        {
            var page = await FetchUpdatePageAsync();
            if (page is null)
            {
                TxtUpdateStatus.Text = "拿到的是缓存页面，已跳过本次检查（请稍后重试）。";
                return null;
            }

            // ① 服务器时间：和本地时间对一下，顺便证明服务器活着、不是旧缓存
            var serverLocal = page.ServerTime.ToOffset(TimeSpan.FromHours(8));
            var skew = (DateTimeOffset.UtcNow - page.ServerTime).Duration();
            Log.Info($"更新检查：服务器时间 {serverLocal:yyyy-MM-dd HH:mm:ss}，本地时间 {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}，偏差 {skew.TotalSeconds:F0}s，X-Cache={page.CacheState}");
            if (skew > TimeSpan.FromMinutes(30))
            {
                TxtUpdateStatus.Text = $"服务器时间对不上（差 {skew.TotalMinutes:F0} 分钟），可能是缓存页或服务器异常，已跳过。";
                return null;
            }

            var html = page.Html;

            // ③ 校验语：服务器内容是否被篡改
            var poem = Regex.Match(html, @"<div id=""poem"">\s*(?<p>[^<]+)\s*</div>");
            if (!poem.Success || !poem.Groups["p"].Value.Contains(ExpectedPoem))
            {
                TxtUpdateStatus.Text = "校验失败：校验语不对，返回内容可能被篡改，已停止更新检查。";
                return null;
            }

            // ② 版本号正文
            var version = Regex.Match(html, @"<div id=""version"">\s*(?<v>[^<]+)\s*</div>");
            if (!version.Success)
            {
                if (!silent) TxtUpdateStatus.Text = "没有解析到版本号。";
                return null;
            }

            var remote = version.Groups["v"].Value.Trim();
            if (!Regex.IsMatch(remote, @"^\d+\.\d+(\.\d+)?$"))
            {
                TxtUpdateStatus.Text = $"版本号格式异常（{remote}），已停止更新检查。";
                return null;
            }

            _lastUpdateCheckUtc = DateTime.UtcNow;
            var stamp = serverLocal.ToString("HH:mm");
            if (Version.TryParse(remote, out var rv) && Version.TryParse(CurrentVersion, out var cv) && rv > cv)
            {
                TxtUpdateStatus.Text = $"发现新版本 {remote}（当前 {CurrentVersion}）";
                return remote;
            }

            TxtUpdateStatus.Text = $"电脑版 {CurrentVersion} · 已是最新（服务器 {stamp}）";
            return null;
        }
        catch (Exception ex)
        {
            if (!silent) TxtUpdateStatus.Text = "检查更新失败：" + ex.Message;
            Log.Warn("检查更新失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>启动时静默检查，有新版就弹窗；同一版本只弹一次。</summary>
    private async Task CheckUpdatesOnStartupAsync()
    {
        await Task.Delay(900);
        var remote = await CheckUpdatesAsync(silent: true);
        if (string.IsNullOrEmpty(remote)) return;
        await PromptUpdateAsync(remote, force: false);
    }

    /// <summary>从托盘回到窗口时，半小时最多查一次。</summary>
    private async Task CheckUpdatesOnShowAsync()
    {
        if (DateTime.UtcNow - _lastUpdateCheckUtc < TimeSpan.FromMinutes(30)) return;
        var remote = await CheckUpdatesAsync(silent: true);
        if (string.IsNullOrEmpty(remote)) return;
        await PromptUpdateAsync(remote, force: false);
    }

    private async Task PromptUpdateAsync(string remote, bool force)
    {
        if (!force && string.Equals(_settings.LastUpdatePrompt, remote, StringComparison.OrdinalIgnoreCase)) return;

        // 别在别的弹窗上再压一层
        if (DialogOverlay.IsVisible)
        {
            await Task.Delay(1200);
            if (DialogOverlay.IsVisible) return;
        }

        _settings.LastUpdatePrompt = remote;
        SettingsStore.Save();

        var go = await ConfirmAsync(
            "发现新版本 " + remote,
            $"当前版本 {CurrentVersion}，新版本 {remote} 已经发布了。\n\n要打开下载页面看看吗？",
            "去下载", "以后再说");
        if (go) OpenUrl(RepoUrl + "/releases");
    }

    private async void BtnCheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        var remote = await CheckUpdatesAsync(silent: false);
        if (string.IsNullOrEmpty(remote)) return;
        await PromptUpdateAsync(remote, force: true);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("打开链接失败：" + ex.Message); }
    }

    private void BtnOpenGithub_Click(object? sender, RoutedEventArgs e) => OpenUrl(RepoUrl);

    private const string AuthorUrl = "https://miqwq.com";

    private void BtnOpenAuthor_Click(object? sender, RoutedEventArgs e) => OpenUrl(AuthorUrl);

    private void ShowInfo(string title, string body)
    {
        TxtInfoTitle.Text = title;
        TxtInfoBody.Text = body;
        InfoOverlay.IsVisible = true;
    }

    private void BtnInfoClose_Click(object? sender, RoutedEventArgs e) => InfoOverlay.IsVisible = false;

    private void ShowKeyboardHelp_Click(object? sender, RoutedEventArgs e)
    {
        ShowInfo("键盘帮助",
            "为什么投屏里打字没反应？\n\n" +
            "Windows 的中文输入法会先把字母截走去组词，投屏引擎根本收不到按键，这跟注入方式无关。\n\n" +
            "正确姿势：\n" +
            "1. 打字前按 Shift 或切到 ENG，让电脑处于英文输入状态；\n" +
            "2. 想打中文，就在手机端用手机自己的输入法；\n" +
            "3. 部分国产 ROM 会拦截 SDK 注入的按键，这时在首页打开「UHID 键盘」——投屏引擎会向手机伪装成一把真实插入的物理键盘，绕过检测。\n\n" +
            "这一版投屏时会自动把 scrcpy 窗口切到 US 布局，退出后还原，正常情况下你不需要手动改系统输入法。");
    }

    private void ShowTutorial_Click(object? sender, RoutedEventArgs e)
    {
        ShowInfo("新手教程",
            "第一次用，按这个顺序来：\n\n" +
            "1. 手机：设置 → 关于手机 → 连点版本号进入开发者选项，打开「USB 调试」；\n" +
            "2. 数据线连电脑，手机上弹出「允许 USB 调试」时勾选「始终允许」；\n" +
            "3. 无线连接：手机打开「无线调试」→「使用配对码配对设备」，点电脑顶栏的设备胶囊，在浮层里填 IP、配对端口和 6 位配对码；\n" +
            "4. 首次连接会提示安装手机助手（约 200KB）。装完后按提示授予「通知使用权」和「读取短信」，不授权也能用投屏和文件；\n" +
            "5. 首页点「开始投屏」；在应用页点任意图标，会自动启动该应用并拉起投屏窗口。\n\n" +
            "文件页支持拖拽上传、双击下载，中文文件名已按完整路径传输，不会再被截断。");
    }

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
