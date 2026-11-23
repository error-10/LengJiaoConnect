using System.IO;
using System.Text;

namespace LengJiaoConnect.Services;

/// <summary>极简日志：所有异常都必须留痕，不允许再出现空 catch 无记录。</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LengJiaoConnect", "logs");

    public static string LogDirectory => Dir;

    public static void Info(string scope, string message) => Write("INFO", scope, message);
    public static void Warn(string scope, string message) => Write("WARN", scope, message);

    public static void Info(string message) => Write("INFO", "app", message);
    public static void Warn(string message) => Write("WARN", "app", message);

    public static void Error(string scope, Exception ex) =>
        Write("ERROR", scope, ex.ToString());

    public static void Error(string scope, string message, Exception? ex = null) =>
        Write("ERROR", scope, ex == null ? message : message + Environment.NewLine + ex);

    public static void Fatal(string scope, Exception ex) => Write("FATAL", scope, ex.ToString());

    private static void Write(string level, string scope, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var file = Path.Combine(Dir, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file,
                    $"{DateTime.Now:HH:mm:ss.fff} [{level}] [{scope}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { /* 日志本身不能反过来把程序弄崩 */ }
    }
}
