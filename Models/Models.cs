using System.Text.RegularExpressions;
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
    public bool HasLaunchCount => LaunchCount > 0;
    public string LaunchBadge => LaunchCount > 99 ? "99+" : (LaunchCount > 0 ? LaunchCount.ToString() : "");
    public string TooltipText => LaunchCount > 0
        ? $"{DisplayName}\n{PackageName}\n已启动 {LaunchCount} 次"
        : $"{DisplayName}\n{PackageName}";

    public void DisposeIcon()
    {
        if (Icon is IDisposable d)
        {
            try { d.Dispose(); } catch { }
        }
        Icon = null;
    }
}

public sealed class SmsItem
{
    private static readonly Regex CodeHintRegex = new(
        @"(?:验证码|校验码|动态码|确认码|激活码|登录码|安全码|口令|verification\s*code|passcode|code|otp|pin)(?:\s*(?:is|are|:|：|为|是|为您的|是您的)\s*|[^\dA-Za-z]{1,8})(?<code>\d{4,8})\b|\b(?<code2>\d{4,8})\s*(?:为您的|是您的|是|为|is\s+your)?[^\d]{0,12}(?:验证码|校验码|动态码|verification\s*code|otp)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Address { get; init; } = "";
    public string Body { get; init; } = "";
    public long Date { get; init; }
    public string TimeText => Date <= 0 ? "" : DateTimeOffset.FromUnixTimeMilliseconds(Date).LocalDateTime.ToString("MM-dd HH:mm");
    public string Snippet => Body.Replace('\r', ' ').Replace('\n', ' ') is var s && s.Length > 42 ? s[..42] + "…" : s;

    public string VerificationCode
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Body)) return "";
            var m = CodeHintRegex.Match(Body);
            if (!m.Success) return "";
            return m.Groups["code"].Success ? m.Groups["code"].Value : m.Groups["code2"].Value;
        }
    }

    public bool HasVerificationCode => VerificationCode.Length > 0;
}

public sealed class AndroidFile
{
    private static readonly IBrush DirBrush = new SolidColorBrush(Color.Parse("#6FD3FF"));
    private static readonly IBrush FileBrush = new SolidColorBrush(Color.Parse("#94A3A0"));
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#A8C7FF"));

    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsLink { get; init; }
    public bool IsRegularFile => !IsDirectory && !IsLink;
    public string Glyph => IsDirectory ? "📁" : (IsLink ? "🔗" : "📄");
    public string SizeText => IsDirectory ? "文件夹" : (IsLink ? "链接" : FormatSize(Size));
    public string DateText => Modified?.ToString("yyyy-MM-dd HH:mm") ?? "";
    public IBrush GlyphBrush => IsDirectory ? DirBrush : (IsLink ? LinkBrush : FileBrush);

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

public sealed class BreadcrumbSegment
{
    public string Label { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool ShowSeparator { get; init; }
}
