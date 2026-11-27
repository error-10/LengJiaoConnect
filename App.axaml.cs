using System;
using System.Diagnostics;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LengJiaoConnect.Services;

namespace LengJiaoConnect;

public partial class App : Application
{
    private static Mutex? _mutex;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        _mutex = new Mutex(true, @"Local\LengJiaoConnect.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            ActivateExisting();
            Environment.Exit(0);
            return;
        }

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Fatal("UI 未处理异常", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal("域未处理异常", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "未知域异常"));

        SettingsStore.Load();
        ApplyBrandTheme();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }

    // 把 Suki 的默认蓝换成品牌冰青，视觉上才和手机端一致
    private static void ApplyBrandTheme()
    {
        try
        {
            var theme = SukiUI.SukiTheme.GetInstance();
            var brand = new SukiUI.Models.SukiColorTheme("棱角青", Avalonia.Media.Color.Parse("#6FD3FF"), Avalonia.Media.Color.Parse("#A8C7FF"));
            theme.AddColorTheme(brand);
            theme.ChangeColorTheme(brand);
        }
        catch (Exception ex)
        {
            Log.Warn("品牌主题应用失败：" + ex.Message);
        }
    }

    private static void ActivateExisting()
    {
        try
        {
            var hwnd = Native.FindWindowW(null, "棱角互联");
            if (hwnd != IntPtr.Zero)
            {
                Native.ShowWindow(hwnd, 9);
                Native.SetForegroundWindow(hwnd);
                return;
            }
            foreach (var p in Process.GetProcessesByName("LengJiaoConnect"))
            {
                if (p.Id == Environment.ProcessId || p.MainWindowHandle == IntPtr.Zero) continue;
                Native.ShowWindow(p.MainWindowHandle, 9);
                Native.SetForegroundWindow(p.MainWindowHandle);
                break;
            }
        }
        catch (Exception ex) { Log.Warn("激活已有窗口失败：" + ex.Message); }
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
