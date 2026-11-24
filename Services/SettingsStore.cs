using System.IO;
using System.Text.Json;

namespace LengJiaoConnect.Services;

public sealed class AppSettings
{
    public int Quality { get; set; } = 1;
    public bool CastAudio { get; set; } = true;
    public bool CastControl { get; set; } = true;
    public bool CastUhidKeyboard { get; set; }
    public bool CastScreenOff { get; set; }

    public bool SyncClipboard { get; set; } = true;
    public bool SyncNotifications { get; set; } = true;
    public bool SyncApps { get; set; } = true;
    public bool SyncSms { get; set; } = true;

    public bool AutoReconnect { get; set; } = true;
    public bool AutoStart { get; set; }
    public bool ShowHiddenFiles { get; set; }
    public bool UseRoot { get; set; }

    /// <summary>上一次连接的设备，保存成 ip:port，无线调试的随机端口也能记住。</summary>
    public string LastEndpoint { get; set; } = "";

    /// <summary>上次弹过更新提示的版本号，避免每次启动都弹同一个版本。</summary>
    public string LastUpdatePrompt { get; set; } = "";
}

public static class SettingsStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LengJiaoConnect");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Settings.Load", ex);
            Current = new AppSettings();
        }
    }

    public static void Save()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
                File.Move(tmp, FilePath, true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings.Save", ex);
        }
    }
}
