using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LengJiaoConnect.Models;
using LengJiaoConnect.Services;

namespace LengJiaoConnect;

public partial class MainWindow
{
    private string _currentPath = "/sdcard/";
    private bool _filesLoading;
    private string CurrentRemotePath => _currentPath;

    private static readonly Regex LsLine = new(
        @"^(?<perm>[-dlcbps][rwxStTs-]{9}[.+@]?)\s+(?<links>\d+)\s+(?<owner>\S+)\s+(?<group>\S+)\s+(?<size>\d+)\s+(?<date>\d{4}-\d{2}-\d{2}|\S{3}\s+\d{1,2})\s+(?<time>\d{2}:\d{2})\s+(?<name>.+)$",
        RegexOptions.Compiled);

    private static string JoinRemote(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir)) dir = "/sdcard/";
        return dir.EndsWith("/") ? dir + name : dir + "/" + name;
    }

    private string WrapShell(string cmd) => _settings.UseRoot ? "su -c " + Adb.Quote(cmd) : cmd;

    private async Task LoadDirectoryAsync(string path)
    {
        if (string.IsNullOrEmpty(_serial) || _filesLoading) return;

        _filesLoading = true;
        TxtFileStatus.Text = "正在读取…";
        try
        {
            var flags = _settings.ShowHiddenFiles ? "-la" : "-l";
            var cmd = $"LANG=C.UTF-8 ls {flags} {Adb.Quote(path)}";
            var r = await Adb.ShellAsync(_serial, WrapShell(cmd));

            if (!r.Ok || string.IsNullOrWhiteSpace(r.StdOut))
            {
                TxtFileStatus.Text = string.IsNullOrWhiteSpace(r.Best) ? "空目录或没有权限" : r.Best.Trim();
                _files.Clear();
                PanelFilesEmpty.IsVisible = true;
                return;
            }

            var items = new List<AndroidFile>();
            foreach (var line in r.StdOut.Split('\n'))
            {
                var text = line.TrimEnd('\r');
                if (text.Length == 0 || text.StartsWith("total ")) continue;

                var m = LsLine.Match(text);
                if (!m.Success) continue;

                var name = m.Groups["name"].Value;
                var linkIndex = name.IndexOf(" -> ", StringComparison.Ordinal);
                var isLink = linkIndex >= 0;
                if (isLink) name = name[..linkIndex];
                if (name is "." or "..") continue;

                var perm = m.Groups["perm"].Value;
                var isDir = perm[0] == 'd';
                long.TryParse(m.Groups["size"].Value, out var size);
                var dateText = m.Groups["date"].Value + " " + m.Groups["time"].Value;
                DateTime.TryParse(dateText, out var modified);

                items.Add(new AndroidFile
                {
                    Name = name,
                    Path = JoinRemote(path, name),
                    Size = size,
                    Modified = modified == default ? null : modified,
                    IsDirectory = isDir,
                    IsLink = isLink
                });
            }

            items = items.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            _files.Clear();
            foreach (var f in items) _files.Add(f);

            _currentPath = path;
            TxtPath.Text = path;
            TxtDropHint.Text = "会传到 " + path;
            PanelFilesEmpty.IsVisible = items.Count == 0;
            TxtFileStatus.Text = items.Count + " 项";
        }
        catch (Exception ex)
        {
            TxtFileStatus.Text = "读取失败：" + ex.Message;
            Log.Warn("读取目录失败：" + ex.Message);
        }
        finally
        {
            _filesLoading = false;
        }
    }

    private void BtnRefreshFiles_Click(object? sender, RoutedEventArgs e) => _ = LoadDirectoryAsync(_currentPath);

    private void BtnUp_Click(object? sender, RoutedEventArgs e)
    {
        var path = _currentPath.TrimEnd('/');
        if (path.Length == 0 || path == "/sdcard") return;
        var idx = path.LastIndexOf('/');
        var parent = idx <= 0 ? "/" : path[..idx];
        _ = LoadDirectoryAsync(parent);
    }

    private void BtnPathGo_Click(object? sender, RoutedEventArgs e) => _ = LoadDirectoryAsync(NormalizePath(TxtPath.Text));

    private void TxtPath_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = LoadDirectoryAsync(NormalizePath(TxtPath.Text));
    }

    private static string NormalizePath(string? raw)
    {
        var p = (raw ?? "").Trim().Replace('\\', '/');
        if (p.Length == 0) return "/sdcard/";
        if (!p.StartsWith("/")) p = "/sdcard/" + p;
        return p.EndsWith("/") ? p : p + "/";
    }

    private async void BtnUpload_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要上传的文件",
            AllowMultiple = true
        });
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().ToList();
        if (paths.Count > 0) await UploadFilesAsync(paths);
    }

    private async Task UploadFilesAsync(IReadOnlyList<string> localPaths)
    {
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }

        GoPage("files");
        Busy($"正在上传 0/{localPaths.Count}…", true);
        var failed = 0;
        try
        {
            for (var i = 0; i < localPaths.Count; i++)
            {
                var local = localPaths[i];
                var remote = JoinRemote(_currentPath, Path.GetFileName(local));
                TxtBusy.Text = $"正在上传 {i + 1}/{localPaths.Count}：{Path.GetFileName(local)}";
                var r = await Adb.PushAsync(_serial, local, remote);
                if (!r.Ok)
                {
                    failed++;
                    Log.Warn("上传失败：" + local + " -> " + r.Best);
                }
            }
            Toast(failed == 0 ? $"上传完成，共 {localPaths.Count} 个" : $"上传结束，{failed} 个失败", failed > 0);
            await LoadDirectoryAsync(_currentPath);
        }
        finally { Busy("", false); }
    }

    private async void BtnDownload_Click(object? sender, RoutedEventArgs e) => await DownloadSelectedAsync();

    private async Task DownloadSelectedAsync()
    {
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }
        var selected = ListFiles.SelectedItems?.Cast<AndroidFile>().Where(f => !f.IsDirectory).ToList() ?? new();
        if (selected.Count == 0) { Toast("先选中要下载的文件"); return; }

        if (selected.Count == 1)
        {
            var file = selected[0];
            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存到",
                SuggestedFileName = file.Name
            });
            if (target == null) return;
            var local = target.TryGetLocalPath() ?? target.Path.LocalPath;
            await PullOneAsync(file, local);
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择保存目录", AllowMultiple = false });
        var dir = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(dir)) return;

        Busy("正在下载…", true);
        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                TxtBusy.Text = $"正在下载 {i + 1}/{selected.Count}：{selected[i].Name}";
                await PullOneAsync(selected[i], Path.Combine(dir, selected[i].Name), quiet: true);
            }
            Toast("下载完成，共 " + selected.Count + " 个");
        }
        finally { Busy("", false); }
    }

    private async Task PullOneAsync(AndroidFile file, string localFullPath, bool quiet = false)
    {
        if (!quiet) Busy("正在下载…", true);
        try
        {
            var r = await Adb.PullAsync(_serial, file.Path, localFullPath);
            if (r.Ok) { if (!quiet) Toast("已保存到 " + localFullPath); }
            else Toast("下载失败：" + r.Best, true);
        }
        finally { if (!quiet) Busy("", false); }
    }

    private async void BtnNewFolder_Click(object? sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("新建文件夹", "名称", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0) { Toast("名称里不能有斜杠", true); return; }

        var r = await Adb.ShellAsync(_serial, WrapShell($"mkdir -p {Adb.Quote(JoinRemote(_currentPath, name))}"));
        if (!r.Ok) Toast("创建失败：" + r.Best, true);
        await LoadDirectoryAsync(_currentPath);
    }

    private async void BtnRename_Click(object? sender, RoutedEventArgs e)
    {
        var selected = ListFiles.SelectedItems?.Cast<AndroidFile>().ToList() ?? new();
        if (selected.Count != 1) { Toast("一次只能重命名一个"); return; }

        var file = selected[0];
        var name = await PromptAsync("重命名", "新名称", file.Name);
        if (string.IsNullOrWhiteSpace(name) || name == file.Name) return;
        if (name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0) { Toast("名称里不能有斜杠", true); return; }

        var r = await Adb.ShellAsync(_serial, WrapShell($"mv {Adb.Quote(file.Path)} {Adb.Quote(JoinRemote(_currentPath, name))}"));
        if (!r.Ok) Toast("重命名失败：" + r.Best, true);
        await LoadDirectoryAsync(_currentPath);
    }

    private async void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        var selected = ListFiles.SelectedItems?.Cast<AndroidFile>().ToList() ?? new();
        if (selected.Count == 0) { Toast("先选中要删除的项"); return; }
        if (!await ConfirmAsync("删除", $"确定删除选中的 {selected.Count} 项？删了就找不回来了。", "删除", "取消")) return;

        var failed = 0;
        foreach (var f in selected)
        {
            var r = await Adb.ShellAsync(_serial, WrapShell($"rm -rf {Adb.Quote(f.Path)}"));
            if (!r.Ok) failed++;
        }
        Toast(failed == 0 ? "已删除" : failed + " 项删除失败", failed > 0);
        await LoadDirectoryAsync(_currentPath);
    }

    private async void ListFiles_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ListFiles.SelectedItem is not AndroidFile file) return;
        if (file.IsDirectory) await LoadDirectoryAsync(file.Path);
        else await DownloadSelectedAsync();
    }

    private void FileOption_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.ShowHiddenFiles = ChkShowHidden.IsChecked == true;
        _settings.UseRoot = ChkUseRoot.IsChecked == true;
        SettingsStore.Save();
        _ = LoadDirectoryAsync(_currentPath);
    }
}
