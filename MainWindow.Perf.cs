using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using LengJiaoConnect.Services;

namespace LengJiaoConnect;

// 设备性能采样：只在主窗口可见时跑，隐藏/最小化立即停，避免无意义地唤醒手机。
public partial class MainWindow
{
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
        _perfTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _perfTimer.Tick += async (_, _) => await RefreshPerfAsync();
        _perfTimer.Start();
        _ = RefreshPerfAsync();
    }

    private async Task RefreshPerfAsync()
    {
        if (_perfBusy) return;

        if (string.IsNullOrEmpty(_serial) || !IsVisible || WindowState == Avalonia.Controls.WindowState.Minimized)
        {
            SetPerfIdle();
            return;
        }

        _perfBusy = true;
        try
        {
            const string cmd =
                "cat /proc/stat | head -n 1; cat /proc/meminfo | head -n 3; df /data | tail -n 1; dumpsys battery | grep -E 'level|temperature'; cat /proc/net/dev";
            var r = await Adb.RunAsync(new[] { "-s", _serial, "shell", cmd }, 8000);
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
            long rx = 0, tx = 0;
            long memTotal = 0, memAvail = 0;

            foreach (var raw in r.StdOut.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("cpu ", StringComparison.Ordinal))
                {
                    var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 5)
                    {
                        double total = 0;
                        for (var i = 1; i < f.Length; i++)
                            if (double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                                total += v;

                        var idle = double.Parse(f[4], CultureInfo.InvariantCulture) +
                                   double.Parse(f[5], CultureInfo.InvariantCulture);

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
                else if (line.StartsWith("wlan0:", StringComparison.Ordinal))
                {
                    var f = line[6..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 9)
                    {
                        long.TryParse(f[0], out rx);
                        long.TryParse(f[8], out tx);
                    }
                }
                else if (line.StartsWith("level:", StringComparison.Ordinal))
                {
                    if (int.TryParse(line[6..].Trim(), out var lv)) level = lv;
                }
                else if (line.StartsWith("temperature:", StringComparison.Ordinal))
                {
                    if (double.TryParse(line[12..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                        temp = t / 10.0;
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

            TxtPerfMem.Text = double.IsNaN(memPct) ? "—" : $"{memPct:0}%";
            BarMem.Value = double.IsNaN(memPct) ? 0 : memPct;

            TxtPerfDisk.Text = disk.HasValue ? $"{disk.Value:0}%" : "—";
            BarDisk.Value = disk ?? 0;

            TxtPerfTemp.Text = temp.HasValue ? $"{temp.Value:0.0}°C" : "—";
            BarTemp.Value = temp ?? 0;

            if (memTotal > 0)
                ToolTip.SetTip(TxtPerfMem, $"{(memTotal - memAvail) / 1024.0 / 1024.0:0.0} GB / {memTotal / 1024.0 / 1024.0:0.0} GB 已用");

            // 网速：两次采样差分
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

            TxtPerfHint.Text = level.HasValue ? $"电量 {level}%" : "";
            PerfDot.Fill = new SolidColorBrush(Color.Parse("#6FD3FF"));
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

    private void SetPerfIdle()
    {
        TxtPerfHint.Text = string.IsNullOrEmpty(_serial) ? "等待设备" : "已暂停";
        TxtNetDown.Text = "—"; TxtNetUp.Text = "—";
        _netPrimed = false;
        PerfDot.Fill = new SolidColorBrush(Color.Parse("#3A4A47"));
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
