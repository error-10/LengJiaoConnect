using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LengJiaoConnect.Models;

namespace LengJiaoConnect.Services;

/// <summary>
/// 与手机端 HelperService 的常驻通道。
/// 协议：一行一个 JSON（\n 分帧，UTF-8）。
/// 连接时强制 ping/pong 应用层握手，防止 adb forward 本地端口假连接引发无限重连风暴。
/// </summary>
public sealed class HelperClient : IDisposable
{
    private const int Port = 8889;

    // 串行化所有实例对 tcp:8889 的 adb forward 增删，防止旧实例 Dispose 拆掉新实例刚建好的转发。
    private static readonly SemaphoreSlim ForwardGate = new(1, 1);

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private Task? _pingTask;
    private int _disconnectedRaised;
    private volatile bool _disposed;

    public string Serial { get; }
    public bool IsConnected => _tcp?.Connected == true && !_disposed;
    public DateTime LastSeen { get; private set; } = DateTime.MinValue;

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
        if (_disposed) return false;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return false;
            return await ConnectCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<bool> ConnectCoreAsync(CancellationToken ct)
    {
        if (IsConnected) return true;

        bool forwarded;
        await ForwardGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Adb.RunAsync(new[] { "-s", Serial, "forward", "--remove", $"tcp:{Port}" }, 5000, ct)
                .ConfigureAwait(false);
            var forward = await Adb.RunAsync(
                new[] { "-s", Serial, "forward", $"tcp:{Port}", "localabstract:lengjiao_helper" }, 8000, ct)
                .ConfigureAwait(false);
            forwarded = forward.Ok;
            if (!forwarded)
                Log.Warn("Helper", "adb forward 失败: " + forward.Best);
        }
        finally
        {
            ForwardGate.Release();
        }

        if (!forwarded) return false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (_disposed) return false;
            ct.ThrowIfCancellationRequested();

            TcpClient? tcp = null;
            StreamReader? reader = null;
            StreamWriter? writer = null;
            try
            {
                tcp = new TcpClient { NoDelay = true };
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(1500);
                await tcp.ConnectAsync(IPAddress.Loopback, Port, connectCts.Token).ConfigureAwait(false);

                var stream = tcp.GetStream();
                reader = new StreamReader(stream, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
                writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                // adb forward 会先由本机 adb server 接下 TCP；必须做一次应用层 ping/pong 握手，
                // 确认手机侧 HelperService 真正在监听 localabstract:lengjiao_helper。
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeCts.CancelAfter(1500);
                await writer.WriteLineAsync("{\"t\":\"ping\"}".AsMemory(), handshakeCts.Token).ConfigureAwait(false);
                var firstLine = await reader.ReadLineAsync(handshakeCts.Token).ConfigureAwait(false);
                if (firstLine == null)
                    throw new IOException("手机端助手未响应（服务未启动或未授予通知使用权）");

                if (_disposed)
                {
                    reader.Dispose();
                    writer.Dispose();
                    tcp.Dispose();
                    return false;
                }

                _reader = reader;
                _writer = writer;
                _tcp = tcp;
                _cts = new CancellationTokenSource();
                Interlocked.Exchange(ref _disconnectedRaised, 0);
                LastSeen = DateTime.Now;
                if (firstLine.Length > 0) Dispatch(firstLine);

                _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
                _pingTask = Task.Run(() => PingLoopAsync(_cts.Token));
                Log.Info("Helper", $"已连接 {Serial}:{Port}");
                return true;
            }
            catch (Exception ex)
            {
                try { reader?.Dispose(); } catch { }
                try { writer?.Dispose(); } catch { }
                try { tcp?.Dispose(); } catch { }
                Log.Warn("Helper", $"连接尝试 {attempt + 1}/3 失败: {ex.Message}");
                if (attempt < 2) await Task.Delay(350, ct).ConfigureAwait(false);
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
                if ((DateTime.Now - LastSeen).TotalSeconds > 50)
                {
                    Log.Warn("Helper.Ping", "心跳超时（超过 50 秒无响应）");
                    RaiseDisconnected();
                    return;
                }
                await SendAsync(new { t = "ping" }, ct).ConfigureAwait(false);
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
        if (_disposed || !IsConnected) return;
        var json = JsonSerializer.Serialize(payload);
        var entered = false;
        try
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            if (_disposed || !IsConnected || _writer == null) return;
            await _writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Helper.Send", ex.Message);
            RaiseDisconnected();
        }
        finally
        {
            if (entered) _sendLock.Release();
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
        if (_disposed) return;
        if (Interlocked.Exchange(ref _disconnectedRaised, 1) == 1) return;
        DisposeTransport();
        Log.Warn("Helper", "连接已断开: " + Serial);
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeTransport()
    {
        try { _cts?.Cancel(); } catch { }
        var locked = false;
        try
        {
            locked = _sendLock.Wait(300);
            try { _reader?.Dispose(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _tcp?.Dispose(); } catch { }
            _reader = null;
            _writer = null;
            _tcp = null;
        }
        finally
        {
            if (locked) _sendLock.Release();
        }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }

    public static async Task RemoveForwardAsync(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return;
        await ForwardGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Adb.RunAsync(new[] { "-s", serial, "forward", "--remove", $"tcp:{Port}" }, 2500)
                .ConfigureAwait(false);
        }
        finally
        {
            ForwardGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeTransport();
        // 清理转发走 ForwardGate 串行队列，避免与新连接竞态。
        _ = RemoveForwardAsync(Serial);
    }
}
