using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using LengJiaoConnect.Models;

namespace LengJiaoConnect.Services;

public sealed record AdbResult(int ExitCode, string StdOut, string StdErr, bool TimedOut = false)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
    public string All => (StdOut + "\n" + StdErr).Trim();
    public string Best => string.IsNullOrWhiteSpace(StdOut) ? StdErr.Trim() : StdOut.Trim();
}

/// <summary>
/// adb 进程封装。要点：
/// 1) 一律用 ArgumentList 传参，不再手拼引号（中文/空格路径不会再被截断）；
/// 2) 每条命令都有超时（含 stdout/stderr 管道读取），卡死就杀进程树，绝不永久阻塞线程；
/// 3) 输出按 UTF-8 解码，中文文件名不再乱码。
/// </summary>
public static class Adb
{
    public static string AdbPath { get; } = LocateAdb();

    private static string LocateAdb()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "Tools", "adb.exe");
        if (File.Exists(local)) return local;

        var fromPath = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.Combine(p.Trim(), "adb.exe"))
            .FirstOrDefault(File.Exists);
        return fromPath ?? "adb.exe";
    }

    public static async Task<AdbResult> RunAsync(IEnumerable<string> args, int timeoutMs = 20000,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = AdbPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return new AdbResult(-1, "", "无法启动 adb 进程");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                return new AdbResult(process.ExitCode, stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                var timedOut = !cancellationToken.IsCancellationRequested;
                return new AdbResult(-1, "", timedOut ? "adb 命令超时（" + timeoutMs + "ms）" : "adb 命令已取消", timedOut);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Adb.Run", string.Join(" ", args), ex);
            return new AdbResult(-1, "", ex.Message);
        }
    }

    public static Task<AdbResult> RunAsync(params string[] args) => RunAsync(args, 20000);

    // ---------- 设备 ----------

    public static async Task<(bool Ok, List<DeviceInfo> Devices)> TryGetDevicesAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(new[] { "devices", "-l" }, 8000, ct).ConfigureAwait(false);
        var list = new List<DeviceInfo>();
        if (!result.Ok)
            return (false, list);

        foreach (var raw in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices")) continue;
            var m = Regex.Match(line, @"^(?<serial>\S+)\s+(?<state>\S+)(?:\s+(?<rest>.*))?$");
            if (!m.Success) continue;

            var state = m.Groups["state"].Value switch
            {
                "device" => DeviceState.Device,
                "offline" => DeviceState.Offline,
                "unauthorized" => DeviceState.Unauthorized,
                _ => DeviceState.Unknown
            };
            var modelMatch = Regex.Match(m.Groups["rest"].Value, @"model:(\S+)");
            list.Add(new DeviceInfo
            {
                Serial = m.Groups["serial"].Value,
                State = state,
                Model = modelMatch.Success ? modelMatch.Groups[1].Value.Replace('_', ' ') : ""
            });
        }
        return (true, list);
    }

    public static async Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct = default)
    {
        var (_, devices) = await TryGetDevicesAsync(ct).ConfigureAwait(false);
        return devices;
    }

    public static Task<AdbResult> ConnectAsync(string endpoint) =>
        RunAsync(new[] { "connect", endpoint }, 12000);

    public static Task<AdbResult> DisconnectAsync(string endpoint) =>
        RunAsync(new[] { "disconnect", endpoint }, 8000);

    public static Task<AdbResult> PairAsync(string ip, string port, string code) =>
        RunAsync(new[] { "pair", $"{ip}:{port}", code }, 25000);

    // ---------- 设备内命令 ----------

    public static Task<AdbResult> ShellAsync(string serial, string command, int timeoutMs = 20000,
        CancellationToken ct = default) =>
        RunAsync(new[] { "-s", serial, "shell", command }, timeoutMs, ct);

    public static async Task<string> ShellTextAsync(string serial, string command, int timeoutMs = 20000)
    {
        var r = await ShellAsync(serial, command, timeoutMs).ConfigureAwait(false);
        return r.Best;
    }

    public static async Task<bool> IsInstalledAsync(string serial, string package)
    {
        var r = await ShellAsync(serial, $"pm path {Quote(package)}", 10000).ConfigureAwait(false);
        return r.StdOut.Contains("package:", StringComparison.Ordinal);
    }

    public static async Task<string> GetPropAsync(string serial, string prop)
    {
        var r = await ShellAsync(serial, $"getprop {Quote(prop)}", 8000).ConfigureAwait(false);
        return r.Best;
    }

    // ---------- 文件 ----------

    /// <summary>把任意字符串安全地塞进设备 sh 的单引号里。</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public static Task<AdbResult> PushAsync(string serial, string localPath, string remoteFullPath,
        CancellationToken ct = default) =>
        RunAsync(new[] { "-s", serial, "push", localPath, remoteFullPath }, 600000, ct);

    public static Task<AdbResult> PullAsync(string serial, string remotePath, string localFullPath,
        CancellationToken ct = default) =>
        RunAsync(new[] { "-s", serial, "pull", remotePath, localFullPath }, 600000, ct);

    // ---------- 安装 ----------

    public static async Task<InstallResult> InstallApkAsync(string serial, string apkPath,
        bool allowDowngrade = true, bool grantPermissions = false, CancellationToken ct = default)
    {
        if (!File.Exists(apkPath))
            return new InstallResult { Success = false, Message = "APK 文件不存在：" + apkPath };

        var args = new List<string> { "-s", serial, "install", "-r", "-t" };
        if (allowDowngrade) args.Add("-d");
        if (grantPermissions) args.Add("-g");
        args.Add(apkPath);

        var r = await RunAsync(args, 300000, ct).ConfigureAwait(false);
        var text = r.All;
        if (r.Ok && text.Contains("Success", StringComparison.OrdinalIgnoreCase))
            return new InstallResult { Success = true, Message = "安装成功" };

        var reason = Regex.Match(text, @"Failure\s*\[(?<r>[^\]]+)\]");
        var message = reason.Success ? reason.Groups["r"].Value : text;
        if (message.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE"))
            message = "签名不一致，需要先卸载已安装的版本";
        return new InstallResult { Success = false, Message = string.IsNullOrWhiteSpace(message) ? "安装失败" : message.Trim() };
    }
}
