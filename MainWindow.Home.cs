using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LengJiaoConnect.Services;

namespace LengJiaoConnect;

public partial class MainWindow
{
    private void Quality_Checked(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady || sender is not RadioButton rb) return;
        if (int.TryParse(rb.Tag as string, out var q))
        {
            _settings.Quality = q;
            SettingsStore.Save();
        }
    }

    private void CastOption_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.CastAudio = ChkCastAudio.IsChecked == true;
        _settings.CastControl = ChkCastControl.IsChecked == true;
        _settings.CastUhidKeyboard = ChkCastUhid.IsChecked == true;
        _settings.CastScreenOff = ChkCastScreenOff.IsChecked == true;
        SettingsStore.Save();
    }

    private void WizMode_Checked(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        var wifi = WizWifi.IsChecked == true;
        PanelWizUsb.IsVisible = !wifi;
        PanelWizWifi.IsVisible = wifi;
    }

    private async void BtnPair_Click(object? sender, RoutedEventArgs e)
    {
        var ip = TxtPairIp.Text?.Trim() ?? "";
        var port = TxtPairPort.Text?.Trim() ?? "";
        var code = TxtPairCode.Text?.Trim() ?? "";
        if (ip.Length == 0 || port.Length == 0 || code.Length == 0)
        {
            TxtWizardStatus.Text = "IP、端口、配对码都要填。";
            return;
        }

        BtnPair.IsEnabled = false;
        TxtWizardStatus.Text = "正在配对…";
        try
        {
            var r = await Adb.PairAsync(ip, port, code);
            if (r.Best.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
            {
                TxtWizardStatus.Text = "配对成功，下一步填端口连接（通常 5555）。";
                TxtWifiIp.Text = ip;
                if (string.IsNullOrWhiteSpace(TxtWifiPort.Text)) TxtWifiPort.Text = "5555";
            }
            else
            {
                TxtWizardStatus.Text = "配对失败：" + r.Best;
            }
        }
        finally
        {
            BtnPair.IsEnabled = true;
        }
    }

    private async void BtnConnectWifi_Click(object? sender, RoutedEventArgs e)
    {
        var ip = TxtWifiIp.Text?.Trim() ?? "";
        var port = TxtWifiPort.Text?.Trim() ?? "5555";
        if (ip.Length == 0)
        {
            TxtWizardStatus.Text = "先填手机 IP。";
            return;
        }

        var endpoint = ip + ":" + port;
        BtnConnectWifi.IsEnabled = false;
        TxtWizardStatus.Text = "正在连接 " + endpoint + "…";
        try
        {
            var r = await Adb.ConnectAsync(endpoint);
            if (r.Best.Contains("connected", StringComparison.OrdinalIgnoreCase))
            {
                _settings.LastEndpoint = endpoint;
                SettingsStore.Save();
                TxtLastEndpoint.Text = "上次设备：" + endpoint;
                TxtWizardStatus.Text = "已连接，正在刷新设备…";
                await PollDevicesAsync();
            }
            else
            {
                TxtWizardStatus.Text = "连接失败：" + r.Best;
            }
        }
        finally
        {
            BtnConnectWifi.IsEnabled = true;
        }
    }
}
