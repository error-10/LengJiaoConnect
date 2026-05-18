using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace LengJiaoConnect
{
    public class HelperApkManager
    {
        private TcpClient _client;
        private NetworkStream _stream;
        private string _serial;
        private bool _isRunning;
        private MainWindow _mainWindow;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        private IntPtr _hwnd;
        private HwndSource _hwndSource;
        private bool _ignoreNextPcClip = false;

        public bool SyncClipboard = true;
        public bool SyncNotifications = true;

        public bool IsRunning => _isRunning;

        public HelperApkManager(MainWindow mainWindow, string serial)
        {
            _mainWindow = mainWindow;
            _serial = serial;
        }

        public async Task<bool> InstallAsync()
        {
            string apkPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "HelperApk", "LengJiaoHelper.apk");
            if (!File.Exists(apkPath)) apkPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HelperApk", "LengJiaoHelper.apk");
            
            string installRes = await Task.Run(() => AdbHelper.ExecuteCommand($"-s {_serial} install -t -g \"{apkPath}\""));
            if (installRes.Contains("Failure") || installRes.Contains("failed"))
            {
                return false;
            }
            return true;
        }

        public async Task<bool> ConnectAsync()
        {
            if (_isRunning) return true;

            // 1. Setup Socket Forwarding
            AdbHelper.ExecuteCommand($"-s {_serial} forward tcp:8889 localabstract:lengjiao_helper");

            // 2. Try to connect
            for (int i = 0; i < 8; i++)
            {
                try
                {
                    _client = new TcpClient("127.0.0.1", 8889);
                    _client.Client.Blocking = true;
                    
                    await Task.Delay(200); 

                    if (_client.Client.Poll(100, SelectMode.SelectRead))
                    {
                        byte[] peekBuf = new byte[1];
                        if (_client.Client.Receive(peekBuf, SocketFlags.Peek) == 0)
                        {
                            _client.Close();
                            throw new Exception("ADB Socket closed immediately (Service not running)");
                        }
                    }

                    _stream = _client.GetStream();
                    _isRunning = true;
                    break;
                }
                catch
                {
                    await Task.Delay(2000);
                }
            }

            if (!_isRunning)
            {
                return false;
            }

            // 3. Start receiving messages
            _ = ReadFromHelperAsync();

            // 4. Hook PC Clipboard
            _mainWindow.Dispatcher.Invoke(() =>
            {
                _hwnd = new WindowInteropHelper(_mainWindow).EnsureHandle();
                _hwndSource = HwndSource.FromHwnd(_hwnd);
                _hwndSource.AddHook(HwndHook);
                AddClipboardFormatListener(_hwnd);
            });
            
            return true;
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;

            try
            {
                _mainWindow.Dispatcher.Invoke(() =>
                {
                    RemoveClipboardFormatListener(_hwnd);
                    _hwndSource?.RemoveHook(HwndHook);
                });
            }
            catch { }

            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            Task.Run(() => AdbHelper.ExecuteCommand($"-s {_serial} forward --remove tcp:8889"));
        }

        private void SendToPhone(string text)
        {
            if (!_isRunning || _stream == null || !SyncClipboard) return;
            try
            {
                string payload = "SET_CLIP:" + text;
                byte[] data = Encoding.UTF8.GetBytes(payload);
                _stream.Write(data, 0, data.Length);
                _stream.Flush();
            }
            catch { }
        }

        private async Task ReadFromHelperAsync()
        {
            byte[] buffer = new byte[65536];
            StringBuilder sb = new StringBuilder();

            try
            {
                while (_isRunning)
                {
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    sb.Append(chunk);

                    string data = sb.ToString();
                    int nullIndex;
                    while ((nullIndex = data.IndexOf('\0')) != -1)
                    {
                        string message = data.Substring(0, nullIndex);
                        data = data.Substring(nullIndex + 1);
                        sb.Clear();
                        sb.Append(data);

                        if (message.StartsWith("CLIP_EVENT:") && SyncClipboard)
                        {
                            string phoneClip = message.Substring(11);
                            _mainWindow.Dispatcher.Invoke(() =>
                            {
                                try
                                {
                                    _ignoreNextPcClip = true;
                                    Clipboard.SetText(phoneClip);
                                }
                                catch { }
                            });
                        }
                        else if (message.StartsWith("NOTIF_EVENT:") && SyncNotifications)
                        {
                            string notifData = message.Substring(12);
                            string[] parts = notifData.Split(new[] { '|' }, 3);
                            if (parts.Length == 3)
                            {
                                string appName = parts[0];
                                string title = parts[1];
                                string text = parts[2];

                                _mainWindow.Dispatcher.Invoke(() =>
                                {
                                    if (title.Length > 0 || text.Length > 0)
                                    {
                                        string displayTitle = string.IsNullOrEmpty(title) ? appName : $"{appName} | {title}";
                                        _mainWindow.ShowSystemNotification(displayTitle, text);
                                    }
                                });
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                Stop();
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                if (_ignoreNextPcClip)
                    _ignoreNextPcClip = false;
                else
                {
                    try
                    {
                        if (Clipboard.ContainsText())
                            SendToPhone(Clipboard.GetText());
                    }
                    catch { }
                }
            }
            return IntPtr.Zero;
        }
    }
}
