using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LengJiaoConnect.Models;
using LengJiaoConnect.Services;

namespace LengJiaoConnect;

public partial class MainWindow : SukiUI.Controls.SukiWindow
{
    private readonly AppSettings _settings = SettingsStore.Current;
    private readonly ScrcpyService _scrcpy = new();
    private readonly ObservableCollection<AppInfo> _apps = new();
    private readonly ObservableCollection<AppInfo> _recent = new();
    private readonly ObservableCollection<SmsItem> _sms = new();
    private readonly ObservableCollection<AndroidFile> _files = new();

    private HelperClient? _helper;
    private DeviceInfo? _device;
    private string _serial = "";
    private DispatcherTimer? _deviceTimer;
    private DispatcherTimer? _mediaTimer;
    private TrayIcon? _tray;
    private bool _realExit;
    private bool _uiReady;
    private bool _mediaPlaying;
    private bool _polling;
    private DateTime _lastReconnect = DateTime.MinValue;
    private string? _lastClip;
    private TaskCompletionSource<bool?>? _dialogTcs;

    public MainWindow()
    {
        InitializeComponent();

        ListApps.ItemsSource = _apps;
        ListRecent.ItemsSource = _recent;
        ListSms.ItemsSource = _sms;
        ListFiles.ItemsSource = _files;

        _scrcpy.StatusChanged += OnCastStatus;

        Opened += OnOpened;
        Closing += OnClosing;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        LoadSettingsUi();
        _uiReady = true;

        // 调试/截图用：LJC_PAGE=files 之类，启动后直接落到指定页
        var startPage = Environment.GetEnvironmentVariable("LJC_PAGE");
        if (!string.IsNullOrWhiteSpace(startPage))
        {
            RadioButton? nav = startPage.ToLowerInvariant() switch
            {
                "files" => NavFiles,
                "apps" => NavApps,
                "sms" => NavSms,
                "settings" => NavSettings,
                _ => NavHome
            };
            if (nav is not null) nav.IsChecked = true;
        }

        TxtPageSub.Text = "设备、投屏与高级互联";
        TxtDropHint.Text = "会传到 " + CurrentRemotePath;
    }

    // ───────────────────────── 生命周期 ─────────────────────────

