using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LengJiaoConnect.Models;
using LengJiaoConnect.Services;

namespace LengJiaoConnect;

public partial class MainWindow : SukiUI.Controls.SukiWindow
{
    private const uint WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private static readonly IBrush BrushOnline = new SolidColorBrush(Color.Parse("#6FD3FF"));
    private static readonly IBrush BrushOffline = new SolidColorBrush(Color.Parse("#64758A"));
    private static readonly IBrush BrushWarn = new SolidColorBrush(Color.Parse("#FFD166"));
    private static readonly IBrush BrushDanger = new SolidColorBrush(Color.Parse("#FF6B6B"));
    private static readonly IBrush BrushSuccess = new SolidColorBrush(Color.Parse("#56D364"));
    private static readonly IBrush BrushDim = new SolidColorBrush(Color.Parse("#8FA8A4"));

    private readonly AppSettings _settings = SettingsStore.Current;
    private readonly ScrcpyService _scrcpy = new();
    private readonly ObservableCollection<AppInfo> _apps = new();
    private readonly ObservableCollection<AppInfo> _visibleApps = new();
    private readonly ObservableCollection<AppInfo> _recent = new();
    private readonly ObservableCollection<SmsItem> _sms = new();
    private readonly ObservableCollection<AndroidFile> _files = new();
    private readonly ObservableCollection<BreadcrumbSegment> _breadcrumbs = new();

    private readonly SemaphoreSlim _deviceSwitchGate = new(1, 1);
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    private HelperClient? _helper;
    private DeviceInfo? _device;
    private string _serial = "";
    private int _deviceEpoch;
    private string _currentPageTag = "home";
    private string? _suppressedSerial;
    private bool _suppressAutoReconnect;

    private DispatcherTimer? _deviceTimer;
    private DispatcherTimer? _mediaTimer;
    private DispatcherTimer? _statusTimer;
    private TrayIcon? _tray;
    private IntPtr _clipboardHwnd;
    private bool _openedOnce;
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

        ListApps.ItemsSource = _visibleApps;
        ListRecent.ItemsSource = _recent;
        ListSms.ItemsSource = _sms;
        ListFiles.ItemsSource = _files;
        ListBreadcrumbs.ItemsSource = _breadcrumbs;

        _scrcpy.StatusChanged += OnCastStatus;

        Opened += OnOpened;
        Closing += OnClosing;
        KeyDown += MainWindow_KeyDown;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        LoadLaunchCounts();
        LoadSettingsUi();
        RebuildBreadcrumbs(CurrentRemotePath);
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
        // Avalonia 在 Hide() -> Show() 恢复窗口时会再次触发 Opened，必须做一次性保护，
        // 避免重复创建托盘图标与轮询定时器造成泄漏。
        if (_openedOnce) return;
        _openedOnce = true;

        CreateTray();
        StartClipboardWatcher();
        await PollDevicesAsync();

        if (Environment.GetEnvironmentVariable("LJC_POPOVER") == "1") ToggleDevicePopover(true);
        if (Environment.GetEnvironmentVariable("LJC_PAIR") == "1")
        {
            ToggleDevicePopover(true);
            BtnPopoverPairToggle_Click(null, new RoutedEventArgs());
        }

        _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _deviceTimer.Tick += async (_, _) => await PollDevicesAsync();
        _deviceTimer.Start();

        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _mediaTimer.Tick += async (_, _) =>
        {
            if (_helper is { IsConnected: true } && IsVisible && WindowState != WindowState.Minimized)
                await _helper.RequestMediaAsync();
        };
        _mediaTimer.Start();

        // 定期刷新设备状态（WiFi 信号/SSID、蓝牙、充电态等）；若外部新装了助手也自动接上
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _statusTimer.Tick += async (_, _) =>
        {
            var serial = _serial;
            var epoch = _deviceEpoch;
            if (string.IsNullOrEmpty(serial)) return;
            if (IsVisible && WindowState != WindowState.Minimized)
                await UpdateDeviceStatusAsync(serial, epoch);
            if (!_helperInstalled && await Adb.IsInstalledAsync(serial, "com.lengjiao.helper"))
            {
                await RefreshHelperStatusAsync();
                if (_deviceEpoch == epoch && _helperInstalled && _helper is not { IsConnected: true })
                {
                    var helper = new HelperClient(serial);
                    _helper = helper;
                    WireHelperEvents(helper, epoch);
                    if (await helper.ConnectAsync())
                    {
                        await helper.CheckPermissionAsync();
                        if (_settings.SyncApps) await RequestAppsAsync();
                        if (_settings.SyncSms) await RequestSmsAsync();
                        await helper.RequestMediaAsync();
                        UpdateHomeHelper();
                    }
                }
            }
        };
        _statusTimer.Start();

