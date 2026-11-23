using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LengJiaoConnect.Models;
using LengJiaoConnect.Services;

namespace LengJiaoConnect;

public partial class MainWindow
{
    private bool _devicePopoverOpen;

    private sealed record MdnsEndpoint(string Name, string Type, string Host, int Port);

    // ───────────────────────── 设备浮层 ─────────────────────────

    private void BtnDevicePill_Click(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        ToggleDevicePopover(!_devicePopoverOpen);
    }

    private void CloseDevicePopover(object? sender, PointerPressedEventArgs e) => ToggleDevicePopover(false);

    private void ToggleDevicePopover(bool open)
    {
        _devicePopoverOpen = open;
        DevicePopoverHost.IsVisible = open;
        if (!open)
        {
            PanelPopoverPair.IsVisible = false;
            TxtPopoverPairStatus.Text = "";
            TxtPopoverStatus.Text = "";
            return;
        }

        TxtPopoverStatus.Text = "";
        BtnPopoverConnect.IsEnabled = true;
        AnimateIn(DevicePopover, -6, 180);
        _ = LoadDevicePopoverAsync();
    }

    private async Task LoadDevicePopoverAsync()
    {
        try
        {
            var devices = await Adb.GetDevicesAsync();
            var list = devices.Where(d => d.State != DeviceState.Unknown).ToList();
            foreach (var d in list) d.IsCurrent = d.Serial == _serial;

            ListPopoverDevices.ItemsSource = list;
            ScrollPopoverDevices.IsVisible = list.Count > 0;
            TxtPopoverEmpty.IsVisible = list.Count == 0;
            TxtPopoverHint.Text = list.Count == 0
                ? "没有找到设备，插上 USB 或输入 IP 连接"
                : $"共 {list.Count} 台，点一下切换";

            if (list.Count > 0) return;

            // 没有已连接设备时，看一眼局域网里有没有开着无线调试的
            var endpoints = await DiscoverEndpointsAsync();
            var found = endpoints.FirstOrDefault(x =>
                x.Type.Contains("_adb-tls-connect", StringComparison.OrdinalIgnoreCase) ||
                x.Type.Contains("_adb._tcp", StringComparison.OrdinalIgnoreCase));
            if (found is null) return;

            TxtPopoverIp.Text = found.Host;
            TxtPopoverPort.Text = found.Port.ToString();
            TxtPopoverHint.Text = $"发现无线调试设备 {found.Host}:{found.Port}，点「连接」";
        }
        catch (Exception ex)
        {
            Log.Warn("读取设备失败：" + ex.Message);
        }
    }

    private async void BtnPopoverRefresh_Click(object? sender, RoutedEventArgs e)
    {
        BtnPopoverRefresh.IsEnabled = false;
        try
        {
            await LoadDevicePopoverAsync();
        }
        finally
        {
            BtnPopoverRefresh.IsEnabled = true;
        }
    }

