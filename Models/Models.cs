using Avalonia.Media;

namespace LengJiaoConnect.Models;

public enum DeviceState { Device, Offline, Unauthorized, Unknown }

public sealed class DeviceInfo
{
    public string Serial { get; init; } = "";
    public DeviceState State { get; init; }
    public string Model { get; init; } = "";

    /// <summary>是否当前正在控制的那台（设备浮层里显示勾选）。</summary>
    public bool IsCurrent { get; set; }

    public bool IsOnline => State == DeviceState.Device;
    public bool IsWireless => Serial.Contains(':') || Serial.Count(c => c == '.') >= 3;
    public string DisplayName => string.IsNullOrWhiteSpace(Model) ? Serial : Model;
    public string StateText => State switch
    {
        DeviceState.Device => "已连接",
        DeviceState.Offline => "离线",
        DeviceState.Unauthorized => "未授权",
        _ => "未知"
    };
}

public sealed class AppInfo
{
    public string PackageName { get; init; } = "";
    public string AppName { get; init; } = "";
    public string IconBase64 { get; init; } = "";
    public IImage? Icon { get; set; }
    public bool IsRecent { get; set; }
    public int LaunchCount { get; set; }
    public string DisplayName => string.IsNullOrWhiteSpace(AppName) ? PackageName : AppName;
}

public sealed class SmsItem
{
    public string Address { get; init; } = "";
    public string Body { get; init; } = "";
    public long Date { get; init; }
    public string TimeText => Date <= 0 ? "" : DateTimeOffset.FromUnixTimeMilliseconds(Date).LocalDateTime.ToString("MM-dd HH:mm");
    public string Snippet => Body.Length > 40 ? Body[..40] + "…" : Body;
}

public sealed class AndroidFile
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsLink { get; init; }
    public string Glyph => IsDirectory ? "📁" : "📄";
    public string SizeText => IsDirectory ? "文件夹" : FormatSize(Size);
    public string DateText => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public IBrush GlyphBrush => IsDirectory
        ? new SolidColorBrush(Color.Parse("#6FD3FF"))
        : new SolidColorBrush(Color.Parse("#94A3A0"));

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return kb.ToString("0.0") + " KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb.ToString("0.0") + " MB";
        return (mb / 1024.0).ToString("0.00") + " GB";
    }
}

public sealed class MediaInfo
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public bool Playing { get; init; }
    public string Package { get; init; } = "";
}

public sealed class InstallResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
}
