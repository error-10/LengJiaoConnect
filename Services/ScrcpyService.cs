using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace LengJiaoConnect.Services;

/// <summary>
/// scrcpy 子进程管理。旧实现里"死等 MainWindowHandle"会永久占线程、
/// 退出回调还会在程序关闭后 Dispatcher.Invoke 崩溃——这里全部按超时+事件处理。
/// </summary>
public sealed class ScrcpyService : IDisposable
{
    private readonly Dictionary<string, Process> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IntPtr> _originalLayouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _rotationBefore = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadKeyboardLayoutW(string pwszKLID, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>把 scrcpy 窗口的输入法切到 US 布局，避免中文输入法截走按键。</summary>
    private static void ApplyUsKeyboard(Process process)
    {
        try
        {
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero) return;
            var hkl = LoadKeyboardLayoutW("00000409", 1);
            if (hkl == IntPtr.Zero) return;
            SendMessageW(handle, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
        }
        catch (Exception ex)
        {
            Log.Warn("scrcpy.IME", ex.Message);
        }
    }

    private void RestoreKeyboard(string serial)
    {
        if (!_originalLayouts.Remove(serial, out var hkl)) return;
        try { ActivateKeyboardLayout(hkl, 0); } catch { }
    }

    private static async Task<int> ReadAccelRotationAsync(string serial)
    {
        try
        {
            var text = await Adb.ShellTextAsync(serial, "settings get system accelerometer_rotation", 5000);
            return int.TryParse(text.Trim(), out var value) ? value : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>部分 ROM 在投屏/息屏时会把自动旋转打开，这里按投屏前的状态恢复。</summary>
    private async Task RestoreAccelRotationAsync(string serial)
    {
        if (!_rotationBefore.TryGetValue(serial, out var before) || before < 0) return;
        try
        {
            var now = await ReadAccelRotationAsync(serial);
            if (now < 0 || now == before) return;
            await Adb.ShellAsync(serial, "settings put system accelerometer_rotation " + before, 5000);
            Log.Info("scrcpy.rotation", "自动旋转被投屏改动（" + before + " → " + now + "），已恢复");
        }
        catch (Exception ex)
        {
            Log.Warn("scrcpy.rotation", ex.Message);
        }
    }

    /// <summary>serial, 文案, 是否错误</summary>
    public event Action<string, string, bool>? StatusChanged;

    public bool IsRunning(string serial)
    {
        lock (_gate)
        {
            if (!_running.TryGetValue(serial, out var p)) return false;
            try
            {
                if (!p.HasExited) return true;
            }
            catch { }
            _running.Remove(serial);
            return false;
        }
    }

    private static string ExePath => Path.Combine(AppContext.BaseDirectory, "Tools", "scrcpy", "scrcpy.exe");

    public async Task<bool> StartAsync(string serial, AppSettings settings)
    {
        if (IsRunning(serial))
        {
            StatusChanged?.Invoke(serial, "投屏已在运行", false);
            return true;
        }

        if (!File.Exists(ExePath))
        {
            StatusChanged?.Invoke(serial, "找不到 scrcpy：" + ExePath, true);
            return false;
        }

        var args = new List<string>
        {
            "-s", serial,
            "--window-title", "棱角互联 · " + serial
        };

        if (settings.CastScreenOff) args.Add("--turn-screen-off");
        if (!settings.CastAudio) args.Add("--no-audio");
        if (!settings.CastControl)
        {
            args.Add("--no-control");
        }
        else if (settings.CastUhidKeyboard)
        {
            args.Add("--keyboard=uhid");
        }

        switch (settings.Quality)
        {
            case 0:
                args.AddRange(new[] { "-b", "2M", "-m", "800", "--max-fps", "30" });
                break;
            case 2:
                args.AddRange(new[] { "-b", "24M", "--max-fps", "120" });
                break;
            default:
                args.AddRange(new[] { "-b", "8M", "-m", "1600", "--max-fps", "60" });
                break;
        }

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _rotationBefore[serial] = await ReadAccelRotationAsync(serial);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var errorTail = "";
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            errorTail = e.Data;
            Log.Warn("scrcpy", e.Data);
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info("scrcpy", e.Data);
        };
        process.Exited += (_, _) =>
        {
            lock (_gate) _running.Remove(serial);
            RestoreKeyboard(serial);
            _ = RestoreAccelRotationAsync(serial);
            StatusChanged?.Invoke(serial, "投屏已结束", false);
        };

        _originalLayouts[serial] = GetKeyboardLayout(0);

        try
        {
            if (!process.Start())
            {
                StatusChanged?.Invoke(serial, "scrcpy 启动失败", true);
                return false;
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            lock (_gate) _running[serial] = process;
        }
        catch (Exception ex)
        {
            Log.Error("scrcpy.Start", ex);
            StatusChanged?.Invoke(serial, "scrcpy 启动失败：" + ex.Message, true);
            return false;
        }

        // 最多等 12 秒窗口出现；不占用 UI 线程，也不会无限等。
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(100).ConfigureAwait(false);
            try
            {
                if (process.HasExited)
                {
                    StatusChanged?.Invoke(serial,
                        "scrcpy 已退出" + (string.IsNullOrWhiteSpace(errorTail) ? "" : "：" + errorTail), true);
                    return false;
                }
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    ApplyUsKeyboard(process);
                    _ = RestoreAccelRotationAsync(serial);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(2500).ConfigureAwait(false);
                        await RestoreAccelRotationAsync(serial).ConfigureAwait(false);
                        await Task.Delay(3500).ConfigureAwait(false);
                        await RestoreAccelRotationAsync(serial).ConfigureAwait(false);
                    });
                    StatusChanged?.Invoke(serial, "投屏已启动", false);
                    return true;
                }
            }
            catch { }
        }

        StatusChanged?.Invoke(serial, "投屏窗口没有出现（设备可能未授权投屏）", true);
        return true;
    }

    public void Stop(string serial)
    {
        Process? process;
        lock (_gate)
        {
            if (!_running.TryGetValue(serial, out process)) return;
            _running.Remove(serial);
        }
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warn("scrcpy.Stop", ex.Message);
        }
    }

    public void StopAll()
    {
        List<string> serials;
        lock (_gate) serials = _running.Keys.ToList();
        foreach (var serial in serials) Stop(serial);
    }

    public void Dispose() => StopAll();
}