    private async void PopoverDevice_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DeviceInfo device }) return;

        ToggleDevicePopover(false);
        if (device.Serial == _serial) return;

        if (!device.IsOnline)
        {
            Toast($"{device.DisplayName} 当前{device.StateText}", true);
            return;
        }

        await SetActiveDeviceAsync(device);
        Toast("已切换到 " + device.DisplayName);
    }

    private async void BtnPopoverConnect_Click(object? sender, RoutedEventArgs e)
    {
        var ip = TxtPopoverIp.Text?.Trim() ?? "";
        var port = TxtPopoverPort.Text?.Trim();
        if (string.IsNullOrWhiteSpace(port)) port = "5555";

        if (ip.Length == 0)
        {
            TxtPopoverStatus.Text = "先填手机 IP";
            TxtPopoverIp.Focus();
            return;
        }

        var endpoint = $"{ip}:{port}";
        BtnPopoverConnect.IsEnabled = false;
        TxtPopoverStatus.Text = "正在连接…";

        try
        {
            var r = await Adb.ConnectAsync(endpoint);
            var text = r.Best;
            TxtPopoverStatus.Text = text;

            if (!IsConnectedText(text)) return;

            _settings.LastEndpoint = endpoint;
            SettingsStore.Save();
            TxtLastEndpoint.Text = endpoint;

            await LoadDevicePopoverAsync();
            var target = (ListPopoverDevices.ItemsSource as IEnumerable<DeviceInfo>)?
                .FirstOrDefault(d => d.Serial == endpoint && d.IsOnline);
            if (target is null) return;

            await SetActiveDeviceAsync(target);
            ToggleDevicePopover(false);
            Toast("已连接 " + endpoint);
        }
        catch (Exception ex)
        {
            TxtPopoverStatus.Text = "连接失败：" + ex.Message;
            Log.Warn("无线连接失败：" + ex);
        }
        finally
        {
            BtnPopoverConnect.IsEnabled = true;
        }
    }

    // ───────────────────────── 首次配对（Android 11+） ─────────────────────────

    private async void BtnPopoverPairToggle_Click(object? sender, RoutedEventArgs e)
    {
        PanelPopoverPair.IsVisible = !PanelPopoverPair.IsVisible;
        if (!PanelPopoverPair.IsVisible) return;

        TxtPopoverPairStatus.Text = "";

        var ip = TxtPopoverIp.Text?.Trim() ?? "";
        if (ip.Length == 0) ip = await GetDeviceWlanIpAsync();
        if (ip.Length > 0 && string.IsNullOrWhiteSpace(TxtPopoverPairIp.Text)) TxtPopoverPairIp.Text = ip;

        // 手机的配对弹窗开着时，mDNS 会广播 _adb-tls-pairing，能把端口直接填上
        var endpoints = await DiscoverEndpointsAsync();
        var pairing = endpoints.FirstOrDefault(x => x.Type.Contains("_adb-tls-pairing", StringComparison.OrdinalIgnoreCase));
        if (pairing is null) return;

        TxtPopoverPairIp.Text = pairing.Host;
        TxtPopoverPairPort.Text = pairing.Port.ToString();
        TxtPopoverPairStatus.Text = "已自动填好配对地址，输入手机上的 6 位码即可。";
    }

    private async void BtnPopoverPairGo_Click(object? sender, RoutedEventArgs e)
    {
        var ip = (TxtPopoverPairIp.Text ?? "").Trim();
        var port = (TxtPopoverPairPort.Text ?? "").Trim();
        var code = (TxtPopoverPairCode.Text ?? "").Trim();

        if (ip.Length == 0 || port.Length == 0 || code.Length == 0)
        {
            TxtPopoverPairStatus.Text = "IP、配对端口、配对码都要填。";
            return;
        }

        BtnPopoverPairGo.IsEnabled = false;
        try
        {
            var ok = await PairAndConnectAsync(ip, port, code, s => TxtPopoverPairStatus.Text = s);
            if (!ok) return;

            await LoadDevicePopoverAsync();
            var target = (ListPopoverDevices.ItemsSource as IEnumerable<DeviceInfo>)?
                .FirstOrDefault(d => d.IsOnline && d.Serial.StartsWith(ip + ":", StringComparison.Ordinal));
            if (target is null) return;

            await SetActiveDeviceAsync(target);
            ToggleDevicePopover(false);
            Toast("已连接 " + target.Serial);
        }
        finally
        {
            BtnPopoverPairGo.IsEnabled = true;
        }
    }

    private async Task<bool> PairAndConnectAsync(string ip, string pairPort, string code, Action<string> status)
    {
        status("正在配对…");
        var pair = await Adb.PairAsync(ip, pairPort, code);
        var pairText = pair.Best;

        var paired = pairText.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase)
                     || pairText.Contains("already paired", StringComparison.OrdinalIgnoreCase);
        if (!paired)
        {
            status(FriendlyPairError(pairText));
            return false;
        }

        status("配对成功，正在连接…");

        var endpoints = await DiscoverEndpointsAsync();
        var connectPort = endpoints.FirstOrDefault(x => x.Host == ip &&
            (x.Type.Contains("_adb-tls-connect", StringComparison.OrdinalIgnoreCase) ||
             x.Type.Contains("_adb._tcp", StringComparison.OrdinalIgnoreCase)))?.Port ?? 5555;

        var endpoint = $"{ip}:{connectPort}";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var r = await Adb.ConnectAsync(endpoint);
            if (IsConnectedText(r.Best))
            {
                _settings.LastEndpoint = endpoint;
                SettingsStore.Save();
                TxtLastEndpoint.Text = endpoint;
                status("已连接 " + endpoint);
                return true;
            }

            if (attempt < 2) await Task.Delay(1200);
        }

        status($"已配对成功，但连接 {endpoint} 没成功。打开手机的「无线调试」页，把上面显示的 IP:端口填到「无线连接」里再点连接。");
        return false;
    }

    private static bool IsConnectedText(string text) =>
        text.Contains("connected to", StringComparison.OrdinalIgnoreCase) &&
        !text.Contains("cannot", StringComparison.OrdinalIgnoreCase) &&
        !text.Contains("failed", StringComparison.OrdinalIgnoreCase);

    private static string FriendlyPairError(string text)
    {
        if (text.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Incorrect", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed to authenticate", StringComparison.OrdinalIgnoreCase))
        {
            return "配对码不对。用手机上此刻显示的 6 位数字，配对窗口关掉就失效了。";
        }

        if (text.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed to connect", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("No route", StringComparison.OrdinalIgnoreCase))
        {
            return "连不上配对端口：确认手机和电脑在同一 WiFi，且「使用配对码配对设备」窗口还开着。";
        }

        if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("protocol fault", StringComparison.OrdinalIgnoreCase))
        {
            return "配对超时，重新打开一次配对窗口再试。";
        }

        return "配对失败：" + text;
    }

    private static async Task<List<MdnsEndpoint>> DiscoverEndpointsAsync()
    {
        var list = new List<MdnsEndpoint>();
        try
        {
            var r = await Adb.RunAsync(new[] { "mdns", "services" }, 6000);
            foreach (var raw in r.StdOut.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;

                var hostPort = parts[^1];
                var colon = hostPort.LastIndexOf(':');
                if (colon <= 0 || !int.TryParse(hostPort[(colon + 1)..], out var port)) continue;

                list.Add(new MdnsEndpoint(parts[0], parts[1], hostPort[..colon], port));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("mDNS 探测失败：" + ex.Message);
        }

        return list;
    }
}
