using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Diagnostics;

namespace LengJiaoConnect
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _globalDeviceMonitor;
        private bool _isOobeShowing = false;
        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private bool _isRealExit = false; // 判断是真退出还是最小化到托盘
        private bool _isAutoStart = false;
        
        // 核心缓存：防止轮询卡顿
        private Dictionary<string, string> _deviceNamesCache = new Dictionary<string, string>();
        private string _lastConnectedSerials = "INIT_STATE";
        private string _activeSerial = "";
        private bool _isFirstConnectionShowed = false;
        private void BtnTransferSettings_Click(object sender, RoutedEventArgs e)
        {
            ShowPopup(PanelTransferSettings);
        }

        private void BtnToggleRoot_Click(object sender, RoutedEventArgs e)
        {
            _fmUseRoot = !_fmUseRoot;
            
            // 极客红绿变色警示交互
            BtnToggleRoot.Foreground = _fmUseRoot ? new SolidColorBrush(Color.FromRgb(255, 85, 85)) : new SolidColorBrush(Colors.White);
            BtnToggleRoot.BorderBrush = _fmUseRoot ? new SolidColorBrush(Color.FromRgb(255, 85, 85)) : new SolidColorBrush(Color.FromRgb(85, 85, 85));
            BtnToggleRoot.Content = _fmUseRoot ? "☠️ 已开启 Root 提权模式 (拥有根目录完全权限)" : "🔓 开启 Root 提权模式 (su -c)";
            
            if (_fmUseRoot)
            {
                MessageBox.Show("【极客警告】\n您已开启 Root 提权模式！\n在此模式下，您可以访问系统根目录 (/) 和 /data 等私密分区。\n但请注意：误删系统关键文件可能导致手机变砖，请极其谨慎地操作！", "高危权限提醒", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        // ================= 软件生命周期与托盘系统 =================
        
        // 🌟 软件启动时，自动初始化托盘和读取开机自启状态
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // 1. 初始化系统托盘图标 (极其极客的写法，直接提取自己 exe 的图标)
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
            _notifyIcon.Text = "棱角互联 (后台运行中)";
            _notifyIcon.Visible = true;
            _notifyIcon.DoubleClick += (s, args) => ShowMainWindow();

            // 2. 初始化托盘右键菜单
            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("显示主界面", null, (s, args) => ShowMainWindow());
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("完全退出", null, (s, args) => RealExit());
            _notifyIcon.ContextMenuStrip = contextMenu;

            // 3. 读取注册表，同步开机自启按钮的状态
            using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
            {
                _isAutoStart = key?.GetValue("LengJiaoConnect") != null;
                UpdateAutoStartUI();
            }
        }

        // 🌟 拦截窗口的“X”关闭按钮，改为隐藏到托盘
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isRealExit)
            {
                e.Cancel = true; // 拦截关闭
                this.Hide();     // 隐藏窗口
                _notifyIcon.ShowBalloonTip(2000, "棱角互联", "已最小化到系统托盘并在后台待命", System.Windows.Forms.ToolTipIcon.Info);
            }
            else
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            base.OnClosing(e);
        }

        private void ShowMainWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate(); // 抢占焦点，弹到最前面
        }

        private void RealExit()
        {
            _isRealExit = true;
           System.Windows.Application.Current.Shutdown();
        }

        // ================= 新增按钮交互事件 =================

        // 🌟 开机自启逻辑：直接操作 Windows 注册表
        private void BtnToggleAutoStart_Click(object sender, RoutedEventArgs e)
        {
            _isAutoStart = !_isAutoStart;
            string appPath = Process.GetCurrentProcess().MainModule.FileName;

            using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (_isAutoStart) key.SetValue("LengJiaoConnect", $"\"{appPath}\""); // 写入注册表
                else key.DeleteValue("LengJiaoConnect", false); // 抹除注册表
            }
            UpdateAutoStartUI();
        }

        private void UpdateAutoStartUI()
        {
            BtnToggleAutoStart.Foreground = _isAutoStart ? new SolidColorBrush(Color.FromRgb(76, 175, 80)) : new SolidColorBrush(Colors.White);
            BtnToggleAutoStart.BorderBrush = _isAutoStart ? new SolidColorBrush(Color.FromRgb(76, 175, 80)) : new SolidColorBrush(Color.FromRgb(85, 85, 85));
            BtnToggleAutoStart.Content = _isAutoStart ? "🚀 已开启开机自启" : "🚀 开机自动运行并在后台待命";
        }

        // 🌟 软链接逻辑：呼叫系统默认浏览器
        private void BtnGithubLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/error-10",
                    UseShellExecute = true
                });
            }
            catch { }
        }
        private void BtnAuthorLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://miqwq.com",
                    UseShellExecute = true // 在 .NET Core 之后，打开网页必须加这句
                });
            }
            catch { }
        }

        private void BtnToggleHiddenFiles_Click(object sender, RoutedEventArgs e)
        {
            _fmShowHidden = !_fmShowHidden;
            
            BtnToggleHiddenFiles.Foreground = _fmShowHidden ? new SolidColorBrush(Color.FromRgb(76, 175, 80)) : new SolidColorBrush(Colors.White);
            BtnToggleHiddenFiles.BorderBrush = _fmShowHidden ? new SolidColorBrush(Color.FromRgb(76, 175, 80)) : new SolidColorBrush(Color.FromRgb(85, 85, 85));
            BtnToggleHiddenFiles.Content = _fmShowHidden ? "👁️ 已显示所有隐藏文件 (ls -a)" : "🙈 隐藏以点号开头的文件 (默认)";
        }
        
        private DateTime _lastReconnectAttempt = DateTime.MinValue;
        private bool _isManualDisconnected = false; // 标记是否是手动断开
        private bool _turnScreenOffCasting = false; // 默认投屏后保持亮屏
        
        // 【新增】Scrcpy 核心引擎配置状态
        private int _scrcpyQualityLevel = 1; // 0:流畅 1:均衡 2:画质狂魔
        private bool _scrcpyEnableAudio = true;
        private bool _scrcpyEnableControl = true;
        // 【新增】文件传输高级配置状态
        private bool _fmUseRoot = false;
        private bool _fmShowHidden = false;
        private HelperApkManager _helperApkManager;

        private string _configFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last_ip.txt");

        private void SaveLastIp(string ip)
    {
        try { System.IO.File.WriteAllText(_configFilePath, ip); } catch { }
    }

    private string LoadLastIp()
    {
        try { if (System.IO.File.Exists(_configFilePath)) return System.IO.File.ReadAllText(_configFilePath).Trim(); } catch { }
        return "";
    }

        // 记录打开教程前的面板
        private UIElement _previousPanelBeforeTutorial;
        private TranslateTransform _previousTransBeforeTutorial;

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
        }

       private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 1. 尝试静默重连上次的设备
        string savedIp = LoadLastIp();
        if (!string.IsNullOrEmpty(savedIp))
        {
            // 在后台静默踢一脚，不干扰 UI
            _ = Task.Run(() => AdbHelper.ExecuteCommand($"connect {savedIp}:5555"));
        }

        // 2. 启动全局设备监控
        _globalDeviceMonitor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _globalDeviceMonitor.Tick += async (s, args) => await CheckDevicesAsync();
        _globalDeviceMonitor.Start();
    }

        // ================= 全局心脏监控 =================
        private async Task CheckDevicesAsync()
        {
            try
            {
                string output = await Task.Run(() => AdbHelper.ExecuteCommand("devices"));
                string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                
                List<string> currentSerials = new List<string>();

                for (int i = 1; i < lines.Length; i++)
                {
                    var parts = lines[i].Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && parts[1] == "device")
                    {
                        string serial = parts[0];
                        currentSerials.Add(serial);

                        if (!_deviceNamesCache.ContainsKey(serial))
                        {
                            string model = await Task.Run(() => AdbHelper.ExecuteCommand($"-s {serial} shell getprop ro.product.model")).ConfigureAwait(true);
                            _deviceNamesCache[serial] = string.IsNullOrEmpty(model.Trim()) ? "未知设备" : model.Trim();
                        }
                    }
                }

                string currentSerialsStr = string.Join(",", currentSerials);

                if (currentSerialsStr != _lastConnectedSerials)
                {
                    _lastConnectedSerials = currentSerialsStr;

                    string wifiPath = "M12.01,21.49 L23.64,7 C23.19,6.66 18.71,3 12,3 C5.28,3 0.81,6.66 0.36,7 L11.99,21.49 C11.99,21.49 12.01,21.51 12.01,21.49 Z";
                    string usbPath = "M15,7v4h1v2h-3V5h2l-3-4L9,5h2v8H8v-2h1V7H5v4h1v2H3v-2h1V7H1v4h1v2h3v8h4v4h2v-4h4v-8h3v-2h1V7H15z";

                    Dispatcher.Invoke(() =>
                    {
                        if (currentSerials.Count == 0)
                        {
                            _activeSerial = "";
                            TxtHeaderDeviceName.Text = "未连接设备";
                            TxtHeaderDeviceName.Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170));
                            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(85, 85, 85));
                            
                            IconConnectionType.Visibility = Visibility.Collapsed;
                            StatusBarContainer.Opacity = 0;
                            TransFrame1.Y = 0; 
                            TransFrame2.Y = 30;
                            _isFirstConnectionShowed = false; 
                            BtnDisconnect.Visibility = Visibility.Collapsed;
                            
                            if (!_isOobeShowing) ShowOobe();

                            if (!_isManualDisconnected && (DateTime.Now - _lastReconnectAttempt).TotalSeconds > 8)
                            {
                                _lastReconnectAttempt = DateTime.Now;
                                string savedIp = LoadLastIp();
                                if (!string.IsNullOrEmpty(savedIp))
                                {
                                    _ = Task.Run(() => AdbHelper.ExecuteCommand($"connect {savedIp}:5555"));
                                }
                            }
                        }
                        else
                        {
                            if (!currentSerials.Contains(_activeSerial)) _activeSerial = currentSerials[0];

                            TxtHeaderDeviceName.Text = _deviceNamesCache[_activeSerial];
                            TxtHeaderDeviceName.Foreground = new SolidColorBrush(Color.FromRgb(224, 224, 224));
                            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                            
                            bool isNetwork = _activeSerial.Contains(":") || _activeSerial.Contains(".");
                            IconConnectionType.Data = Geometry.Parse(isNetwork ? wifiPath : usbPath);
                            IconConnectionType.Visibility = Visibility.Visible;
                            BtnDisconnect.Visibility = isNetwork ? Visibility.Visible : Visibility.Collapsed;

                            if (_isOobeShowing) HideOobe();

                            if (!_isFirstConnectionShowed)
                            {
                                _isFirstConnectionShowed = true;
                                TriggerStatusBarAnimation(_activeSerial);
                                _ = CheckAndAutoStartAdvancedInteropAsync(_activeSerial);
                            }
                            else
                            {
                                _ = UpdateDeviceStatusAsync(_activeSerial);
                            }
                        }

                        DeviceListContainer.Children.Clear();
                        foreach (var s in currentSerials)
                        {
                            bool isNet = s.Contains(":") || s.Contains(".");
                            
                            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal };
                            sp.Children.Add(new System.Windows.Shapes.Path {
                                Data = Geometry.Parse(isNet ? wifiPath : usbPath),
                                Fill = new SolidColorBrush(s == _activeSerial ? Color.FromRgb(76, 175, 80) : Colors.White),
                                Height = 14, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0),
                                VerticalAlignment = VerticalAlignment.Center
                            });
                            sp.Children.Add(new TextBlock {
                                Text = s == _activeSerial ? $"✔️ {_deviceNamesCache[s]} ({s})" : $"     {_deviceNamesCache[s]} ({s})",
                                VerticalAlignment = VerticalAlignment.Center
                            });

                            Button btn = new Button
                            {
                                Content = sp,
                                Style = (Style)FindResource("HeaderButtonStyle"),
                                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                                Foreground = s == _activeSerial ? new SolidColorBrush(Color.FromRgb(76, 175, 80)) : new SolidColorBrush(Colors.White),
                                HorizontalContentAlignment = HorizontalAlignment.Left,
                                Margin = new Thickness(0, 0, 0, 8),
                                Padding = new Thickness(15, 10, 15, 10)
                            };
                            btn.Click += delegate {
                                _activeSerial = s;
                                TxtHeaderDeviceName.Text = _deviceNamesCache[s];
                                IconConnectionType.Data = Geometry.Parse(isNet ? wifiPath : usbPath);
                                HidePopup();
                                _lastConnectedSerials = "FORCE_REFRESH";
                                _ = UpdateDeviceStatusAsync(s);
                            };
                            DeviceListContainer.Children.Add(btn);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                // 🌟 防弹衣机制：如果报错，拦截崩溃并在顶部提醒！
                Dispatcher.Invoke(() => {
                    TxtHeaderDeviceName.Text = "⚠️ 引擎通讯异常";
                    TxtHeaderDeviceName.ToolTip = ex.Message;
                    StatusDot.Fill = new SolidColorBrush(Color.FromRgb(220, 53, 69)); // 指示灯变红
                });
            }
        }

        // ================= OOBE 核心动效 =================
        private void ShowOobe()
        {
            _isOobeShowing = true;
            OobeLayer.Visibility = Visibility.Visible;

            var blurAnim = new DoubleAnimation(0, 15, TimeSpan.FromSeconds(0.25));
            MainBlur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);

            var fadeOverlayAnim = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.25));
            OobeOverlay.BeginAnimation(OpacityProperty, fadeOverlayAnim);

            var fadeContainerAnim = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.25));
            var slideAnim = new DoubleAnimation(40, 0, TimeSpan.FromSeconds(0.3)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            
            OobeContainer.BeginAnimation(OpacityProperty, fadeContainerAnim);
            OobeTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);

            ResetOobePanels();
        }

        private void HideOobe()
        {
            _isOobeShowing = false;
            
            var blurAnim = new DoubleAnimation(15, 0, TimeSpan.FromSeconds(0.25));
            MainBlur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);

            var fadeAnim = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.2));
            var slideAnim = new DoubleAnimation(0, 40, TimeSpan.FromSeconds(0.2)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };

            fadeAnim.Completed += (s, e) => { OobeLayer.Visibility = Visibility.Collapsed; };

            OobeOverlay.BeginAnimation(OpacityProperty, fadeAnim);
            OobeContainer.BeginAnimation(OpacityProperty, fadeAnim);
            OobeTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }

        private void ResetOobePanels()
        {
            TransStep1.BeginAnimation(TranslateTransform.XProperty, null);
            TransStep2Wired.BeginAnimation(TranslateTransform.XProperty, null);
            TransStep3Pair.BeginAnimation(TranslateTransform.XProperty, null);
            TransStep4Connect.BeginAnimation(TranslateTransform.XProperty, null);
            TransTutorial.BeginAnimation(TranslateTransform.XProperty, null);

            OobeStep1.Visibility = Visibility.Visible; TransStep1.X = 0;
            OobeStep2Wired.Visibility = Visibility.Collapsed; TransStep2Wired.X = 640;
            OobeStep3WirelessPair.Visibility = Visibility.Collapsed; TransStep3Pair.X = 640;
            OobeStep4WirelessConnect.Visibility = Visibility.Collapsed; TransStep4Connect.X = 640;
            OobeTutorialPanel.Visibility = Visibility.Collapsed; TransTutorial.X = 640;
        }

        private void SlidePanel(UIElement hidePanel, TranslateTransform hideTrans, UIElement showPanel, TranslateTransform showTrans, bool isForward)
        {
            showPanel.Visibility = Visibility.Visible;
            double hideEnd = isForward ? -680 : 680;
            double showStart = isForward ? 680 : -680;

            var hideAnim = new DoubleAnimation(0, hideEnd, TimeSpan.FromSeconds(0.3)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            var showAnim = new DoubleAnimation(showStart, 0, TimeSpan.FromSeconds(0.3)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };

            hideAnim.Completed += (s, e) => hidePanel.Visibility = Visibility.Collapsed;

            hideTrans.BeginAnimation(TranslateTransform.XProperty, hideAnim);
            showTrans.BeginAnimation(TranslateTransform.XProperty, showAnim);
        }

        private void BtnDeviceMenu_Click(object sender, RoutedEventArgs e)
        {
            ShowPopup(PanelDeviceCenter); // 弹出统一设备中心
        }

        private void BtnAddNewDeviceFromMenu_Click(object sender, RoutedEventArgs e)
        {
            HidePopup();
            ShowOobe(); // 呼出连接向导
        }

        // ================= 按钮流转逻辑 =================
        private void BtnCloseOobe_Click(object sender, RoutedEventArgs e) { HideOobe(); }

        private void BtnOobeSelectWired_Click(object sender, RoutedEventArgs e) { SlidePanel(OobeStep1, TransStep1, OobeStep2Wired, TransStep2Wired, true); }
        private void BtnOobeSelectWireless_Click(object sender, RoutedEventArgs e) { SlidePanel(OobeStep1, TransStep1, OobeStep3WirelessPair, TransStep3Pair, true); }

        private void BtnOobeBack_Click(object sender, RoutedEventArgs e)
        {
            if (OobeStep2Wired.Visibility == Visibility.Visible) SlidePanel(OobeStep2Wired, TransStep2Wired, OobeStep1, TransStep1, false);
            else if (OobeStep3WirelessPair.Visibility == Visibility.Visible) SlidePanel(OobeStep3WirelessPair, TransStep3Pair, OobeStep1, TransStep1, false);
            else if (OobeStep4WirelessConnect.Visibility == Visibility.Visible) SlidePanel(OobeStep4WirelessConnect, TransStep4Connect, OobeStep1, TransStep1, false);
        }

        private void BtnToggleWirelessMode_Click(object sender, RoutedEventArgs e)
        {
            if (OobeStep3WirelessPair.Visibility == Visibility.Visible)
            {
                OobeInputConnectIp.Text = OobeInputPairIp.Text; // 同步IP
                SlidePanel(OobeStep3WirelessPair, TransStep3Pair, OobeStep4WirelessConnect, TransStep4Connect, true);
            }
            else
            {
                SlidePanel(OobeStep4WirelessConnect, TransStep4Connect, OobeStep3WirelessPair, TransStep3Pair, false);
            }
        }

        private void BtnShowTutorial_Click(object sender, RoutedEventArgs e)
        {
            if (OobeStep3WirelessPair.Visibility == Visibility.Visible)
            {
                _previousPanelBeforeTutorial = OobeStep3WirelessPair; _previousTransBeforeTutorial = TransStep3Pair;
            }
            else
            {
                _previousPanelBeforeTutorial = OobeStep4WirelessConnect; _previousTransBeforeTutorial = TransStep4Connect;
            }
            SlidePanel(_previousPanelBeforeTutorial, _previousTransBeforeTutorial, OobeTutorialPanel, TransTutorial, true);
        }

        private void BtnCloseTutorial_Click(object sender, RoutedEventArgs e)
        {
            SlidePanel(OobeTutorialPanel, TransTutorial, _previousPanelBeforeTutorial, _previousTransBeforeTutorial, false);
        }

        // ================= 异步防卡死：ADB 动作区 =================
        private async void BtnOobeActionPair_Click(object sender, RoutedEventArgs e)
        {
            _isManualDisconnected = false; // 用户点击了配对，说明不是断开状态，重置这个标记
            string ip = OobeInputPairIp.Text.Trim();
            string port = OobeInputPairPort.Text.Trim();
            string code = OobeInputPairCode.Text.Trim();

            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(port) || string.IsNullOrEmpty(code)) { MessageBox.Show("请完整填写 IP、端口和配对码。"); return; }

            BtnPairAction.Content = "正在配对...";
            BtnPairAction.IsEnabled = false;

            string result = await Task.Run(() => AdbHelper.ExecuteCommand($"pair {ip}:{port} {code}"));

            BtnPairAction.Content = "开始配对";
            BtnPairAction.IsEnabled = true;

            if (result.Contains("Failed") || result.Contains("error"))
            {
                MessageBox.Show($"配对失败，请检查输入或手机端状态:\n{result}", "配对失败");
            }
            else
            {
                // 配对成功！Android 11+ 通常会自动建立正式连接。
                BtnPairAction.Content = "配对成功，等待自动连接...";
                
                // 循环等 3 秒，给手机一点自动连接的反应时间
                bool autoConnected = false;
                for (int i = 0; i < 3; i++)
                {
                    await Task.Delay(1000);
                    string chk = await Task.Run(() => AdbHelper.ExecuteCommand("devices"));
                    if (chk.Contains(ip))
                    {
                        autoConnected = true;
                        break;
                    }
                }

                if (autoConnected)
                {
                    // 手机已经自动连上了！
                    // 不弹任何窗，也不切面板。全权交给全局心脏监控去把整个 OOBE 面板收起
                    BtnPairAction.Content = "已自动完成连接";
                }
                else
                {
                    // 如果过了 3 秒还没连上（部分定制系统可能不会自动连）
                    // 那就丝滑地左滑到连接面板，让他自己点“立即连接”
                    OobeInputConnectIp.Text = ip;
                    SlidePanel(OobeStep3WirelessPair, TransStep3Pair, OobeStep4WirelessConnect, TransStep4Connect, true);
                    BtnPairAction.Content = "开始配对"; // 恢复原来文字以便下次用
                }
            }
        }

        private async void BtnOobeActionConnect_Click(object sender, RoutedEventArgs e)
        {
            _isManualDisconnected = false; // 用户点击了连接，说明不是断开状态，重置这个标记
            string ip = OobeInputConnectIp.Text.Trim();
            string port = OobeInputConnectPort.Text.Trim();

            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(port)) return;

            TxtConnectStatus.Text = "正在尝试建立连接，请稍候...";
            TxtConnectStatus.Foreground = new SolidColorBrush(Color.FromRgb(244, 208, 63));
            BtnConnectAction.IsEnabled = false;

            string result = await Task.Run(() => AdbHelper.ExecuteCommand($"connect {ip}:{port}"));

            BtnConnectAction.IsEnabled = true;

            if (result.Contains("connected"))
    {
        SaveLastIp(ip); // 【新增】连接成功，立刻记住这个 IP
        TxtConnectStatus.Text = "连接成功！";
        // ... 原有逻辑
    }
            else
            {
                TxtConnectStatus.Text = "连接失败，请重试";
                TxtConnectStatus.Foreground = new SolidColorBrush(Color.FromRgb(220, 53, 69));
                MessageBox.Show($"网络握手失败:\n{result}", "错误");
            }
        }

        // ================= 小弹窗功能保留 =================
        private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { HidePopup(); }
        private void BtnClosePopup_Click(object sender, RoutedEventArgs e) 
        { 
            _isWaitingForPermission = false;
            HidePopup(); 
        }
        private void BtnToggleScreenMode_Click(object sender, RoutedEventArgs e)
        {
            _turnScreenOffCasting = !_turnScreenOffCasting;
            
            // 动态切换按钮的文字和颜色状态
            if (_turnScreenOffCasting)
            {
                BtnToggleScreenMode.Content = "🌑 投屏后息屏";
                BtnToggleScreenMode.Foreground = new SolidColorBrush(Color.FromRgb(231, 76, 60)); // 变成显眼的红色警告
            }
            else
            {
                BtnToggleScreenMode.Content = "💡 保持亮屏";
                BtnToggleScreenMode.Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)); // 恢复灰色
            }
        }

        private void ShowPrivacyStep(int step)
        {
            PrivacyStep1.Visibility = Visibility.Collapsed;
            PrivacyStep2.Visibility = Visibility.Collapsed;
            PrivacyStep3.Visibility = Visibility.Collapsed;
            PrivacyStep4.Visibility = Visibility.Collapsed;

            if (step == 1) PrivacyStep1.Visibility = Visibility.Visible;
            if (step == 2) PrivacyStep2.Visibility = Visibility.Visible;
            if (step == 3) PrivacyStep3.Visibility = Visibility.Visible;
            if (step == 4) PrivacyStep4.Visibility = Visibility.Visible;
        }

        private async void BtnAdvancedToggle_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_activeSerial))
            {
                MessageBox.Show("请先连接一台设备！", "提示");
                return;
            }

            string packages = await Task.Run(() => AdbHelper.ExecuteCommand($"-s {_activeSerial} shell pm list packages"));
            bool isInstalled = packages.Contains("com.lengjiao.helper");

            if (isInstalled && _helperApkManager != null && _helperApkManager.IsRunning)
            {
                ChkSyncClipboard.IsChecked = _helperApkManager.SyncClipboard;
                ChkSyncNotifications.IsChecked = _helperApkManager.SyncNotifications;
                ShowPopup(PanelAdvancedSettings);
            }
            else if (isInstalled)
            {
                // Installed but not connected/authorized
                ShowPrivacyStep(2);
                ShowPopup(PanelPrivacyPolicy);
            }
            else
            {
                // Not installed
                ShowPrivacyStep(1);
                ShowPopup(PanelPrivacyPolicy);
            }
        }

        private async void BtnPrivacyAgree_Click(object sender, RoutedEventArgs e)
        {
            BtnPrivacyAgree.Content = "正在部署...";
            BtnPrivacyAgree.IsEnabled = false;

            if (_helperApkManager == null)
                _helperApkManager = new HelperApkManager(this, _activeSerial);

            bool installed = await _helperApkManager.InstallAsync();
            BtnPrivacyAgree.Content = "同意并安装";
            BtnPrivacyAgree.IsEnabled = true;

            if (installed)
            {
                ShowPrivacyStep(2);
            }
            else
            {
                MessageBox.Show("辅助服务安装失败。请检查设备是否拦截了ADB安装请求。", "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnPrivacyGoToSettings_Click(object sender, RoutedEventArgs e)
        {
            await Task.Run(() => AdbHelper.ExecuteCommand($"-s {_activeSerial} shell am start -a android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS"));
            ShowPrivacyStep(3);
            
            _isWaitingForPermission = true;
            _ = WaitForPermissionAndConnectAsync();
        }

        private bool _isWaitingForPermission = false;
        private async Task WaitForPermissionAndConnectAsync()
        {
            if (_helperApkManager == null)
                _helperApkManager = new HelperApkManager(this, _activeSerial);

            for (int i = 0; i < 30; i++) // wait up to 60s
            {
                if (!_isWaitingForPermission) return; // User cancelled

                bool connected = await _helperApkManager.ConnectAsync();
                if (connected)
                {
                    Dispatcher.Invoke(() => {
                        BtnAdvancedToggle.Content = "✨ 高级互联(已开启)";
                        BtnAdvancedToggle.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                        ShowPrivacyStep(4);
                    });
                    return;
                }
                await Task.Delay(2000);
            }

            Dispatcher.Invoke(() => {
                _isWaitingForPermission = false;
                ShowPrivacyStep(2); // Fallback to asking them to authorize
            });
        }

        private void ChkAdvancedOption_Changed(object sender, RoutedEventArgs e)
        {
            if (_helperApkManager != null)
            {
                _helperApkManager.SyncClipboard = ChkSyncClipboard.IsChecked == true;
                _helperApkManager.SyncNotifications = ChkSyncNotifications.IsChecked == true;
            }
        }

        private void BtnOpenPermissionSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_activeSerial))
            {
                Task.Run(() => AdbHelper.ExecuteCommand($"-s {_activeSerial} shell am start -a android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS"));
            }
        }

        private void BtnShowRevokeAuth_Click(object sender, RoutedEventArgs e)
        {
            // 直接调用 ShowPopup 切换面板，不用调用 HidePopup，因为 ShowPopup 内部已经把所有子面板收起了
            ShowPopup(PanelRevokeAuth);
        }

        private void BtnCancelRevoke_Click(object sender, RoutedEventArgs e)
        {
            HidePopup();
        }

        private async void BtnConfirmRevoke_Click(object sender, RoutedEventArgs e)
        {
            HidePopup();
            if (_helperApkManager != null)
            {
                _helperApkManager.Stop();
                _helperApkManager = null;
            }
            
            if (!string.IsNullOrEmpty(_activeSerial))
            {
                await Task.Run(() => AdbHelper.ExecuteCommand($"-s {_activeSerial} uninstall com.lengjiao.helper"));
            }

            BtnAdvancedToggle.Content = "✨ 高级互联(未开启)";
            BtnAdvancedToggle.Background = new SolidColorBrush(Color.FromRgb(231, 76, 60));
        }

        private async Task CheckAndAutoStartAdvancedInteropAsync(string serial)
        {
            string packages = await Task.Run(() => AdbHelper.ExecuteCommand($"-s {serial} shell pm list packages"));
            if (packages.Contains("com.lengjiao.helper"))
            {
                if (_helperApkManager == null)
                    _helperApkManager = new HelperApkManager(this, serial);
                
                bool connected = await _helperApkManager.ConnectAsync();
                
                Dispatcher.Invoke(() => {
                    if (connected)
                    {
                        BtnAdvancedToggle.Content = "✨ 高级互联(已开启)";
                        BtnAdvancedToggle.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                    }
                    else
                    {
                        BtnAdvancedToggle.Content = "✨ 高级互联(未开启)";
                        BtnAdvancedToggle.Background = new SolidColorBrush(Color.FromRgb(231, 76, 60));
                    }
                });
            }
            else
            {
                Dispatcher.Invoke(() => {
                    BtnAdvancedToggle.Content = "✨ 高级互联(未开启)";
                    BtnAdvancedToggle.Background = new SolidColorBrush(Color.FromRgb(231, 76, 60));
                    ShowPrivacyStep(1);
                    ShowPopup(PanelPrivacyPolicy);
                });
            }
        }

        private void BtnScrcpySettings_Click(object sender, RoutedEventArgs e)
        {
            ShowPopup(PanelScrcpySettings); // 弹出我们刚刚画好的高颜值面板
        }

        // --- 设置面板交互逻辑 ---
        private void BtnQuality_Click(object sender, RoutedEventArgs e)
        {
            Button btn = sender as Button;
            if (btn.Name == "BtnSetQ_Low") _scrcpyQualityLevel = 0;
            else if (btn.Name == "BtnSetQ_Mid") _scrcpyQualityLevel = 1;
            else if (btn.Name == "BtnSetQ_High") _scrcpyQualityLevel = 2;

            // 动态刷新卡片UI状态 (绿色代表选中，灰色代表未选中)
            SolidColorBrush activeBg = new SolidColorBrush(Color.FromRgb(37, 37, 38));
            SolidColorBrush inactiveBg = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            SolidColorBrush activeBorder = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            SolidColorBrush inactiveBorder = new SolidColorBrush(Color.FromRgb(51, 51, 51));
            SolidColorBrush activeText = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            SolidColorBrush inactiveText = new SolidColorBrush(Color.FromRgb(170, 170, 170));

            BtnSetQ_Low.Background = _scrcpyQualityLevel == 0 ? activeBg : inactiveBg;
            BtnSetQ_Low.BorderBrush = _scrcpyQualityLevel == 0 ? activeBorder : inactiveBorder;
            ((TextBlock)((StackPanel)BtnSetQ_Low.Content).Children[0]).Foreground = _scrcpyQualityLevel == 0 ? activeText : inactiveText;

            BtnSetQ_Mid.Background = _scrcpyQualityLevel == 1 ? activeBg : inactiveBg;
            BtnSetQ_Mid.BorderBrush = _scrcpyQualityLevel == 1 ? activeBorder : inactiveBorder;
            ((TextBlock)((StackPanel)BtnSetQ_Mid.Content).Children[0]).Foreground = _scrcpyQualityLevel == 1 ? activeText : inactiveText;

            BtnSetQ_High.Background = _scrcpyQualityLevel == 2 ? activeBg : inactiveBg;
            BtnSetQ_High.BorderBrush = _scrcpyQualityLevel == 2 ? activeBorder : inactiveBorder;
            ((TextBlock)((StackPanel)BtnSetQ_High.Content).Children[0]).Foreground = _scrcpyQualityLevel == 2 ? activeText : inactiveText;
        }

        private void BtnToggleAudio_Click(object sender, RoutedEventArgs e)
        {
            _scrcpyEnableAudio = !_scrcpyEnableAudio;
            BtnToggleAudio.Foreground = _scrcpyEnableAudio ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromRgb(136, 136, 136));
            BtnToggleAudio.Content = _scrcpyEnableAudio ? "🔊 开启音频同步 (需 Android 11+)" : "🔇 已关闭音频同步";
        }

        private void BtnToggleControl_Click(object sender, RoutedEventArgs e)
        {
            _scrcpyEnableControl = !_scrcpyEnableControl;
            BtnToggleControl.Foreground = _scrcpyEnableControl ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromRgb(136, 136, 136));
            BtnToggleControl.Content = _scrcpyEnableControl ? "🖱️ 允许电脑控制手机 (键鼠操作)" : "👁️ 仅观影模式 (禁用电脑控制)";
        }

        // --- 核心拉起投屏逻辑 ---
        private void BtnScreenCast_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_activeSerial))
            {
                MessageBox.Show("请先连接一台设备后再进行投屏！", "提示");
                return;
            }

            try
            {
                string scrcpyPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "scrcpy", "scrcpy.exe");
                
                if (!System.IO.File.Exists(scrcpyPath))
                {
                    MessageBox.Show($"找不到 scrcpy 核心组件！\n请确保已将文件解压至:\n{scrcpyPath}", "缺失组件");
                    return;
                }

                // 1. 基础设备指向
                string args = $"-s {_activeSerial}";
                
                // 2. 息屏与控制开关
                if (_turnScreenOffCasting) args += " -S";
                if (!_scrcpyEnableAudio) args += " --no-audio";
                if (!_scrcpyEnableControl) args += " --no-control";

                // 3. 核心画质调度引擎 (极其硬核的参数注入)
                if (_scrcpyQualityLevel == 0) 
                    args += " -b 2M -m 800 --max-fps 30"; // 流畅：低码率，锁 30帧，限制短边 800
                else if (_scrcpyQualityLevel == 1) 
                    args += " -b 8M -m 1920 --max-fps 60"; // 均衡：8M 码率，锁 60帧，适合日常 1080p
                else if (_scrcpyQualityLevel == 2) 
                    args += " -b 24M --max-fps 120"; // 狂魔：24M 超高码率，解禁 120帧，压榨硬件极限

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = scrcpyPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动投屏失败: {ex.Message}", "错误");
            }
        }

        private void ShowPopup(UIElement targetPanel)
        {
            // 🌟 核心修复：每次弹窗前，先把所有子面板强行隐藏，防止互相嵌套或干扰
            PanelDeviceCenter.Visibility = Visibility.Collapsed;
            PanelScrcpySettings.Visibility = Visibility.Collapsed;
            PanelNewItem.Visibility = Visibility.Collapsed;
            PanelTransferSettings.Visibility = Visibility.Collapsed; // 🌟 新增防冲突
            PanelPrivacyPolicy.Visibility = Visibility.Collapsed;
            PanelAdvancedSettings.Visibility = Visibility.Collapsed;
            PanelRevokeAuth.Visibility = Visibility.Collapsed;

            // 单独让被点名的面板显示
            targetPanel.Visibility = Visibility.Visible;
            PopupOverlay.Visibility = Visibility.Visible;

            var blurAnim = new DoubleAnimation(0, 15, TimeSpan.FromSeconds(0.25));
            MainBlur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);

            var fadeAnim = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.2));
            var slideAnim = new DoubleAnimation(40, 0, TimeSpan.FromSeconds(0.25)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };

            PopupContainer.BeginAnimation(OpacityProperty, fadeAnim);
            PopupTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }

        private async void HidePopup()
        {
            var blurAnim = new DoubleAnimation(15, 0, TimeSpan.FromSeconds(0.2));
            MainBlur.BeginAnimation(BlurEffect.RadiusProperty, blurAnim);

            var fadeAnim = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.15));
            var slideAnim = new DoubleAnimation(0, 40, TimeSpan.FromSeconds(0.2)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };

            PopupContainer.BeginAnimation(OpacityProperty, fadeAnim);
            PopupTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);

            await Task.Delay(200);
            PopupOverlay.Visibility = Visibility.Collapsed;
            
            // 确保完全解除模糊（防止动画卡死）
            MainBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
            MainBlur.Radius = 0;
        }

        private async void TriggerStatusBarAnimation(string serial)
        {
            // 开局清理可能存在的旧动画锁定，并归位
            TransFrame1.BeginAnimation(TranslateTransform.YProperty, null);
            TransFrame2.BeginAnimation(TranslateTransform.YProperty, null);
            TransFrame1.Y = 0;
            TransFrame2.Y = 30;

            // 1. 渐显 "自动连接成功"
            var fadeAnim = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.5));
            StatusBarContainer.BeginAnimation(OpacityProperty, fadeAnim);

            // 2. 停顿 3 秒钟
            await Task.Delay(3000);

            // 停顿期间去后台抓取真实的电量、WIFI和蓝牙状态
            await UpdateDeviceStatusAsync(serial);

            // 3. 同步滑动：帧1滚出视野，帧2滚入视野
            var slideUp1 = new DoubleAnimation(0, -30, TimeSpan.FromSeconds(0.4)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var slideUp2 = new DoubleAnimation(30, 0, TimeSpan.FromSeconds(0.4)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            
            TransFrame1.BeginAnimation(TranslateTransform.YProperty, slideUp1);
            TransFrame2.BeginAnimation(TranslateTransform.YProperty, slideUp2);
        }

        private async Task UpdateDeviceStatusAsync(string serial)
        {
            if (string.IsNullOrEmpty(serial)) return;

            try
            {
                // ADB 抓取电量
                string batteryOut = await Task.Run(() => AdbHelper.ExecuteCommand($"-s {serial} shell dumpsys battery"));
                int level = 100;
                foreach (var line in batteryOut.Split('\n'))
                {
                    if (line.Trim().StartsWith("level:"))
                    {
                        int.TryParse(line.Split(':')[1].Trim(), out level);
                        break;
                    }
                }

                // ADB 抓取 WiFi 状态
                string wlanOut = await Task.Run(() => AdbHelper.ExecuteCommand($"-s {serial} shell ip addr show wlan0"));
                bool wifiOn = wlanOut.Contains("inet ");

                // ADB 抓取蓝牙状态
                string btOut = await Task.Run(() => AdbHelper.ExecuteCommand($"-s {serial} shell settings get global bluetooth_on"));
                bool btOn = btOut.Trim() == "1";

                Dispatcher.Invoke(() =>
                {
                    TxtStatusBattery.Text = $"{level}%";
                    IconStatusWifi.Visibility = wifiOn ? Visibility.Visible : Visibility.Collapsed;
                    IconStatusBT.Visibility = btOn ? Visibility.Visible : Visibility.Collapsed;
                });
            }
            catch { }
        }
   
    private async void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_activeSerial)) return;

            if (_activeSerial.Contains(":") || _activeSerial.Contains("."))
            {
                BtnDisconnect.IsEnabled = false;
                BtnDisconnect.Content = "断开中...";

                await Task.Run(() => AdbHelper.ExecuteCommand($"disconnect {_activeSerial}"));
                
                // 核心：标记为手动断开，阻止哈基米乱帮倒忙
                _isManualDisconnected = true; 
                
                _activeSerial = "";
                _lastConnectedSerials = "FORCE_REFRESH";

                BtnDisconnect.Content = "断开无线连接";
                BtnDisconnect.IsEnabled = true;
            }
        }

        private void BtnOpenTerminal_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 获取当前软件运行目录
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    // /K 表示执行完命令后不自动关闭黑框
                    // 自动变黑底绿字，自动 cd 到软件目录，并打印欢迎语
                    Arguments = $"/K \"color 0A & cd /d \"{baseDir}\" & echo ======================================= & echo [棱角互联] 已为您拉起内置 ADB 终端 & echo 您可以直接在这里输入 adb 命令进行调试 & echo ======================================= & echo.\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动终端失败: {ex.Message}", "错误");
            }
        }
        // ================= 文件管理器逻辑引擎 =================
        private string _currentAndroidPath = "/sdcard/";

       private void BtnFileTransfer_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_activeSerial))
            {
                MessageBox.Show("请先连接一台设备！", "提示");
                return;
            }
            
            // 切换面板显示
            PanelHomeButtons.Visibility = Visibility.Collapsed;
            PanelFileManager.Visibility = Visibility.Visible;
            
            // 🌟 注入丝滑的渐现 + 向上浮现动画
            PanelFileManager.Opacity = 0;
            TransFileManager.Y = 30;
            
            var fadeAnim = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3));
            var slideAnim = new DoubleAnimation(30, 0, TimeSpan.FromSeconds(0.3)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            
            PanelFileManager.BeginAnimation(OpacityProperty, fadeAnim);
            TransFileManager.BeginAnimation(TranslateTransform.YProperty, slideAnim);

            // 加载根目录
            _ = LoadDirectoryAsync(_currentAndroidPath);
        }

        private void BtnExitFileManager_Click(object sender, RoutedEventArgs e)
        {
            // 🌟 注入退出时的渐隐 + 向下沉底动画
            var fadeAnim = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.2));
            var slideAnim = new DoubleAnimation(0, 30, TimeSpan.FromSeconds(0.2)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            
            // 动画播放完毕后再隐藏面板
            fadeAnim.Completed += (s, ev) => 
            {
                PanelFileManager.Visibility = Visibility.Collapsed;
                PanelHomeButtons.Visibility = Visibility.Visible;
                
                // 给主界面大按钮也加上一个优雅的回退显现动画
                PanelHomeButtons.Opacity = 0;
                PanelHomeButtons.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3)));
            };

            PanelFileManager.BeginAnimation(OpacityProperty, fadeAnim);
            TransFileManager.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }

        private void BtnRefreshDir_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadDirectoryAsync(_currentAndroidPath);
        }

        private void BtnDirUp_Click(object sender, RoutedEventArgs e)
        {
            if (_currentAndroidPath == "/" || _currentAndroidPath == "/sdcard/") return; // 保护底层
            
            // 剥离最后一层目录
            string path = _currentAndroidPath.TrimEnd('/');
            int lastSlash = path.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                _currentAndroidPath = path.Substring(0, lastSlash + 1);
                _ = LoadDirectoryAsync(_currentAndroidPath);
            }
        }

        private void TxtCurrentPath_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string newPath = TxtCurrentPath.Text.Trim();
                if (!newPath.EndsWith("/")) newPath += "/";
                _currentAndroidPath = newPath;
                _ = LoadDirectoryAsync(_currentAndroidPath);
            }
        }

        // 双击列表项进入文件夹
        // 🌟 1. 双击列表项：如果是文件夹就进入，如果是文件就下载！
       private async void ListFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ListFiles.SelectedItem is AndroidFile selectedItem)
            {
                if (selectedItem.IsDirectory)
                {
                    _currentAndroidPath += selectedItem.Name + "/";
                    _ = LoadDirectoryAsync(_currentAndroidPath);
                }
                else
                {
                    await DownloadFileToDesktop(selectedItem.Name);
                }
            }
        }

        // 🌟 2. 拖放文件：从电脑直接拖进软件，秒推送到手机！
        private async Task DownloadFileToDesktop(string fileName)
        {
            string remotePath = _currentAndroidPath + fileName;
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string localPath = System.IO.Path.Combine(desktopPath, fileName);

            LoadingOverlay.Visibility = Visibility.Visible;
            TxtLoadingHint.Text = $"正在将 {fileName}\n高速拉取至电脑桌面...";

            await Task.Run(() => AdbHelper.ExecuteCommand($"-s {_activeSerial} pull \"{remotePath}\" \"{localPath}\""));

            LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        // 🌟 核心：调用 ADB ls 命令并硬核解析输出 (帮你把误删的代码接回来了)
        private async Task LoadDirectoryAsync(string path)
        {
            Dispatcher.Invoke(() => TxtCurrentPath.Text = path); 
            
            // 🌟 动态构建底层 ADB 命令
            string lsArg = _fmShowHidden ? "ls -al" : "ls -l"; // 如果开启隐藏文件，追加 -a 参数
            string shellCmd = $"LANG=C.UTF-8 {lsArg} '{path}'";

            // 🌟 注入 Root 灵魂
            if (_fmUseRoot)
            {
                // 用 su -c 将整个命令包裹起来，以 Root 身份强行执行！
                shellCmd = $"su -c \"{shellCmd}\"";
            }

            string result = await Task.Run(() => AdbHelper.ExecuteCommand($"-s {_activeSerial} shell \"{shellCmd}\""));
            
            List<AndroidFile> fileList = new List<AndroidFile>();
            string[] lines = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.StartsWith("total ") || line.Contains("Permission denied") || line.Contains("No such file or directory")) continue;

                string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 6)
                {
                    bool isDir = parts[0].StartsWith("d");
                    string size = isDir ? "" : FormatSize(parts[4]); 
                    string date = parts.Length >= 7 ? $"{parts[5]} {parts[6]}" : "";
                    
                    int nameStartIndex = line.IndexOf(parts.Length >= 7 ? parts[6] : parts[5]) + (parts.Length >= 7 ? parts[6].Length : parts[5].Length);
                    string name = line.Substring(nameStartIndex).Trim();

                    fileList.Add(new AndroidFile { Name = name, IsDirectory = isDir, Size = size, Date = date });
                }
            }

            fileList.Sort((a, b) =>
            {
                if (a.IsDirectory && !b.IsDirectory) return -1;
                if (!a.IsDirectory && b.IsDirectory) return 1;
                return a.Name.CompareTo(b.Name);
            });

            Dispatcher.Invoke(() => { ListFiles.ItemsSource = fileList; });
        }

        // 字节大小转换器
        private string FormatSize(string bytesStr)
        {
            if (long.TryParse(bytesStr, out long bytes))
            {
                string[] sizes = { "B", "KB", "MB", "GB" };
                int order = 0;
                double len = bytes;
                while (len >= 1024 && order < sizes.Length - 1) { order++; len = len / 1024; }
                return $"{len:0.##} {sizes[order]}";
            }
            return bytesStr;
        }

        // 🌟 新增：新建文件夹与文件的交互逻辑
        private void BtnShowNewItemPopup_Click(object sender, RoutedEventArgs e)
        {
            InputNewItemName.Text = ""; // 每次打开清空输入框
            ShowPopup(PanelNewItem); // 复用我们漂亮的高级弹窗
        }

        private async void ListFiles_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    LoadingOverlay.Visibility = Visibility.Visible;
                    TxtLoadingHint.Text = $"正在极速推送 {files.Length} 个文件到手机...\n大文件请耐心等待";

                    foreach (string localFilePath in files)
                    {
                        await Task.Run(() => AdbHelper.ExecuteCommand($"-s {_activeSerial} push \"{localFilePath}\" \"{_currentAndroidPath}\""));
                    }

                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    _ = LoadDirectoryAsync(_currentAndroidPath); 
                }
            }
        }
        private async void BtnConfirmNewItem_Click(object sender, RoutedEventArgs e)
        {
            string name = InputNewItemName.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;

            HidePopup();
            LoadingOverlay.Visibility = Visibility.Visible;
            TxtLoadingHint.Text = $"正在创建 {name} ...";

            string targetPath = _currentAndroidPath + name;
            
            if (name.Contains(".")) await Task.Run(() => AdbHelper.ExecuteCommand($"-s {_activeSerial} shell touch \"{targetPath}\""));
            else await Task.Run(() => AdbHelper.ExecuteCommand($"-s {_activeSerial} shell mkdir \"{targetPath}\""));

            LoadingOverlay.Visibility = Visibility.Collapsed;
            _ = LoadDirectoryAsync(_currentAndroidPath);
        }

        private async void MenuDownload_Click(object sender, RoutedEventArgs e)
        {
            if (ListFiles.SelectedItem is AndroidFile selectedItem && !selectedItem.IsDirectory)
            {
                await DownloadFileToDesktop(selectedItem.Name);
            }
        }

        // 🌟 新增：右键菜单 - 删除
        private async void MenuDelete_Click(object sender, RoutedEventArgs e)
        {
            if (ListFiles.SelectedItem is AndroidFile selectedItem)
            {
                var result = MessageBox.Show($"⚠️ 极客警告\n\n确定要永久删除 【{selectedItem.Name}】 吗？\n这是底层 ADB 物理删除，将无法在回收站中找回！", 
                    "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                
                if (result == MessageBoxResult.Yes)
                {
                    LoadingOverlay.Visibility = Visibility.Visible;
                    TxtLoadingHint.Text = $"正在物理抹除 {selectedItem.Name} ...";

                    await Task.Run(() => AdbHelper.ExecuteCommand($"-s {_activeSerial} shell rm -rf \"{_currentAndroidPath}{selectedItem.Name}\""));

                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    _ = LoadDirectoryAsync(_currentAndroidPath);
                }
            }
        }

        private void BtnRebootMenu_Click(object sender, RoutedEventArgs e)
        {
            RebootMenuPopup.IsOpen = true;
        }

        private async void MenuRebootNormal_Click(object sender, RoutedEventArgs e)
        {
            RebootMenuPopup.IsOpen = false;
            await ExecuteRebootCommand("reboot", "正在重启设备...");
        }

        private async void MenuRebootFastboot_Click(object sender, RoutedEventArgs e)
        {
            RebootMenuPopup.IsOpen = false;
            await ExecuteRebootCommand("reboot bootloader", "正在重启至 Fastboot 模式...");
        }

        private async void MenuRebootRecovery_Click(object sender, RoutedEventArgs e)
        {
            RebootMenuPopup.IsOpen = false;
            await ExecuteRebootCommand("reboot recovery", "正在重启至 Recovery 模式...");
        }

        private async void MenuRebootEDL_Click(object sender, RoutedEventArgs e)
        {
            RebootMenuPopup.IsOpen = false;
            if (MessageBox.Show("警告：重启至 9008 (EDL) 模式通常用于深度刷机/救砖，手机屏幕会完全黑屏无反应。部分机型可能需要拆机短接。\n\n您确定要继续吗？", "危险操作警告", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                await ExecuteRebootCommand("reboot edl", "正在尝试重启至 9008 (EDL) 模式...");
            }
        }

        public void ShowSystemNotification(string title, string text)
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.ShowBalloonTip(5000, title, text, System.Windows.Forms.ToolTipIcon.Info);
            }
        }

        private async Task ExecuteRebootCommand(string command, string statusMessage)
        {
            if (string.IsNullOrEmpty(_activeSerial))
            {
                MessageBox.Show("请先连接一台设备！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                await Task.Run(() => AdbHelper.ExecuteCommand($"-s {_activeSerial} {command}"));
                MessageBox.Show($"{statusMessage}\n指令已发送，设备可能会断开连接。", "操作成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"发送重启指令失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

 // ================= 文件管理器数据模型 =================
    public class AndroidFile
    {
        public string Name { get; set; }
        public string Size { get; set; }
        public string Date { get; set; }
        public bool IsDirectory { get; set; }
        
        public string Icon => IsDirectory ? "📁" : "📄";
        public SolidColorBrush TextColor => IsDirectory ? new SolidColorBrush(Color.FromRgb(220, 220, 220)) : new SolidColorBrush(Color.FromRgb(170, 170, 170));
    }
}
}