    private async void OnOpened(object? sender, EventArgs e)
    {
        CreateTray();
        await PollDevicesAsync();
        if (Environment.GetEnvironmentVariable("LJC_POPOVER") == "1") ToggleDevicePopover(true);
        if (Environment.GetEnvironmentVariable("LJC_PAIR") == "1")
        {
            ToggleDevicePopover(true);
            BtnPopoverPairToggle_Click(null, new Avalonia.Interactivity.RoutedEventArgs());
        }

        _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _deviceTimer.Tick += async (_, _) => await PollDevicesAsync();
        _deviceTimer.Start();

        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _mediaTimer.Tick += async (_, _) =>
        {
            if (_helper is { IsConnected: true }) await _helper.RequestMediaAsync();
        };
        _mediaTimer.Start();

        StartPerfTimer();
        Log.Info($"GLASS actual={ActualTransparencyLevel} hint={string.Join(",", TransparencyLevelHint ?? new List<WindowTransparencyLevel>())}");
        Dispatcher.UIThread.Post(() => Log.Info($"LAYOUT title={TxtPageTitle.Bounds} sub={TxtPageSub.Bounds} stack={((Control)TxtPageTitle.Parent!).Bounds}"), DispatcherPriority.Background);

        _ = CheckUpdatesAsync(silent: true);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_realExit) return;
        e.Cancel = true;
        Hide();
        Toast("已最小化到托盘，右键托盘图标可完全退出");
    }

    private void CreateTray()
    {
        try
        {
            _tray = new TrayIcon
            {
                ToolTipText = "棱角互联",
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://LengJiaoConnect/Assets/app.ico"))),
                IsVisible = true
            };

            var menu = new NativeMenu();
            var show = new NativeMenuItem("显示主界面");
            show.Click += (_, _) => RestoreFromTray();
            var exit = new NativeMenuItem("完全退出");
            exit.Click += (_, _) => RealExit();
            menu.Add(show);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(exit);

            _tray.Menu = menu;
            _tray.Clicked += (_, _) => RestoreFromTray();
            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
        }
        catch (Exception ex)
        {
            Log.Warn("托盘初始化失败：" + ex.Message);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void RealExit()
    {
        _realExit = true;
        Cleanup();
        Close();
        Environment.Exit(0);
    }

    private void Cleanup()
    {
        _deviceTimer?.Stop();
        _mediaTimer?.Stop();
        _perfTimer?.Stop();
        _scrcpy.Dispose();
        DetachHelper();
        SettingsStore.Save();
        if (_tray != null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
            _tray = null;
        }
    }

    // ───────────────────────── 导航 ─────────────────────────

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        BeginMoveDrag(e);
    }

    private void BtnMin_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMax_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();
    private void Nav_Checked(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady || sender is not RadioButton rb || rb.IsChecked != true) return;

        var tag = rb.Tag as string ?? "home";
        PageHome.IsVisible = tag == "home";
        PageFiles.IsVisible = tag == "files";
        PageApps.IsVisible = tag == "apps";
        PageSms.IsVisible = tag == "sms";
        PageSettings.IsVisible = tag == "settings";

        Control page = tag switch
        {
            "files" => PageFiles,
            "apps" => PageApps,
            "sms" => PageSms,
            "settings" => PageSettings,
            _ => PageHome
        };
        AnimateIn(page, 14);

        (TxtPageTitle.Text, TxtPageSub.Text) = tag switch
        {
            "files" => ("文件", "浏览、上传与下载手机文件"),
            "apps" => ("应用", "启动应用、安装 APK"),
            "sms" => ("短信", "最近的 50 条短信"),
            "settings" => ("设置", "连接、高级互联与关于"),
            _ => ("概览", "设备、投屏与高级互联")
        };

        if (tag == "files" && _files.Count == 0 && !string.IsNullOrEmpty(_serial))
            _ = LoadDirectoryAsync(CurrentRemotePath);
        if (tag == "apps" && _apps.Count == 0 && _helper is { IsConnected: true })
            _ = RequestAppsAsync();
        if (tag == "sms" && _sms.Count == 0 && _helper is { IsConnected: true })
            _ = RequestSmsAsync();
    }

    // 统一的入场动效：淡入 + 轻微上移，曲线用的是 Material 的 emphasized decelerate
    private static void AnimateIn(Control c, double dy = 10, int ms = 260)
    {
        c.Opacity = 0;
        c.RenderTransform = TransformOperations.Parse("translateY(" + dy.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px)");
        c.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(ms), Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(ms), Easing = new CubicEaseOut() }
        };
        Dispatcher.UIThread.Post(() =>
        {
            c.RenderTransform = TransformOperations.Parse("translateY(0px)");
            c.Opacity = 1;
        }, DispatcherPriority.Render);
    }

    private void GoPage(string tag)
    {
        foreach (var rb in new[] { NavHome, NavFiles, NavApps, NavSms, NavSettings })
            rb.IsChecked = (rb.Tag as string) == tag;
    }

    private void BtnQuickApps_Click(object? sender, RoutedEventArgs e) => GoPage("apps");
    private void BtnQuickSms_Click(object? sender, RoutedEventArgs e) => GoPage("sms");
    private void BtnQuickFiles_Click(object? sender, RoutedEventArgs e) => GoPage("files");
    private void BtnQuickSettings_Click(object? sender, RoutedEventArgs e) => GoPage("settings");

    // ───────────────────────── 设备 ─────────────────────────

    private async Task PollDevicesAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var list = await Adb.GetDevicesAsync();
            // 多台在线时，保持用户手动选中的那台，不要被“第一台”抢回去
            var online = list.FirstOrDefault(d => d.IsOnline && d.Serial == _serial)
                         ?? list.FirstOrDefault(d => d.IsOnline);

            if (online == null)
            {
                if (_serial.Length > 0)
                {
                    DetachHelper();
                    _serial = "";
                    _device = null;
                    ApplyDeviceUi(null);
                }

                if (_settings.AutoReconnect && !string.IsNullOrWhiteSpace(_settings.LastEndpoint) &&
                    (DateTime.Now - _lastReconnect).TotalSeconds > 10)
                {
                    _lastReconnect = DateTime.Now;
                    await ReconnectLastAsync();
                }
                return;
            }

            if (online.Serial != _serial)
            {
                await SetActiveDeviceAsync(online);
            }
            else
            {
                _device = online;
                ApplyDeviceUi(online);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("轮询设备失败：" + ex.Message);
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task ReconnectLastAsync()
    {
        try
        {
            var r = await Adb.ConnectAsync(_settings.LastEndpoint);
            if (r.Best.Contains("connected", StringComparison.OrdinalIgnoreCase))
                Toast("正在重连 " + _settings.LastEndpoint);
        }
        catch (Exception ex)
        {
            Log.Warn("自动重连失败：" + ex.Message);
        }
    }

    private async Task SetActiveDeviceAsync(DeviceInfo device)
    {
        DetachHelper();
        _device = device;
        _serial = device.Serial;
        ApplyDeviceUi(device);

        if (device.IsWireless)
        {
            _settings.LastEndpoint = device.Serial;
            SettingsStore.Save();
            TxtLastEndpoint.Text = device.Serial;
        }

        _helper = new HelperClient(device.Serial);
        WireHelperEvents();

        await UpdateDeviceStatusAsync(device.Serial);
        await RefreshHelperStatusAsync();
        _ = MaybeOfferHelperAsync();

        // 连上设备就先拉一次目录，文件页打开时不再空白
        if (_files.Count == 0)
        {
            Log.Info("拉取目录：" + CurrentRemotePath);
            _ = LoadDirectoryAsync(CurrentRemotePath);
        }

        Log.Info("开始连接 helper…");
        var ok = await _helper.ConnectAsync();
        Log.Info("helper 连接结果：" + ok);
        UpdateHomeHelper();
        if (ok)
        {
            try
            {
                await _helper.CheckPermissionAsync();
                if (_settings.SyncApps) await RequestAppsAsync();
                if (_settings.SyncSms) await RequestSmsAsync();
                await _helper.RequestMediaAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("初始化同步失败：" + ex);
            }
        }
        else Log.Warn("helper 连接失败：" + device.Serial);
    }

    private void DetachHelper()
    {
        if (_helper != null)
        {
            _helper.Dispose();
            _helper = null;
        }
        _apps.Clear();
        _recent.Clear();
        _sms.Clear();
        PanelAppsEmpty.IsVisible = true;
        PanelSmsEmpty.IsVisible = true;
        PanelSmsDetail.IsVisible = false;
    }

    private void ApplyDeviceUi(DeviceInfo? d)
    {
        var online = d?.IsOnline == true;
        var brush = new SolidColorBrush(Color.Parse(online ? "#6FD3FF" : "#6B7A77"));
        DotTop.Fill = brush;
        SideDot.Fill = brush;

        TxtTopDevice.Text = d?.DisplayName ?? "未连接";
        TxtSideDevice.Text = d?.DisplayName ?? "未连接设备";
        TxtSideHint.Text = online ? (d!.IsWireless ? "无线调试" : "USB 连接") : "USB 或无线调试";
        TxtDevName.Text = d?.DisplayName ?? "未连接设备";
        TxtDevSerial.Text = d?.Serial ?? "插入 USB 或连接无线调试";
        TxtDevState.Text = online ? (d!.IsWireless ? "WiFi 连接" : "USB 连接") : (d?.StateText ?? "离线");
        TxtPageSub.Text = online ? "设备已就绪" : "等待设备连接";

        BtnCast.IsEnabled = online;
        BtnStopCast.IsEnabled = online;
        BtnDisconnect.IsEnabled = online;

        if (!online)
        {
            TxtDevBattery.Text = "电量 —";
            TxtDevWifi.Text = "WiFi —";
            TxtDevBt.Text = "蓝牙 —";
            TxtTopMeta.Text = "";
        }
    }

    private async Task UpdateDeviceStatusAsync(string serial)
    {
        try
        {
            var batteryTask = Adb.ShellTextAsync(serial, "dumpsys battery | grep -E 'level|status'");
            var wifiTask = Adb.ShellTextAsync(serial, "ip addr show wlan0 | grep 'inet '");
            var btTask = Adb.ShellTextAsync(serial, "settings get global bluetooth_on");
            var rssiTask = Adb.ShellTextAsync(serial, "dumpsys wifi | grep -m1 'RSSI:'");
            var ssidTask = Adb.ShellTextAsync(serial, "cmd wifi status | grep -m1 'SSID:'");
            await Task.WhenAll(batteryTask, wifiTask, btTask, rssiTask, ssidTask);

            var level = System.Text.RegularExpressions.Regex.Match(batteryTask.Result, @"level:\s*(\d+)");
            var status = System.Text.RegularExpressions.Regex.Match(batteryTask.Result, @"status:\s*(\d+)");
            double? pct = level.Success ? double.Parse(level.Groups[1].Value) : null;
            var charging = status.Success && (status.Groups[1].Value == "2" || status.Groups[1].Value == "5");

            var connected = wifiTask.Result.Contains("inet ");
            var rssi = System.Text.RegularExpressions.Regex.Match(rssiTask.Result, @"RSSI:\s*(-?\d+)");
            var wifiLevel = !connected ? 0
                : !rssi.Success ? 3
                : int.Parse(rssi.Groups[1].Value) >= -55 ? 3
                : int.Parse(rssi.Groups[1].Value) >= -67 ? 2 : 1;

            var ssidMatch = System.Text.RegularExpressions.Regex.Match(ssidTask.Result, "SSID:\\s*\"?([^\",]+)\"?");
            var ssid = ssidMatch.Success ? ssidMatch.Groups[1].Value.Trim() : "";

            var btOn = btTask.Result.Trim() == "1";

            SetBatteryVisual(pct, charging);
            SetWifiVisual(connected, wifiLevel, ssid);
            SetBtVisual(btOn);
        }
        catch (Exception ex)
        {
            Log.Warn("读取设备状态失败：" + ex.Message);
        }
    }

    private void SetBatteryVisual(double? pct, bool charging)
    {
        if (pct is null)
        {
            BarBattery.Width = 0; TopBatteryBar.Width = 0;
            TxtDevBattery.Text = "—"; TxtTopMeta.Text = "";
            return;
        }

        var p = Math.Clamp(pct.Value, 0, 100);
        var color = p <= 15 ? Color.Parse("#FF6B6B") : p <= 35 ? Color.Parse("#FFD166") : Color.Parse("#6FD3FF");
        var brush = new SolidColorBrush(color);
        BarBattery.Background = brush;
        BarBattery.Width = Math.Max(2, 20 * p / 100);
        TopBatteryBar.Background = brush;
        TopBatteryBar.Width = Math.Max(1.5, 13 * p / 100);

        TxtDevBattery.Text = p.ToString("0") + "%" + (charging ? " ↑" : "");
        TxtTopMeta.Text = p.ToString("0") + "%";
    }

    private void SetWifiVisual(bool connected, int level, string ssid)
    {
        WifiArc1.IsVisible = connected && level >= 1;
        WifiArc2.IsVisible = connected && level >= 2;
        WifiArc3.IsVisible = connected && level >= 3;
        WifiSlash.IsVisible = !connected;
        TxtDevWifi.Text = connected ? (string.IsNullOrWhiteSpace(ssid) ? "已连接" : ssid) : "未连接";

        TopWifiArc1.IsVisible = connected && level >= 1;
        TopWifiArc2.IsVisible = connected && level >= 2;
        TopWifiArc3.IsVisible = connected && level >= 3;
        TopWifiSlash.IsVisible = !connected;
    }

    private void SetBtVisual(bool on)
    {
        IcoBt.Fill = new SolidColorBrush(Color.Parse(on ? "#6FD3FF" : "#4A5A6B"));
        BtSlash.IsVisible = !on;
        TxtDevBt.Text = on ? "已开启" : "已关闭";
        TopBt.Fill = new SolidColorBrush(Color.Parse(on ? "#6FD3FF" : "#4A5A6B"));
        TopBtSlash.IsVisible = !on;
    }
    private async void BtnDisconnect_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_serial)) return;
        if (!await ConfirmAsync("断开连接", "确定要断开 " + _serial + " 吗？", "断开", "取消")) return;

        try
        {
            if (_device?.IsWireless == true) await Adb.DisconnectAsync(_serial);
        }
        catch (Exception ex)
        {
            Log.Warn("断开失败：" + ex.Message);
        }
        DetachHelper();
        _serial = "";
        _device = null;
        ApplyDeviceUi(null);
        Toast("已断开");
    }

    /// <summary>取当前设备的 wlan0 IP，用于预填配对/连接地址。</summary>
    private async Task<string> GetDeviceWlanIpAsync()
    {
        if (string.IsNullOrEmpty(_serial)) return "";
        try
        {
            var ip = await Adb.ShellTextAsync(_serial, "ip route | awk '/wlan0/ {print $9; exit}'");
            return ip.Trim();
        }
        catch (Exception ex)
        {
            Log.Warn("读取设备 IP 失败：" + ex.Message);
            return "";
        }
    }

    // ───────────────────────── 投屏 ─────────────────────────

    private async void BtnCast_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }
        BtnCast.IsEnabled = false;
        try
        {
            await _scrcpy.StartAsync(_serial, _settings);
        }
        finally
        {
            BtnCast.IsEnabled = true;
        }
    }

    private void BtnStopCast_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_serial)) _scrcpy.Stop(_serial);
    }

    private void OnCastStatus(string serial, string message, bool error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TxtCastStatus.Text = message;
            TxtCastStatus.Foreground = new SolidColorBrush(Color.Parse(error ? "#FFB4AB" : "#8FA8A4"));
            if (error) Toast(message, true);
        });
    }

    // ───────────────────────── 高级互联 ─────────────────────────

    private void OnClipboardReceived(string text)
    {
        if (!_settings.SyncClipboard || string.IsNullOrEmpty(text)) return;
        if (text == _lastClip) return;
        _lastClip = text;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.CreateText(text));
                await Clipboard!.SetDataAsync(transfer);
            }
            catch (Exception ex) { Log.Warn("写剪贴板失败：" + ex.Message); }
        });
    }

    private void OnNotificationReceived(string app, string title, string text)
    {
        if (!_settings.SyncNotifications) return;
        var body = string.IsNullOrWhiteSpace(text) ? title : title + " · " + text;
        Dispatcher.UIThread.Post(() =>
        {
            Toast(app + "：" + body);
            ToastWindow.Show(app, string.IsNullOrWhiteSpace(title) ? "新通知" : title, text ?? "");
        });
    }

    private void OnPermissionChanged(bool sms, bool notif)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _helperSms = sms;
            _helperNotif = notif;
            TxtHelperStatus.Text = $"助手{(_helper?.IsConnected == true ? "已连接" : "未连接")} · 短信{(sms ? "已授权" : "未授权")} · 通知{(notif ? "已授权" : "未授权")}";
            UpdateHomeHelper();
        });
    }

    private async Task RequestAppsAsync()
    {
        if (_helper is not { IsConnected: true }) return;
        TxtAppsCount.Text = "同步中…";
        await _helper.RequestAppsAsync();
    }

    private async Task RequestSmsAsync()
    {
        if (_helper is not { IsConnected: true }) return;
        await _helper.RequestSmsAsync();
    }

    // ───────────────────────── UI 工具 ─────────────────────────

    private void Toast(string message, bool error = false)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse(error ? "#3A1618" : "#17222F")),
            BorderBrush = new SolidColorBrush(Color.Parse(error ? "#FF6B6B" : "#2A3A37")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10),
            MaxWidth = 460
        };
        border.Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        border.Opacity = 0;
        border.RenderTransform = TransformOperations.Parse("translateY(10px)");
        border.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180), Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut() }
        };
        ToastStack.Children.Add(border);
        Dispatcher.UIThread.Post(() =>
        {
            border.Opacity = 1;
            border.RenderTransform = TransformOperations.Parse("translateY(0px)");
        }, DispatcherPriority.Background);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            border.Opacity = 0;
            border.RenderTransform = TransformOperations.Parse("translateY(-6px)");
            var gone = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            gone.Tick += (_, _) => { gone.Stop(); ToastStack.Children.Remove(border); };
            gone.Start();
        };
        timer.Start();
    }

    private Task<bool> ConfirmAsync(string title, string body, string ok = "确定", string cancel = "取消")
    {
        DialogTitle.Text = title;
        DialogBody.Text = body;
        DialogInput.IsVisible = false;
        BtnDialogOk.Content = ok;
        BtnDialogCancel.Content = cancel;
        return ShowDialogAsync(false);
    }

    private Task<string?> PromptAsync(string title, string body, string initial = "")
    {
        DialogTitle.Text = title;
        DialogBody.Text = body;
        DialogInput.IsVisible = true;
        DialogInput.Text = initial;
        BtnDialogOk.Content = "确定";
        BtnDialogCancel.Content = "取消";
        var tcs = new TaskCompletionSource<bool?>();
        _dialogTcs = tcs;
        DialogOverlay.IsVisible = true;
        DialogInput.Focus();
        return AwaitPrompt(tcs);
    }

    private async Task<string?> AwaitPrompt(TaskCompletionSource<bool?> tcs)
    {
        var ok = await tcs.Task;
        return ok == true ? DialogInput.Text?.Trim() : null;
    }

    private Task<bool> ShowDialogAsync(bool _)
    {
        var tcs = new TaskCompletionSource<bool?>();
        _dialogTcs = tcs;
        DialogOverlay.IsVisible = true;
        return AwaitBool(tcs);
    }

    private async Task<bool> AwaitBool(TaskCompletionSource<bool?> tcs) => await tcs.Task == true;

    private void BtnDialogOk_Click(object? sender, RoutedEventArgs e)
    {
        DialogOverlay.IsVisible = false;
        _dialogTcs?.TrySetResult(true);
        _dialogTcs = null;
    }

    private void BtnDialogCancel_Click(object? sender, RoutedEventArgs e)
    {
        DialogOverlay.IsVisible = false;
        _dialogTcs?.TrySetResult(false);
        _dialogTcs = null;
    }

    private void Busy(string text, bool on)
    {
        BusyOverlay.IsVisible = on;
        TxtBusy.Text = text;
    }

    // ───────────────────────── 拖拽 ─────────────────────────

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles()?.ToList() ?? new();
        if (files.Count == 0) return;

        e.DragEffects = DragDropEffects.Copy;
        DropOverlay.IsVisible = true;
        var apkOnly = files.All(f => f.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        TxtDropHint.Text = apkOnly ? "松手安装 APK" : "松手上传到 " + CurrentRemotePath;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => DropOverlay.IsVisible = false;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        var items = e.DataTransfer?.TryGetFiles()?.ToList();
        if (items == null || items.Count == 0) return;

        var paths = items.Select(i => i.TryGetLocalPath()).Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().ToList();
        if (paths.Count == 0) return;

        if (paths.All(p => p.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var apk in paths) await InstallApkAsync(apk);
            return;
        }

        GoPage("files");
        await UploadFilesAsync(paths);
    }

    // ───────────────────────── 设置（连接/剪贴板等） ─────────────────────────

    private async void BtnClearLast_Click(object? sender, RoutedEventArgs e)
    {
        _settings.LastEndpoint = "";
        SettingsStore.Save();
        TxtLastEndpoint.Text = "上次设备：—";
        Toast("已清除上次设备");
        await Task.CompletedTask;
    }

    private void OnMediaChanged(MediaInfo media)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TxtMediaTitle.Text = string.IsNullOrWhiteSpace(media.Title) ? "没有正在播放的媒体" : media.Title;
            TxtMediaArtist.Text = media.Artist;
            _mediaPlaying = media.Playing;
            IconPlay.IsVisible = !_mediaPlaying;
            IconPause.IsVisible = _mediaPlaying;
        });
    }

    private async void BtnMediaPrev_Click(object? sender, RoutedEventArgs e)
    {
        if (_helper is { IsConnected: true }) await _helper.MediaCommandAsync("prev");
    }

    private async void BtnMediaPlay_Click(object? sender, RoutedEventArgs e)
    {
        if (_helper is not { IsConnected: true }) return;
        await _helper.MediaCommandAsync(_mediaPlaying ? "pause" : "play");
        await Task.Delay(300);
        await _helper.RequestMediaAsync();
    }

    private async void BtnMediaNext_Click(object? sender, RoutedEventArgs e)
    {
        if (_helper is { IsConnected: true }) await _helper.MediaCommandAsync("next");
    }
}
