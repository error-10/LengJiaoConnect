using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LengJiaoConnect.Models;
using LengJiaoConnect.Services;

namespace LengJiaoConnect;

public partial class MainWindow
{
    private readonly List<SmsItem> _tempSms = new();
    private readonly object _tempSmsGate = new();
    private DispatcherTimer? _smsFlushTimer;

    private void OnSmsReceived(string address, long date, string body)
    {
        lock (_tempSmsGate)
        {
            _tempSms.Add(new SmsItem { Address = address, Date = date, Body = body });
            if (_tempSms.Count > 500) _tempSms.RemoveRange(0, _tempSms.Count - 500);
        }

        Dispatcher.UIThread.Post(() =>
        {
            _smsFlushTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _smsFlushTimer.Stop();
            _smsFlushTimer.Tick -= FlushSmsTick;
            _smsFlushTimer.Tick += FlushSmsTick;
            _smsFlushTimer.Start();
        });
    }

    private void FlushSmsTick(object? sender, EventArgs e)
    {
        _smsFlushTimer?.Stop();

        List<SmsItem> batch;
        lock (_tempSmsGate)
        {
            if (_tempSms.Count == 0) return;
            batch = _tempSms.ToList();
            _tempSms.Clear();
        }

        var merged = _sms.Concat(batch)
            .GroupBy(s => s.Date + "|" + s.Address + "|" + s.Body.GetHashCode())
            .Select(g => g.First())
            .OrderByDescending(s => s.Date)
            .ToList();

        Log.Info("短信刷新 " + merged.Count);
        _sms.Clear();
        foreach (var item in merged) _sms.Add(item);

        TxtSmsCount.Text = _sms.Count + " 条";
        PanelSmsEmpty.IsVisible = ListSms.SelectedItem == null;
        PanelSmsDetail.IsVisible = ListSms.SelectedItem != null;
    }

    private async void BtnRefreshSms_Click(object? sender, RoutedEventArgs e)
    {
        if (_helper is not { IsConnected: true }) { Toast("高级互联没连上，先在设置里装助手", true); return; }
        lock (_tempSmsGate) _tempSms.Clear();
        await _helper.RequestSmsAsync();
        Toast("正在同步短信…");
    }

    private void ListSms_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ListSms.SelectedItem is not SmsItem item)
        {
            PanelSmsDetail.IsVisible = false;
            PanelSmsEmpty.IsVisible = true;
            return;
        }

        TxtSmsAddress.Text = item.Address;
        TxtSmsTime.Text = item.TimeText;
        TxtSmsBody.Text = item.Body;
        PanelSmsDetail.IsVisible = true;
        PanelSmsEmpty.IsVisible = false;
    }
}
