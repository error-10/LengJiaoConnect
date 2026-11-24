using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace LengJiaoConnect;

/// <summary>
/// 右下角浮出的通知条。主窗口收进托盘时也能看见手机推过来的通知。
/// </summary>
public sealed class ToastWindow : Window
{
    private static readonly List<ToastWindow> Open = new();
    private readonly DispatcherTimer _timer;

    public ToastWindow(string app, string title, string body)
    {
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F20D151F")),
            BorderBrush = new SolidColorBrush(Color.Parse("#336FD3FF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12),
            BoxShadow = BoxShadows.Parse("0 12 30 0 #66000000"),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock
                    {
                        Text = app,
                        FontSize = 11,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.Parse("#6FD3FF"))
                    },
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 13.5,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.Parse("#EAF2FA")),
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = body,
                        FontSize = 12.5,
                        Foreground = new SolidColorBrush(Color.Parse("#93A6BC")),
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 3,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            }
        };

        Opened += (_, _) => Place();
        Closed += (_, _) => Open.Remove(this);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _timer.Tick += (_, _) => Close();
    }

    private void Place()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var area = screen.WorkingArea;
        var widthPx = (int)(Bounds.Width * scale);
        var heightPx = (int)(Bounds.Height * scale);
        var gap = (int)(10 * scale);

        Open.Remove(this);
        var index = Open.Count;
        Open.Insert(index, this);

        var x = area.Right - widthPx - (int)(18 * scale);
        var y = area.Bottom - heightPx - (int)(18 * scale) - index * (heightPx + gap);
        Position = new PixelPoint(x, Math.Max(area.Y, y));
        _timer.Start();
    }

    public static void Show(string app, string title, string body)
    {
        // 同屏最多三条，旧的先收掉，避免刷屏
        while (Open.Count >= 3)
        {
            Open[0].Close();
        }
        new ToastWindow(app, title, body).Show();
    }
}