        StartPerfTimer();
        _ = CheckUpdatesOnStartupAsync();
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
            if (_tray != null)
            {
                _tray.IsVisible = false;
                _tray.Dispose();
                _tray = null;
            }

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
        _ = CheckUpdatesOnShowAsync();
        if (!string.IsNullOrEmpty(_serial))
            _ = UpdateDeviceStatusAsync(_serial, _deviceEpoch);
    }

    private async void RealExit()
    {
        if (_realExit) return;
        _realExit = true;
        var serial = _serial;
        Cleanup();
        try
        {
            if (!string.IsNullOrEmpty(serial))
                await Task.WhenAny(HelperClient.RemoveForwardAsync(serial), Task.Delay(1200));
        }
        catch { }
        Close();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Environment.Exit(0);
    }

    private void Cleanup()
    {
        _deviceTimer?.Stop();
        _mediaTimer?.Stop();
        _statusTimer?.Stop();
        _perfTimer?.Stop();
        _smsFlushTimer?.Stop();
        _searchTimer?.Stop();
        StopClipboardWatcher();
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

    // ───────────────────────── 剪贴板监听（PC → 手机） ─────────────────────────

    private void StartClipboardWatcher()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;
            if (AddClipboardFormatListener(handle))
            {
                _clipboardHwnd = handle;
                Win32Properties.AddWndProcHookCallback(this, ClipboardWndProc);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("注册剪贴板监听失败：" + ex.Message);
        }
    }

    private void StopClipboardWatcher()
    {
        if (_clipboardHwnd == IntPtr.Zero) return;
        try
        {
            RemoveClipboardFormatListener(_clipboardHwnd);
            Win32Properties.RemoveWndProcHookCallback(this, ClipboardWndProc);
        }
        catch { }
        _clipboardHwnd = IntPtr.Zero;
    }

    private IntPtr ClipboardWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
            _ = PushLocalClipboardToPhoneAsync();
        return IntPtr.Zero;
    }

    private async Task PushLocalClipboardToPhoneAsync()
    {
        if (!_settings.SyncClipboard || _helper is not { IsConnected: true } || Clipboard is null) return;
        try
        {
            var text = await Clipboard.TryGetTextAsync();
            if (string.IsNullOrEmpty(text)) return;
            if (string.Equals(text, _lastClip, StringComparison.Ordinal)) return;
            _lastClip = text;
            await _helper.SendClipAsync(text);
        }
        catch (Exception ex)
        {
            Log.Warn("读取本机剪贴板失败：" + ex.Message);
        }
    }

    // ───────────────────────── 导航 & 快捷键 ─────────────────────────

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DialogOverlay.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                BtnDialogCancel_Click(null, new RoutedEventArgs());
            }
            else if (e.Key == Key.Enter && DialogInput.IsVisible)
            {
                e.Handled = true;
                BtnDialogOk_Click(null, new RoutedEventArgs());
            }
            return;
        }

        if (UpdateOverlay.IsVisible && e.Key == Key.Escape)
        {
            e.Handled = true;
            UpdateOverlay.IsVisible = false;
            return;
        }

        if (InfoOverlay.IsVisible && e.Key == Key.Escape)
        {
            e.Handled = true;
            InfoOverlay.IsVisible = false;
            return;
        }

        if (_devicePopoverOpen && e.Key == Key.Escape)
        {
            e.Handled = true;
            ToggleDevicePopover(false);
            return;
        }

        if (_currentPageTag == "files" && e.Source is not TextBox)
        {
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                _ = LoadDirectoryAsync(_currentPath);
            }
            else if (e.Key == Key.Back)
            {
                e.Handled = true;
                BtnUp_Click(null, new RoutedEventArgs());
            }
            else if (e.Key == Key.Delete)
            {
                e.Handled = true;
                BtnDelete_Click(null, new RoutedEventArgs());
            }
            else if (e.Key == Key.F2)
            {
                e.Handled = true;
                BtnRename_Click(null, new RoutedEventArgs());
            }
        }
    }

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
        _currentPageTag = tag;

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
        AnimateIn(page, 12, 220);

        (TxtPageTitle.Text, TxtPageSub.Text) = tag switch
        {
            "files" => ("文件", "浏览、上传、下载与 Root 文件管理"),
            "apps" => ("应用", "一键启动并投屏、安装 APK"),
            "sms" => ("短信", "最近 50 条收件箱短信与验证码提取"),
            "settings" => ("设置", "连接策略、高级互联与软件更新"),
            _ => ("概览", _device?.IsOnline == true ? "设备已就绪" : "设备、投屏与高级互联")
        };

        if (tag == "files" && _files.Count == 0 && !string.IsNullOrEmpty(_serial))
            _ = LoadDirectoryAsync(CurrentRemotePath);
        if (tag == "apps" && _apps.Count == 0 && _helper is { IsConnected: true })
            _ = RequestAppsAsync();
        if (tag == "sms" && _sms.Count == 0 && _helper is { IsConnected: true })
            _ = RequestSmsAsync();
    }

    // 统一的入场动效：淡入 + 轻微上移
    private static void AnimateIn(Control c, double dy = 10, int ms = 240)
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
            var (ok, list) = await Adb.TryGetDevicesAsync();
            // adb devices 偶发超时/出错时不能误判设备掉线，保留当前状态等待下次轮询。
            if (!ok) return;

            var onlineList = list.Where(d => d.IsOnline).ToList();
            // 如果用户主动断开了某台 USB 设备，只要该设备仍未拔掉且无其他设备，就保持断开直到用户主动重连或换设备。
            if (_suppressedSerial != null && !onlineList.Any(d => d.Serial == _suppressedSerial))
                _suppressedSerial = null;

            var online = onlineList.FirstOrDefault(d => d.Serial == _serial)
                         ?? onlineList.FirstOrDefault(d => d.Serial != _suppressedSerial);

            if (online == null)
            {
                if (_serial.Length > 0)
                {
                    DetachHelper();
                    _serial = "";
                    _device = null;
                    ApplyDeviceUi(null);
                }

                if (!_suppressAutoReconnect && _settings.AutoReconnect &&
                    !string.IsNullOrWhiteSpace(_settings.LastEndpoint) &&
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
        await _deviceSwitchGate.WaitAsync();
        try
        {
            if (string.Equals(_serial, device.Serial, StringComparison.Ordinal) && _device?.IsOnline == true)
            {
                _device = device;
                ApplyDeviceUi(device);
                return;
            }

            _suppressedSerial = null;
            _suppressAutoReconnect = false;
            var epoch = Interlocked.Increment(ref _deviceEpoch);

            DetachHelper();
            _device = device;
            _serial = device.Serial;
            ApplyDeviceUi(device);
            _ = LoadAppsCacheAsync(epoch);

            if (device.IsWireless)
            {
                _settings.LastEndpoint = device.Serial;
                SettingsStore.Save();
                UpdateLastEndpointUi();
            }

            await UpdateDeviceStatusAsync(device.Serial, epoch);
            if (_deviceEpoch != epoch) return;

            await RefreshHelperStatusAsync();
            if (_deviceEpoch != epoch) return;

            _ = LoadDirectoryAsync(CurrentRemotePath);

            // 只有已安装助手时才尝试建立 socket 通道，避免对未装助手的手机反复建隧道
            if (!_helperInstalled)
            {
                UpdateHomeHelper();
                _ = MaybeOfferHelperAsync();
                return;
            }

            var helper = new HelperClient(device.Serial);
            _helper = helper;
            WireHelperEvents(helper, epoch);

            Log.Info("开始连接 helper…");
            var ok = await helper.ConnectAsync();
            if (_deviceEpoch != epoch || !ReferenceEquals(_helper, helper)) return;

            Log.Info("helper 连接结果：" + ok);
            UpdateHomeHelper();
            if (ok)
            {
                try
                {
                    await helper.CheckPermissionAsync();
                    if (_settings.SyncApps) await RequestAppsAsync();
                    if (_settings.SyncSms) await RequestSmsAsync();
                    await helper.RequestMediaAsync();
                }
                catch (Exception ex)
                {
                    Log.Warn("初始化同步失败：" + ex);
                }
            }
            else
            {
                Log.Warn("helper 连接失败：" + device.Serial);
                _ = MaybeOfferHelperAsync();
            }
        }
        finally
        {
            _deviceSwitchGate.Release();
        }
    }

    private void DetachHelper()
    {
        if (_helper != null)
        {
            _helper.Dispose();
            _helper = null;
        }

        _smsFlushTimer?.Stop();
        lock (_tempSmsGate) _tempSms.Clear();
        lock (_tempAppsGate) _tempApps.Clear();

        foreach (var app in _apps) app.DisposeIcon();
        _apps.Clear();
        _visibleApps.Clear();
        _recent.Clear();
        _sms.Clear();
        _files.Clear();
        _currentPath = "/sdcard/";
        TxtPath.Text = _currentPath;
        RebuildBreadcrumbs(_currentPath);
        TxtFileStatus.Text = "";
        TxtFileSelection.IsVisible = false;
        TxtAppsCount.Text = "";
        TxtSmsCount.Text = "";
        TxtRecentLabel.IsVisible = false;
        ListRecent.IsVisible = false;

        PanelAppsEmpty.IsVisible = true;
        PanelSmsEmpty.IsVisible = true;
        PanelSmsDetail.IsVisible = false;
        PanelFilesEmpty.IsVisible = false;

        ResetPerfCounters();
        UpdateMediaUi(new MediaInfo());
        UpdateCastVisual();
    }

    private void ApplyDeviceUi(DeviceInfo? d)
    {
        var online = d?.IsOnline == true;
        var brush = online ? BrushOnline : BrushOffline;
        DotTop.Fill = brush;
        SideDot.Fill = brush;
        SideDotGlow.IsVisible = online;

        TxtTopDevice.Text = d?.DisplayName ?? "未连接";
        TxtSideDevice.Text = d?.DisplayName ?? "未连接设备";
        TxtSideHint.Text = online ? (d!.IsWireless ? "无线调试" : "USB 连接") : "USB 或无线调试";
        TxtDevName.Text = d?.DisplayName ?? "未连接设备";
        TxtDevSerial.Text = d?.Serial ?? "插入 USB 或连接无线调试";
        TxtDevState.Text = online ? (d!.IsWireless ? "WiFi 连接" : "USB 连接") : (d?.StateText ?? "离线");

        // 仅在概览页更新副标题，绝不覆盖文件/应用/短信/设置页的副标题
        if (_currentPageTag == "home")
            TxtPageSub.Text = online ? "设备已就绪" : "等待设备连接";

        BtnCast.IsEnabled = online;
        BtnStopCast.IsEnabled = online;
        BtnDisconnect.IsEnabled = online;
        BtnReboot.IsEnabled = online;
        UpdateCastVisual();

        if (!online)
        {
            BadgeDevAndroid.IsVisible = false;
            SetBatteryVisual(null, false);
            SetWifiVisual(false, 0, "");
            SetBtVisual(false);
            TxtDevWifi.Text = "—";
            TxtDevBt.Text = "—";
            UpdateHomeHelper();
        }
    }

    private async Task UpdateDeviceStatusAsync(string serial, int epoch)
    {
        try
        {
            var batteryTask = Adb.ShellTextAsync(serial, "dumpsys battery | grep -E 'level|status'");
            var wifiTask = Adb.ShellTextAsync(serial, "ip addr show wlan0 | grep 'inet '");
            var btTask = Adb.ShellTextAsync(serial, "settings get global bluetooth_on");
            var rssiTask = Adb.ShellTextAsync(serial, "dumpsys wifi | grep -m1 'RSSI:'");
            var ssidTask = Adb.ShellTextAsync(serial, "cmd wifi status | grep -m1 'SSID:'");
            var verTask = Adb.GetPropAsync(serial, "ro.build.version.release");
            await Task.WhenAll(batteryTask, wifiTask, btTask, rssiTask, ssidTask, verTask);

            if (_deviceEpoch != epoch || !string.Equals(_serial, serial, StringComparison.Ordinal)) return;

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
            var androidVer = verTask.Result.Trim();

            SetBatteryVisual(pct, charging);
            SetWifiVisual(connected, wifiLevel, ssid);
            SetBtVisual(btOn);

            if (!string.IsNullOrWhiteSpace(androidVer))
            {
                TxtDevAndroid.Text = "Android " + androidVer;
                BadgeDevAndroid.IsVisible = true;
            }
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
            BarBattery.Width = 0;
            TopBatteryBar.Width = 0;
            IconBatteryBolt.IsVisible = false;
            TxtDevBattery.Text = "—";
            TxtTopMeta.Text = "";
            return;
        }

        var p = Math.Clamp(pct.Value, 0, 100);
        var brush = p <= 15 ? BrushDanger : p <= 35 ? BrushWarn : (charging ? BrushSuccess : BrushOnline);
        BarBattery.Background = brush;
        BarBattery.Width = Math.Max(2, 20 * p / 100);
        TopBatteryBar.Background = brush;
        TopBatteryBar.Width = Math.Max(2, 21 * p / 100);
        IconBatteryBolt.IsVisible = charging;

        TxtDevBattery.Text = p.ToString("0") + "%";
        TxtTopMeta.Text = p.ToString("0") + "%" + (charging ? " ⚡" : "");
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
        var brush = on ? BrushOnline : BrushOffline;
        IcoBt.Fill = brush;
        BtSlash.IsVisible = !on;
        TxtDevBt.Text = on ? "已开启" : "已关闭";
        TopBt.Fill = brush;
        TopBtSlash.IsVisible = !on;
    }

    private async void BtnDisconnect_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_serial)) return;
        var serial = _serial;
        var isWireless = _device?.IsWireless == true;
        if (!await ConfirmAsync("断开连接", "确定要断开 " + serial + " 吗？", "断开", "取消")) return;

        // 停止正在运行的投屏并抑制自动重连，确保“断开”真正生效
        _scrcpy.Stop(serial);
        _suppressAutoReconnect = true;
        if (!isWireless) _suppressedSerial = serial;

        try
        {
            if (isWireless) await Adb.DisconnectAsync(serial);
        }
        catch (Exception ex)
        {
            Log.Warn("断开失败：" + ex.Message);
        }

        Interlocked.Increment(ref _deviceEpoch);
        DetachHelper();
        _serial = "";
        _device = null;
        ApplyDeviceUi(null);
        Toast("已断开 " + serial);
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

    private void UpdateCastVisual()
    {
        var casting = !string.IsNullOrEmpty(_serial) && _scrcpy.IsRunning(_serial);
        DotCasting.IsVisible = casting;
        TxtCastBtn.Text = casting ? "投屏运行中" : "开始投屏";
        if (!casting && string.IsNullOrEmpty(_serial))
            TxtCastStatus.Text = "未投屏";
    }

    private async void BtnCast_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }
        BtnCast.IsEnabled = false;
        try
        {
            await _scrcpy.StartAsync(_serial, _settings);
            UpdateCastVisual();
        }
        finally
        {
            BtnCast.IsEnabled = _device?.IsOnline == true;
        }
    }

    private void BtnStopCast_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_serial)) _scrcpy.Stop(_serial);
        UpdateCastVisual();
    }

    private void OnCastStatus(string serial, string message, bool error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TxtCastStatus.Text = message;
            TxtCastStatus.Foreground = error ? BrushDanger : (_scrcpy.IsRunning(serial) ? BrushOnline : BrushDim);
            UpdateCastVisual();
            if (error) Toast(message, true);
        });
    }

    // ───────────────────────── 高级互联 ─────────────────────────

    private void OnClipboardReceived(string text)
    {
        if (!_settings.SyncClipboard || string.IsNullOrEmpty(text)) return;
        Dispatcher.UIThread.Post(async () =>
        {
            if (string.Equals(text, _lastClip, StringComparison.Ordinal)) return;
            _lastClip = text;
            try
            {
                if (Clipboard is not null)
                    await Clipboard.SetTextAsync(text);
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
        lock (_tempAppsGate) _tempApps.Clear();
        TxtAppsCount.Text = "同步中…";
        await _helper.RequestAppsAsync();
    }

    private async Task RequestSmsAsync(bool clearExisting = false)
    {
        if (_helper is not { IsConnected: true }) return;
        lock (_tempSmsGate) _tempSms.Clear();
        if (clearExisting) _smsResetPending = true;
        await _helper.RequestSmsAsync();
    }

    // ───────────────────────── UI 工具 ─────────────────────────

    private void Toast(string message, bool error = false, bool success = false)
    {
        var accentColor = error ? "#FF6B6B" : (success ? "#56D364" : "#6FD3FF");
        var bgColor = error ? "#F0251215" : "#F00E1924";
        var borderColor = error ? "#55FF6B6B" : (success ? "#4456D364" : "#3A6FD3FF");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        grid.Children.Add(new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.Parse(accentColor))
        });
        var textBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetColumn(textBlock, 1);
        grid.Children.Add(textBlock);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse(bgColor)),
            BorderBrush = new SolidColorBrush(Color.Parse(borderColor)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(11, 10, 14, 10),
            MaxWidth = 420,
            BoxShadow = BoxShadows.Parse("0 10 26 0 #66000000"),
            Child = grid
        };
        border.Opacity = 0;
        border.RenderTransform = TransformOperations.Parse("translateY(10px)");
        border.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180), Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut() }
        };

        while (ToastStack.Children.Count >= 4)
            ToastStack.Children.RemoveAt(0);

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

    private async Task<bool> ConfirmAsync(string title, string body, string ok = "确定", string cancel = "取消")
    {
        await _dialogGate.WaitAsync();
        try
        {
            DialogTitle.Text = title;
            DialogBody.Text = body;
            DialogInput.IsVisible = false;
            BtnDialogOk.Content = ok;
            BtnDialogCancel.Content = cancel;
            var tcs = new TaskCompletionSource<bool?>();
            _dialogTcs = tcs;
            DialogOverlay.IsVisible = true;
            return await tcs.Task == true;
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async Task<string?> PromptAsync(string title, string body, string initial = "")
    {
        await _dialogGate.WaitAsync();
        try
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
            DialogInput.SelectAll();
            var ok = await tcs.Task;
            return ok == true ? DialogInput.Text?.Trim() : null;
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private void BtnDialogOk_Click(object? sender, RoutedEventArgs e)
    {
        DialogOverlay.IsVisible = false;
        var tcs = _dialogTcs;
        _dialogTcs = null;
        tcs?.TrySetResult(true);
    }

    private void BtnDialogCancel_Click(object? sender, RoutedEventArgs e)
    {
        DialogOverlay.IsVisible = false;
        var tcs = _dialogTcs;
        _dialogTcs = null;
        tcs?.TrySetResult(false);
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
        if (files.Count == 0 || string.IsNullOrEmpty(_serial)) return;

        e.DragEffects = DragDropEffects.Copy;
        DropOverlay.IsVisible = true;
        var apkOnly = files.All(f => f.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        TxtDropHint.Text = apkOnly ? "松手安装 APK" : "松手上传到 " + CurrentRemotePath;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => DropOverlay.IsVisible = false;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }
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

    // ───────────────────────── 媒体与上次设备 ─────────────────────────

    private void UpdateLastEndpointUi()
    {
        TxtLastEndpoint.Text = string.IsNullOrWhiteSpace(_settings.LastEndpoint)
            ? "上次设备：—"
            : "上次设备：" + _settings.LastEndpoint;
    }

    private void BtnClearLast_Click(object? sender, RoutedEventArgs e)
    {
        _settings.LastEndpoint = "";
        SettingsStore.Save();
        UpdateLastEndpointUi();
        Toast("已清除上次设备");
    }

    private void UpdateMediaUi(MediaInfo media)
    {
        var hasTrack = !string.IsNullOrWhiteSpace(media.Title);
        TxtMediaTitle.Text = hasTrack ? media.Title : "没有正在播放的媒体";
        TxtMediaArtist.Text = hasTrack ? media.Artist : "";
        _mediaPlaying = media.Playing;
        IconPlay.IsVisible = !_mediaPlaying;
        IconPause.IsVisible = _mediaPlaying;
        TxtMediaState.Text = !hasTrack ? "空闲" : (_mediaPlaying ? "播放中" : "已暂停");
        MediaArtBorder.BorderBrush = _mediaPlaying ? BrushOnline : new SolidColorBrush(Color.Parse("#1F3342"));
    }

    private void OnMediaChanged(MediaInfo media)
    {
        Dispatcher.UIThread.Post(() => UpdateMediaUi(media));
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
        if (_helper is { IsConnected: true }) await _helper.RequestMediaAsync();
    }

    private async void BtnMediaNext_Click(object? sender, RoutedEventArgs e)
    {
        if (_helper is { IsConnected: true }) await _helper.MediaCommandAsync("next");
    }
}
