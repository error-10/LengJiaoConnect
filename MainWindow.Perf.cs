using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using LengJiaoConnect.Services;

namespace LengJiaoConnect;

// 设备性能采样：只在主窗口可见时跑，隐藏/最小化立即停，避免无意义地唤醒手机。
public partial class MainWindow
{
    private static readonly IBrush PerfBrushCool = new SolidColorBrush(Color.Parse("#6FD3FF"));
    private static readonly IBrush PerfBrushMem = new SolidColorBrush(Color.Parse("#A8C7FF"));
    private static readonly IBrush PerfBrushWarn = new SolidColorBrush(Color.Parse("#FFD166"));
    private static readonly IBrush PerfBrushHot = new SolidColorBrush(Color.Parse("#FF6B6B"));
    private static readonly IBrush PerfBrushIdle = new SolidColorBrush(Color.Parse("#3A4A47"));

    private DispatcherTimer? _perfTimer;
    private bool _perfBusy;
    private double _cpuLastTotal;
    private double _cpuLastIdle;
    private long _netLastRx;
    private long _netLastTx;
    private DateTime _netLastAt;
    private bool _netPrimed;

    private void StartPerfTimer()
    {
        _perfTimer?.Stop();
        _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _perfTimer.Tick += async (_, _) => await RefreshPerfAsync();
        _perfTimer.Start();
        _ = RefreshPerfAsync();
    }

    private void ResetPerfCounters()
    {
        _cpuLastTotal = 0;
        _cpuLastIdle = 0;
        _netLastRx = 0;
        _netLastTx = 0;
        _netLastAt = default;
        _netPrimed = false;

        TxtPerfCpu.Text = "—"; BarCpu.Value = 0;
        TxtPerfMem.Text = "—"; BarMem.Value = 0;
        TxtPerfDisk.Text = "—"; BarDisk.Value = 0;
        TxtPerfTemp.Text = "—"; BarTemp.Value = 0;
        SetPerfIdle();
    }

