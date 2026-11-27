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

    // 兼容 Android Toybox (YYYY-MM-DD HH:MM) 与 BusyBox/GNU ls (Mon DD HH:MM 或 Mon DD YYYY)
    private static readonly Regex LsLine = new(
        @"^(?<perm>[-dlcbps][rwxStTs-]{9}[.+@]?)\s+(?<links>\d+)\s+(?<owner>\S+)\s+(?<group>\S+)\s+(?<size>\d+)\s+(?<date>\d{4}-\d{2}-\d{2}|\S{3}\s+\d{1,2})\s+(?<time>\d{2}:\d{2}|\d{4})\s+(?<name>.+)$",
        RegexOptions.Compiled);

    private static string EnsureDirSlash(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/sdcard/";
        return path.EndsWith("/") ? path : path + "/";
    }

    private static string JoinRemote(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir)) dir = "/sdcard/";
        return dir.EndsWith("/") ? dir + name : dir + "/" + name;
    }

    private string WrapShell(string cmd) => _settings.UseRoot ? "su -c " + Adb.Quote(cmd) : cmd;

    private static bool IsSuMissing(AdbResult r)
    {
        var text = r.All;
        return text.Contains("su: inaccessible", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("su: not found", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("su: permission denied", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not allowed to su", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AdbResult> RunFileShellAsync(string serial, string cmd, int timeoutMs = 20000)
    {
        var r = await Adb.ShellAsync(serial, WrapShell(cmd), timeoutMs);
        if (_settings.UseRoot && !r.Ok && IsSuMissing(r))
        {
            Log.Warn("Files.Root", "当前设备无 su 权限，自动回退普通模式：" + r.Best);
            _settings.UseRoot = false;
            SettingsStore.Save();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _uiReady = false;
                ChkUseRoot.IsChecked = false;
                _uiReady = true;
                RebuildBreadcrumbs(_currentPath);
                Toast("当前设备不支持 Root（无 su），已自动切回普通模式");
            });
            return await Adb.ShellAsync(serial, cmd, timeoutMs);
        }
        return r;
    }

    private void RebuildBreadcrumbs(string path)
    {
        _breadcrumbs.Clear();
        var norm = EnsureDirSlash(path);
        var parts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (_settings.UseRoot || parts.Length == 0)
        {
            _breadcrumbs.Add(new BreadcrumbSegment { Label = "/", FullPath = "/", ShowSeparator = false });
        }

        var acc = "";
        for (var i = 0; i < parts.Length; i++)
        {
            acc += "/" + parts[i];
            _breadcrumbs.Add(new BreadcrumbSegment
            {
                Label = parts[i],
                FullPath = acc + "/",
                ShowSeparator = _breadcrumbs.Count > 0
            });
        }

        BtnJumpRoot.IsVisible = _settings.UseRoot;
    }

    private async Task LoadDirectoryAsync(string path)
    {
        if (string.IsNullOrEmpty(_serial) || _filesLoading) return;

        // 目录路径始终以 '/' 结尾，确保 Android 上 /sdcard 等软链接目录会被 ls 解引用而非列出链接本身
        var dirPath = EnsureDirSlash(path);
        var serial = _serial;
        var epoch = _deviceEpoch;

        _filesLoading = true;
        TxtFileStatus.Text = "正在读取…";
        try
        {
            var flags = _settings.ShowHiddenFiles ? "-la" : "-l";
            var cmd = $"LANG=C.UTF-8 ls {flags} {Adb.Quote(dirPath)}";
            var r = await RunFileShellAsync(serial, cmd);

            if (_deviceEpoch != epoch || !string.Equals(serial, _serial, StringComparison.Ordinal))
                return;

            if (!r.Ok && string.IsNullOrWhiteSpace(r.StdOut))
            {
                TxtFileStatus.Text = string.IsNullOrWhiteSpace(r.Best) ? "没有权限或目录不存在" : r.Best.Trim();
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

                var perm = m.Groups["perm"].Value;
                var isLink = perm[0] == 'l';
                var name = m.Groups["name"].Value;
                if (isLink)
                {
                    var linkIndex = name.IndexOf(" -> ", StringComparison.Ordinal);
                    if (linkIndex >= 0) name = name[..linkIndex];
                }
                if (name is "." or "..") continue;

                var isDir = perm[0] == 'd';
                long.TryParse(m.Groups["size"].Value, out var size);
                var dateText = m.Groups["date"].Value + " " + m.Groups["time"].Value;
                DateTime.TryParse(dateText, out var modified);

                items.Add(new AndroidFile
                {
                    Name = name,
                    Path = JoinRemote(dirPath, name),
                    Size = size,
                    Modified = modified == default ? null : modified,
                    IsDirectory = isDir,
                    IsLink = isLink
                });
            }

            items = items
                .OrderByDescending(f => f.IsDirectory)
                .ThenByDescending(f => f.IsLink)
                .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _files.Clear();
            foreach (var f in items) _files.Add(f);

            _currentPath = dirPath;
            TxtPath.Text = dirPath;
            TxtDropHint.Text = "会传到 " + dirPath;
            RebuildBreadcrumbs(dirPath);
            PanelFilesEmpty.IsVisible = items.Count == 0;
            TxtFileStatus.Text = items.Count + " 项";
            UpdateFileSelectionText();
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

    private void UpdateFileSelectionText()
    {
        var count = ListFiles.SelectedItems?.Count ?? 0;
        TxtFileSelection.IsVisible = count > 0;
        if (count > 0) TxtFileSelection.Text = $"已选 {count} 项";
    }

    private void ListFiles_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateFileSelectionText();

    private void Breadcrumb_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BreadcrumbSegment seg })
            _ = LoadDirectoryAsync(seg.FullPath);
    }

    private void QuickJump_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string target })
            _ = LoadDirectoryAsync(target);
    }

    private void BtnRefreshFiles_Click(object? sender, RoutedEventArgs e) => _ = LoadDirectoryAsync(_currentPath);

    private void BtnUp_Click(object? sender, RoutedEventArgs e)
    {
        var path = _currentPath.TrimEnd('/');
        if (path.Length == 0) return;
        if (!_settings.UseRoot && path == "/sdcard") return;

        var idx = path.LastIndexOf('/');
        var parent = idx <= 0 ? "/" : path[..idx] + "/";
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

    private async Task<AdbResult> PushSmartAsync(string local, string remote)
    {
        var r = await Adb.PushAsync(_serial, local, remote);
        if (r.Ok || !_settings.UseRoot) return r;

        // Root 模式下，非 /sdcard 路径 adb push 会被拒；借 /data/local/tmp 中转并用 su 移入目标路径
        var tmp = "/data/local/tmp/ljc_up_" + Guid.NewGuid().ToString("N");
        try
        {
            var pushed = await Adb.PushAsync(_serial, local, tmp);
            if (!pushed.Ok) return pushed;
            return await Adb.ShellAsync(_serial, WrapShell($"cp -r {Adb.Quote(tmp)} {Adb.Quote(remote)}"));
        }
        finally
        {
            _ = Adb.ShellAsync(_serial, WrapShell($"rm -rf {Adb.Quote(tmp)}"), 8000);
        }
    }

    private async Task<AdbResult> PullSmartAsync(string remote, string localFullPath)
    {
        var r = await Adb.PullAsync(_serial, remote, localFullPath);
        if (r.Ok || !_settings.UseRoot) return r;

        var tmp = "/data/local/tmp/ljc_dn_" + Guid.NewGuid().ToString("N");
        try
        {
            var copied = await Adb.ShellAsync(_serial, WrapShell($"cp -f {Adb.Quote(remote)} {Adb.Quote(tmp)} && chmod 644 {Adb.Quote(tmp)}"));
            if (!copied.Ok) return copied;
            return await Adb.PullAsync(_serial, tmp, localFullPath);
        }
        finally
        {
            _ = Adb.ShellAsync(_serial, WrapShell($"rm -f {Adb.Quote(tmp)}"), 8000);
        }
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
                var local = localPaths[i].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var name = Path.GetFileName(local);
                if (string.IsNullOrWhiteSpace(name)) { failed++; continue; }

                var remote = JoinRemote(_currentPath, name);
                TxtBusy.Text = $"正在上传 {i + 1}/{localPaths.Count}：{name}";
                var r = await PushSmartAsync(local, remote);
                if (!r.Ok)
                {
                    failed++;
                    Log.Warn("上传失败：" + local + " -> " + r.Best);
                }
            }
            Toast(failed == 0 ? $"上传完成，共 {localPaths.Count} 个" : $"上传结束，{failed} 个失败", failed > 0, failed == 0);
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
        var failed = 0;
        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                TxtBusy.Text = $"正在下载 {i + 1}/{selected.Count}：{selected[i].Name}";
                if (!await PullOneAsync(selected[i], Path.Combine(dir, selected[i].Name), quiet: true))
                    failed++;
            }
            Toast(failed == 0
                ? $"下载完成，共 {selected.Count} 个"
                : $"下载结束，成功 {selected.Count - failed} 个，失败 {failed} 个", failed > 0, failed == 0);
        }
        finally { Busy("", false); }
    }

    private async Task<bool> PullOneAsync(AndroidFile file, string localFullPath, bool quiet = false)
    {
        if (!quiet) Busy("正在下载…", true);
        try
        {
            var r = await PullSmartAsync(file.Path, localFullPath);
            if (r.Ok)
            {
                if (!quiet) Toast("已保存到 " + localFullPath, success: true);
                return true;
            }
            if (!quiet) Toast("下载失败：" + r.Best, true);
            else Log.Warn("下载失败：" + file.Path + " -> " + r.Best);
            return false;
        }
        finally { if (!quiet) Busy("", false); }
    }

    private async void BtnNewFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }
        var name = await PromptAsync("新建文件夹", "名称", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0) { Toast("名称里不能有斜杠", true); return; }

        var r = await RunFileShellAsync(_serial, $"mkdir -p {Adb.Quote(JoinRemote(_currentPath, name))}");
        if (!r.Ok) Toast("创建失败：" + r.Best, true);
        else Toast("已创建文件夹 " + name, success: true);
        await LoadDirectoryAsync(_currentPath);
    }

    private async void BtnRename_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }
        var selected = ListFiles.SelectedItems?.Cast<AndroidFile>().ToList() ?? new();
        if (selected.Count != 1) { Toast("一次只能重命名一个"); return; }

        var file = selected[0];
        var name = await PromptAsync("重命名", "新名称", file.Name);
        if (string.IsNullOrWhiteSpace(name) || name == file.Name) return;
        if (name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0) { Toast("名称里不能有斜杠", true); return; }

        var r = await RunFileShellAsync(_serial, $"mv {Adb.Quote(file.Path)} {Adb.Quote(JoinRemote(_currentPath, name))}");
        if (!r.Ok) Toast("重命名失败：" + r.Best, true);
        else Toast("已重命名为 " + name, success: true);
        await LoadDirectoryAsync(_currentPath);
    }

    private async void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_serial)) { Toast("先连接设备", true); return; }
        var selected = ListFiles.SelectedItems?.Cast<AndroidFile>().ToList() ?? new();
        if (selected.Count == 0) { Toast("先选中要删除的项"); return; }
        if (!await ConfirmAsync("删除", $"确定删除选中的 {selected.Count} 项？删了就找不回来了。", "删除", "取消")) return;

        var failed = 0;
        foreach (var f in selected)
        {
            var r = await RunFileShellAsync(_serial, $"rm -rf {Adb.Quote(f.Path)}");
            if (!r.Ok) failed++;
        }
        Toast(failed == 0 ? "已删除" : failed + " 项删除失败", failed > 0, failed == 0);
        await LoadDirectoryAsync(_currentPath);
    }

    private async void ListFiles_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ListFiles.SelectedItem is not AndroidFile file) return;
        if (file.IsDirectory)
        {
            await LoadDirectoryAsync(file.Path);
            return;
        }

        // 软链接指向目录时（如 /sdcard、/bin、/etc）直接进入目录，否则按普通文件下载
        if (file.IsLink && !string.IsNullOrEmpty(_serial))
        {
            var check = await Adb.ShellTextAsync(_serial, WrapShell($"[ -d {Adb.Quote(file.Path)} ] && echo DIR"), 6000);
            if (check.Contains("DIR", StringComparison.Ordinal))
            {
                await LoadDirectoryAsync(file.Path);
                return;
            }
        }

        await DownloadSelectedAsync();
    }

    private void FileOption_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _settings.ShowHiddenFiles = ChkShowHidden.IsChecked == true;
        _settings.UseRoot = ChkUseRoot.IsChecked == true;
        SettingsStore.Save();
        RebuildBreadcrumbs(_currentPath);
        _ = LoadDirectoryAsync(_currentPath);
    }
}
