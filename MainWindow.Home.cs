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

    private async void MenuReboot_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string cmd }) return;
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }

        var (title, body) = cmd switch
        {
            "reboot" => ("重启系统", "手机会立刻重启，期间会与电脑断开。"),
            "reboot bootloader" => ("重启到 Bootloader", "进入 Fastboot 模式后需要手动退出，普通用户不建议使用。"),
            "reboot recovery" => ("重启到 Recovery", "进入恢复模式后无法正常使用手机，需要手动重启。"),
            _ => ("重启到 EDL 模式", "EDL 是高通底层刷机模式，非高通机型会直接失败；进错模式可能导致手机无法开机，风险自负。")
        };
        if (!await ConfirmAsync(title, body, "继续", "取消")) return;

        try
        {
            var r = await Adb.ShellAsync(_serial, cmd, 10000);
            Toast(r.Ok ? "重启命令已发送" : "重启失败：" + r.Best, !r.Ok);
            Log.Info($"重启命令 {cmd}：{r.Best}");
        }
        catch (Exception ex)
        {
            Toast("重启失败：" + ex.Message, true);
        }
    }

}
