using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace LengJiaoConnect;

/// <summary>
/// 右下角浮出的通知条。主窗口收进托盘时也能看见手机推过来的通知。
/// 支持按真实高度动态重排、点击即收起、严格上限 3 条防刷屏。
/// </summary>
public sealed class ToastWindow : Window
{
    private static readonly List<ToastWindow> Open = new();
    private readonly DispatcherTimer _timer;
    private readonly Border _card;

    public ToastWindow(string app, string title, string body)
    {
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Width = 368;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = new Cursor(StandardCursorType.Hand);

        var accent = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 2, 12, 2),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#6FD3FF"), 0),
                    new GradientStop(Color.Parse("#A8C7FF"), 1)
                }
            }
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1C6FD3FF")),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(app) ? "通知" : app,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#8EE0FF"))
            }
        });
        var timeLabel = new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm"),
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.Parse("#64758A")),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(timeLabel, 1);
        header.Children.Add(timeLabel);

        var bodyStack = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                header,
                new TextBlock
                {
                    Text = title,
                    FontSize = 13.5,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#EAF2FA")),
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(body))
        {
            bodyStack.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = 12.2,
                Foreground = new SolidColorBrush(Color.Parse("#93A6BC")),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        layout.Children.Add(accent);
        Grid.SetColumn(bodyStack, 1);
        layout.Children.Add(bodyStack);

        _card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F50C1520")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3D6FD3FF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(13, 11, 14, 12),
            BoxShadow = BoxShadows.Parse("0 14 34 0 #77000000"),
            Opacity = 0,
            RenderTransform = TransformOperations.Parse("translateX(18px)"),
            Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut() },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(240), Easing = new CubicEaseOut() }
            },
            Child = layout
        };

        Content = _card;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _timer.Tick += (_, _) => Dismiss();

        PointerPressed += (_, _) => Dismiss();
        Opened += (_, _) =>
        {
            Reflow();
            Dispatcher.UIThread.Post(() =>
            {
                Reflow();
                _card.Opacity = 1;
                _card.RenderTransform = TransformOperations.Parse("translateX(0px)");
            }, DispatcherPriority.Render);
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            Open.Remove(this);
            Reflow();
        };
    }

    private void Dismiss()
    {
        _timer.Stop();
        Close();
    }

    private static void Reflow()
    {
        if (Open.Count == 0) return;
        var anchor = Open[0];
        var screen = anchor.Screens.ScreenFromWindow(anchor) ?? anchor.Screens.Primary;
        if (screen is null) return;

        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var area = screen.WorkingArea;
        var gap = (int)(10 * scale);
        var marginRight = (int)(18 * scale);
        var cursorBottom = area.Bottom - (int)(18 * scale);

        for (var i = 0; i < Open.Count; i++)
        {
            var win = Open[i];
            var widthDip = win.Bounds.Width > 0 ? win.Bounds.Width : win.Width;
            var heightDip = win.Bounds.Height > 0 ? win.Bounds.Height : 94;
            var widthPx = (int)(widthDip * scale);
            var heightPx = (int)(heightDip * scale);

            var x = area.Right - widthPx - marginRight;
            var y = Math.Max(area.Y, cursorBottom - heightPx);
            win.Position = new PixelPoint(x, y);
            cursorBottom = y - gap;
        }
    }

    public static void Show(string app, string title, string body)
    {
        // 立刻入队再 Show，防止同一帧连发多条绕过 3 条上限
        while (Open.Count >= 3)
        {
            var oldest = Open[0];
            Open.RemoveAt(0);
            oldest.Close();
        }
        var toast = new ToastWindow(app, title, body);
        Open.Add(toast);
        toast.Show();
    }
}