    private async Task RefreshPerfAsync()
    {
        if (_perfBusy) return;

        var serial = _serial;
        var epoch = _deviceEpoch;
        if (string.IsNullOrEmpty(serial) || !IsVisible || WindowState == WindowState.Minimized)
        {
            SetPerfIdle();
            return;
        }

        _perfBusy = true;
        try
        {
            const string cmd =
                "cat /proc/stat | head -n 1; cat /proc/meminfo | head -n 3; df -P /data | tail -n 1; dumpsys battery | grep -E 'level|status|temperature'; cat /proc/net/dev";
            var r = await Adb.RunAsync(new[] { "-s", serial, "shell", cmd }, 8000);
            if (_deviceEpoch != epoch || !string.Equals(serial, _serial, StringComparison.Ordinal))
                return;

            if (!r.Ok || string.IsNullOrWhiteSpace(r.StdOut))
            {
                SetPerfIdle();
                return;
            }

            double? cpu = null;
            double memPct = double.NaN;
            double? disk = null;
            double? temp = null;
            int? level = null;
            bool charging = false;
            long rx = 0, tx = 0;
            long memTotal = 0, memAvail = 0;

            foreach (var raw in r.StdOut.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("cpu ", StringComparison.Ordinal))
                {
                    var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 6 &&
                        double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var idle4) &&
                        double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var iowait5))
                    {
                        double total = 0;
                        for (var i = 1; i < f.Length; i++)
                            if (double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                                total += v;

                        var idle = idle4 + iowait5;
                        if (_cpuLastTotal > 0 && total > _cpuLastTotal)
                        {
                            var dTotal = total - _cpuLastTotal;
                            var dIdle = idle - _cpuLastIdle;
                            cpu = Math.Clamp((1 - dIdle / dTotal) * 100, 0, 100);
                        }

                        _cpuLastTotal = total;
                        _cpuLastIdle = idle;
                    }
                }
                else if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    memTotal = ParseKb(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    memAvail = ParseKb(line);
                }
                else if (line.StartsWith("level:", StringComparison.Ordinal))
                {
                    if (int.TryParse(line[6..].Trim(), out var lv)) level = lv;
                }
                else if (line.StartsWith("status:", StringComparison.Ordinal))
                {
                    var st = line[7..].Trim();
                    charging = st == "2" || st == "5";
                }
                else if (line.StartsWith("temperature:", StringComparison.Ordinal))
                {
                    if (double.TryParse(line[12..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                        temp = t / 10.0;
                }
                else if (IsTrafficInterface(line, out var ifaceColon))
                {
                    var f = line[(ifaceColon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 9 &&
                        long.TryParse(f[0], out var ifRx) &&
                        long.TryParse(f[8], out var ifTx))
                    {
                        rx += ifRx;
                        tx += ifTx;
                    }
                }
                else if (line.Contains('%'))
                {
                    var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var token in f)
                    {
                        if (token.EndsWith("%", StringComparison.Ordinal) &&
                            double.TryParse(token[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                        {
                            disk = p;
                            break;
                        }
                    }
                }
            }

            if (memTotal > 0 && memAvail > 0)
                memPct = (1.0 - (double)memAvail / memTotal) * 100;

            TxtPerfCpu.Text = cpu.HasValue ? $"{cpu.Value:0}%" : "—";
            BarCpu.Value = cpu ?? 0;
            BarCpu.Foreground = LoadColor(cpu ?? 0, PerfBrushCool);

            TxtPerfMem.Text = double.IsNaN(memPct) ? "—" : $"{memPct:0}%";
            BarMem.Value = double.IsNaN(memPct) ? 0 : memPct;
            BarMem.Foreground = LoadColor(double.IsNaN(memPct) ? 0 : memPct, PerfBrushMem);

            TxtPerfDisk.Text = disk.HasValue ? $"{disk.Value:0}%" : "—";
            BarDisk.Value = disk ?? 0;
            BarDisk.Foreground = LoadColor(disk ?? 0, PerfBrushWarn, warnAt: 75, hotAt: 90);

            TxtPerfTemp.Text = temp.HasValue ? $"{temp.Value:0.0}°C" : "—";
            BarTemp.Value = temp ?? 0;
            BarTemp.Foreground = temp switch
            {
                >= 42 => PerfBrushHot,
                >= 37 => PerfBrushWarn,
                _ => PerfBrushCool
            };

            if (memTotal > 0)
                ToolTip.SetTip(TxtPerfMem, $"{(memTotal - memAvail) / 1024.0 / 1024.0:0.0} GB / {memTotal / 1024.0 / 1024.0:0.0} GB 已用");

            if (level.HasValue)
                SetBatteryVisual(level.Value, charging);

            // 网速：两次采样差分（汇总 wlan / 蜂窝 / USB 共享等活跃网卡）
            if (rx > 0 || tx > 0)
            {
                var now = DateTime.UtcNow;
                if (_netPrimed && _netLastAt != default)
                {
                    var secs = (now - _netLastAt).TotalSeconds;
                    if (secs > 0.2)
                    {
                        var down = Math.Max(0, (rx - _netLastRx) / secs);
                        var up = Math.Max(0, (tx - _netLastTx) / secs);
                        TxtNetDown.Text = FormatSpeed(down);
                        TxtNetUp.Text = FormatSpeed(up);
                    }
                }
                _netLastRx = rx; _netLastTx = tx; _netLastAt = now; _netPrimed = true;
            }

            TxtPerfHint.Text = level.HasValue ? $"实时 · 电量 {level}%" : "实时采样";
            PerfDot.Fill = PerfBrushCool;
        }
        catch (Exception ex)
        {
            Log.Warn("性能采样失败：" + ex.Message);
            SetPerfIdle();
        }
        finally
        {
            _perfBusy = false;
        }
    }

    private static bool IsTrafficInterface(string line, out int colonIndex)
    {
        colonIndex = line.IndexOf(':');
        if (colonIndex <= 0) return false;
        var name = line[..colonIndex];
        return name.StartsWith("wlan", StringComparison.Ordinal) ||
               name.StartsWith("rmnet", StringComparison.Ordinal) ||
               name.StartsWith("ccmni", StringComparison.Ordinal) ||
               name.StartsWith("rndis", StringComparison.Ordinal) ||
               name.StartsWith("ncm", StringComparison.Ordinal) ||
               name.StartsWith("eth", StringComparison.Ordinal);
    }

    private static IBrush LoadColor(double pct, IBrush normal, double warnAt = 68, double hotAt = 85) =>
        pct >= hotAt ? PerfBrushHot : (pct >= warnAt ? PerfBrushWarn : normal);

    private void SetPerfIdle()
    {
        TxtPerfHint.Text = string.IsNullOrEmpty(_serial) ? "等待设备" : "已暂停";
        TxtNetDown.Text = "—"; TxtNetUp.Text = "—";
        _netPrimed = false;
        PerfDot.Fill = PerfBrushIdle;
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec:0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:0} KB/s";
        return $"{bytesPerSec / 1024 / 1024:0.0} MB/s";
    }

    private static long ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb : 0;
    }
}
