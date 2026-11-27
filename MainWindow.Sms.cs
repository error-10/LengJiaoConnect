using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
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
    private bool _smsResetPending;
    private SmsItem? _selectedSms;

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

        var prev = _selectedSms;
        var baseList = _smsResetPending ? Enumerable.Empty<SmsItem>() : _sms;
        _smsResetPending = false;

        // 用完整字段元组去重，避免 32 位 GetHashCode 碰撞
        var merged = baseList.Concat(batch)
            .GroupBy(s => (s.Date, s.Address, s.Body))
            .Select(g => g.First())
            .OrderByDescending(s => s.Date)
            .ToList();

        Log.Info("短信刷新 " + merged.Count);
        _sms.Clear();
        foreach (var item in merged) _sms.Add(item);

        TxtSmsCount.Text = _sms.Count + " 条";

        // 刷新后尽量保持正在阅读的那条短信，不突然关掉详情页
        if (prev != null)
        {
            var match = _sms.FirstOrDefault(s => s.Date == prev.Date && s.Address == prev.Address && s.Body == prev.Body);
            if (match != null) ListSms.SelectedItem = match;
        }

        PanelSmsEmpty.IsVisible = ListSms.SelectedItem == null;
        PanelSmsDetail.IsVisible = ListSms.SelectedItem != null;
    }

    private async void BtnRefreshSms_Click(object? sender, RoutedEventArgs e)
    {
        if (_helper is not { IsConnected: true }) { Toast("高级互联没连上，先在设置里装助手", true); return; }
        await RequestSmsAsync(clearExisting: true);
        Toast("正在同步短信…");
    }

    private void ListSms_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ListSms.SelectedItem is not SmsItem item)
        {
            _selectedSms = null;
            PanelSmsDetail.IsVisible = false;
            PanelSmsEmpty.IsVisible = true;
            return;
        }

        _selectedSms = item;
        TxtSmsAddress.Text = item.Address;
        TxtSmsTime.Text = item.TimeText;
        TxtSmsBody.Text = item.Body;

        var code = item.VerificationCode;
        BtnCopySmsCode.IsVisible = code.Length > 0;
        if (code.Length > 0) TxtCopySmsCode.Text = "复制验证码 " + code;

        PanelSmsDetail.IsVisible = true;
        PanelSmsEmpty.IsVisible = false;
    }

    private async void BtnCopySmsCode_Click(object? sender, RoutedEventArgs e)
    {
        var code = _selectedSms?.VerificationCode;
        if (string.IsNullOrEmpty(code) || Clipboard is null) return;
        try
        {
            _lastClip = code;
            await Clipboard.SetTextAsync(code);
            Toast("已复制验证码 " + code, success: true);
        }
        catch (Exception ex) { Log.Warn("复制验证码失败：" + ex.Message); }
    }

    private async void BtnCopySmsBody_Click(object? sender, RoutedEventArgs e)
    {
        var body = _selectedSms?.Body;
        if (string.IsNullOrEmpty(body) || Clipboard is null) return;
        try
        {
            _lastClip = body;
            await Clipboard.SetTextAsync(body);
            Toast("已复制短信全文", success: true);
        }
        catch (Exception ex) { Log.Warn("复制短信失败：" + ex.Message); }
    }
}
