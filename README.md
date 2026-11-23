# 棱角互联 · LengJiaoConnect

让跨设备协同回归纯粹：投屏、应用、短信、文件、剪贴板与通知，一个窗口全搞定。

界面用 **Avalonia + SukiUI** 重写，深色玻璃质感、冰蓝强调色、自绘标题栏与分组导航。

> 旧 WPF 版源码保留在 [`legacy/wpf`](legacy/wpf)，仅作参照，不参与编译。

## 核心特性

- **投屏与应用直达**（基于 scrcpy）：三档画质、声音、鼠标控制、UHID 键盘、息屏投屏；应用列表全量拉取、搜索、常用应用置顶、点按即启动。
- **文件管理**：浏览、上传、下载、新建、重命名、删除，隐藏文件与 Root 穿透，支持多选与拖拽上传；中文文件名不再被截断。
- **高级互联**：本地 socket 双向通信，剪贴板同步、通知转发（带防风暴过滤）、短信收件箱、媒体控制。
- **双模连接**：USB 直连与 Android 11+ 无线调试配对，记住上次设备，断线自动重连。
- **性能监控**：CPU、内存、存储、电池温度、实时上下行网速；只在窗口可见时采样，隐藏即停。
- **日常体验**：托盘常驻、关闭到托盘、开机自启、单实例、日志、版本与内容校验。

## 环境

- Windows 10/11 x64
- .NET 8 SDK（构建）/ .NET 8 Desktop Runtime（运行）
- `Tools\adb.exe`、`Tools\scrcpy\`（仓库自带）
- 手机端助手：`HelperApk\LengJiaoHelper.apk`（Android 7.0+）

## 构建

```powershell
dotnet build .\LengJiaoConnect.Avalonia.csproj -c Release
```

产物：`bin\Release\net8.0\LengJiaoConnect.exe`（`Tools`、`HelperApk` 会一并复制）。

发布绿色版：

```powershell
dotnet publish .\LengJiaoConnect.Avalonia.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

## 手机端助手

```powershell
cd .\HelperApk
powershell -ExecutionPolicy Bypass -File .\build.ps1
adb install -r -t -g .\LengJiaoHelper.apk
adb shell cmd notification allow_listener com.lengjiao.helper/com.lengjiao.helper.HelperService
adb shell pm grant com.lengjiao.helper android.permission.READ_SMS
```

`build.ps1` 会自动查找 Android SDK build-tools 与 JDK；脚本本身必须保存为 **UTF-8 with BOM**，否则 PowerShell 5.1 会按 GBK 读取、中文注释会破坏语法。

## 通信协议

PC 与手机助手之间是本地 socket（`lengjiao_helper`，经 `adb forward tcp:8889`），**一行一个 JSON**。

PC → 手机：

```json
{"t":"ping"}
{"t":"clip","text":"..."}
{"t":"perm"}
{"t":"perm_page"}
{"t":"sync","what":"apps"}
{"t":"sync","what":"sms"}
{"t":"sync","what":"media"}
{"t":"media","cmd":"play|pause|next|prev"}
```

手机 → PC：

```json
{"t":"pong"}
{"t":"clip","text":"..."}
{"t":"notif","app":"...","title":"...","text":"..."}
{"t":"sms","addr":"...","date":1730000000000,"body":"..."}
{"t":"app","pkg":"...","name":"...","icon":"<base64 png>"}
{"t":"apps_end","count":261}
{"t":"media","title":"...","artist":"...","playing":true,"pkg":"..."}
{"t":"perm","sms":true,"notif":true}
```

## 文件传输与中文名

Windows 上的 adb 在「目标参数是目录」时会自己推导文件名，遇到中文会在 UTF-8 字符中间截断：截在合法边界就成功但名字残缺，截在字符中间就整条失败。

所以本项目**永远传完整路径**：

- 上传：`adb push <本地文件> <远端目录>/<文件名>`
- 下载：`adb pull <远端文件> <本地目录>\<文件名>`

参数一律通过 `ProcessStartInfo.ArgumentList` 传递，不手工拼引号。

## 目录

```
.
├─ Assets/            图标与字体
├─ HelperApk/         手机端助手源码、清单、构建脚本、成品 APK
├─ Models/            设备、应用、短信、文件、媒体模型
├─ Services/          Adb / HelperClient / Scrcpy / 设置 / 日志
├─ MainWindow.*.cs    主窗口与各页面
├─ Tools/             adb、scrcpy
└─ legacy/wpf/        旧 WPF 版源码（不参与编译）
```

## 日志

`%APPDATA%\LengJiaoConnect\logs\app-yyyyMMdd.log`

## 说明

- 本工程不读取、不上传任何数据；所有 adb / socket 通信都在本机与数据线（或局域网）之间完成。
- 手机端助手只申请 `READ_SMS` 与 `QUERY_ALL_PACKAGES`，用于短信读取与应用列表；通知通过 `NotificationListenerService` 授权。
