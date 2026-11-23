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

}
