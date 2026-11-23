using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LengJiaoConnect.Models;

namespace LengJiaoConnect.Services;

/// <summary>
/// 与手机端 HelperService 的常驻通道。
/// 协议：一行一个 JSON（\n 分帧，UTF-8），彻底告别"一次 read 当一条命令"的老毛病。
/// </summary>
public sealed class HelperClient : IDisposable
{
    private const int Port = 8889;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private Task? _pingTask;
    private int _disconnectedRaised;

    public string Serial { get; }
    public bool IsConnected => _tcp?.Connected == true && _disposed == false;
    public DateTime LastSeen { get; private set; } = DateTime.MinValue;

    private bool _disposed;

    public event Action<string>? ClipboardReceived;
    public event Action<string, string, string>? NotificationReceived;
    public event Action<string, long, string>? SmsReceived;
    public event Action<string, string, string>? AppReceived;
    public event Action<int>? AppsCompleted;
    public event Action<MediaInfo>? MediaChanged;
    public event Action<bool, bool>? PermissionChanged;
    public event EventHandler? Disconnected;

    public HelperClient(string serial) => Serial = serial;

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return true;

        await Adb.RunAsync(new[] { "-s", Serial, "forward", "--remove", $"tcp:{Port}" }, 5000, ct)
            .ConfigureAwait(false);
        var forward = await Adb.RunAsync(
            new[] { "-s", Serial, "forward", $"tcp:{Port}", "localabstract:lengjiao_helper" }, 8000, ct)
            .ConfigureAwait(false);
        if (!forward.Ok)
        {
            Log.Warn("Helper", "adb forward 失败: " + forward.Best);
            return false;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var tcp = new TcpClient { NoDelay = true };
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(1500);
                await tcp.ConnectAsync(IPAddress.Loopback, Port, connectCts.Token).ConfigureAwait(false);

                var stream = tcp.GetStream();
                _reader = new StreamReader(stream, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
                _writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };
                _tcp = tcp;
                _cts = new CancellationTokenSource();
                Interlocked.Exchange(ref _disconnectedRaised, 0);
                LastSeen = DateTime.Now;
                _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
                _pingTask = Task.Run(() => PingLoopAsync(_cts.Token));
                Log.Info("Helper", $"已连接 {Serial}:{Port}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Helper", $"连接尝试 {attempt + 1}/8 失败: {ex.Message}");
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
        }
        return false;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _reader != null)
            {
                var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                if (line.Length == 0) continue;
                LastSeen = DateTime.Now;
                Dispatch(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Helper.Read", ex.Message);
        }
        finally
        {
            RaiseDisconnected();
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(20000, ct).ConfigureAwait(false);
                await SendAsync(new { t = "ping" }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Helper.Ping", ex.Message);
            RaiseDisconnected();
        }
    }

    private void Dispatch(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var typeProp)) return;

            switch (typeProp.GetString())
            {
                case "clip":
                    ClipboardReceived?.Invoke(Str(root, "text"));
                    break;
                case "notif":
                    NotificationReceived?.Invoke(Str(root, "app"), Str(root, "title"), Str(root, "text"));
                    break;
                case "sms":
                    SmsReceived?.Invoke(Str(root, "addr"), Long(root, "date"), Str(root, "body"));
                    break;
                case "app":
                    AppReceived?.Invoke(Str(root, "pkg"), Str(root, "name"), Str(root, "icon"));
                    break;
                case "apps_end":
                    AppsCompleted?.Invoke((int)Long(root, "count"));
                    break;
                case "media":
                    MediaChanged?.Invoke(new MediaInfo
                    {
                        Title = Str(root, "title"),
                        Artist = Str(root, "artist"),
                        Playing = Bool(root, "playing"),
                        Package = Str(root, "pkg")
                    });
                    break;
                case "perm":
                    PermissionChanged?.Invoke(Bool(root, "sms"), Bool(root, "notif"));
                    break;
                case "pong":
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Helper.Dispatch", "无法解析消息: " + line[..Math.Min(120, line.Length)] + " / " + ex.Message);
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public async Task SendAsync(object payload, CancellationToken ct = default)
    {
        if (!IsConnected || _writer == null) return;
        var json = JsonSerializer.Serialize(payload);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public Task SendClipAsync(string text) => SendAsync(new { t = "clip", text });

    public Task RequestAppsAsync() => SendAsync(new { t = "sync", what = "apps" });

    public Task RequestSmsAsync() => SendAsync(new { t = "sync", what = "sms" });

    public Task RequestMediaAsync() => SendAsync(new { t = "sync", what = "media" });

    public Task CheckPermissionAsync() => SendAsync(new { t = "perm" });

    public Task MediaCommandAsync(string cmd) => SendAsync(new { t = "media", cmd });

    public Task OpenPermissionPageAsync() => SendAsync(new { t = "perm_page" });

    private void RaiseDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnectedRaised, 1) == 1) return;
        DisposeTransport();
        Log.Warn("Helper", "连接已断开: " + Serial);
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeTransport()
    {
        try { _cts?.Cancel(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _reader = null;
        _writer = null;
        _tcp = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeTransport();
        _ = Adb.RunAsync(new[] { "-s", Serial, "forward", "--remove", $"tcp:{Port}" }, 4000);
        _sendLock.Dispose();
    }
}
