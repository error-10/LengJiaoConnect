<div align="center">

<img src="Assets/app-logo.png" alt="棱角互联 Logo" width="84" />

# 棱角互联 · LengJiaoConnect

**Windows × Android 跨设备互联工具**  
投屏、应用直达、文件与 Root 管理、双向剪贴板、通知浮窗、短信验证码与实时性能监控，一个轻量窗口全搞定。

[![Release](https://img.shields.io/github/v/release/error-10/LengJiaoConnect?style=flat-square&color=6FD3FF)](https://github.com/error-10/LengJiaoConnect/releases)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-12%20%2B%20SukiUI-0B5394?style=flat-square)](https://avaloniaui.net/)
[![Android](https://img.shields.io/badge/Android-7.0%2B-3DDC84?style=flat-square)](HelperApk/)
[![Platform](https://img.shields.io/badge/Windows-10%2F11%20x64-0078D6?style=flat-square)](#环境要求)

[下载最新版 Release](https://github.com/error-10/LengJiaoConnect/releases/latest) · [快速上手](#快速上手) · [通信协议](#通信协议) · [从源码构建](#从源码构建)

</div>

---

## 为什么做这个

市面上跨屏协同工具要么绑定品牌、要么常驻全家桶。**棱角互联**把核心能力收敛进一个基于 **Avalonia 12 + SukiUI** 的深色毛玻璃客户端和一个约 **160 KB** 的极简 Android 助手，全部通信仅在 USB 数据线或局域网内完成，无云端账号、无数据上传。

---

## 核心特性

| 模块 | 能力一览 |
| --- | --- |
| **投屏与控制** | 基于 `scrcpy 3.3.4`：三档画质（流畅 `2M/800p/30fps` · 均衡 `8M/1600p/60fps` · 高清 `24M/原画/120fps`）、音频转发、鼠标控制、UHID 硬件键盘、息屏投屏；窗口弹出自动切 US 键盘防中文输入法截键，退出自动恢复系统自动旋转状态。 |
| **应用直达** | 拉取设备可启动应用与图标并本地按设备缓存（秒开不空白）；实时搜索、常用应用带图标置顶与启动计数徽章；点击图标自动启动应用并拉起投屏；支持拖拽或选择批量安装 APK。 |
| **文件与 Root 管理** | 可点击面包屑导航 + 快捷目录胶囊（`SD卡` / `下载` / `相册` / Root 根目录）；完整路径传输彻底避免 Windows `adb` 中文文件名截断；自动解引用 `/sdcard` 等软链接目录；Root 模式支持无 `su` 设备自动回退及 `/data/local/tmp` 中转读写；支持多选、拖拽上传及 `F5` / `Backspace` / `F2` / `Delete` 快捷键。 |
| **高级互联** | 本地 `adb forward` + 按行 JSON 持久通道（带 `ping/pong` 应用层握手与心跳断线检测）：PC 端 `WM_CLIPBOARDUPDATE` 实时监听双向剪贴板、手机通知防风暴转发至桌面右下角独立浮窗（主窗口收进托盘依然可见，动态防重叠、点击即收起）、媒体播放信息与切歌播控。 |
| **短信与验证码** | 同步最近 50 条收件箱短信，自动识别中英文 4–8 位验证码并在列表打标，详情页提供「一键复制验证码」与「复制全文」。 |
| **双模连接与多设备** | USB 即插即连 + Android 11+ 无线调试标准配对（配对端口与连接端口分离）；顶栏设备浮层支持多设备一键切换、局域网 mDNS 无线设备自动发现、一键重连上次设备与主动断开防拉回。 |
| **性能与设备工具** | CPU、内存、存储、电池温度（动态阈值变色进度条）与全活跃网卡实时上下行网速，仅在窗口可见时采样；首页提供四项快捷重启菜单（系统 / Bootloader / Recovery / 高通 EDL）。 |
| **软件内双端更新** | 设置页内置「软件更新」中心与专属更新弹窗：同时校对 Windows 客户端（带 CDN 缓存识别、服务器校时与防篡改校验语）与手机端助手版本，发现新版本时侧栏自动亮起提醒。 |

---

## 快速上手

1. 前往 [Releases](https://github.com/error-10/LengJiaoConnect/releases/latest) 下载 `LengJiaoConnect-x.x.x-win64.zip` 并解压运行 `LengJiaoConnect.exe`（需安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)）。
2. **有线连接**：在手机「开发者选项」中打开 **USB 调试**，数据线连接电脑并在手机端勾选始终允许。
3. **无线连接（Android 11+）**：
   - 打开手机 **无线调试 → 使用配对码配对设备**；
   - 点击软件右上角设备胶囊 →「新设备需要配对码？」→ 输入 IP、配对端口与 6 位配对码（若局域网 mDNS 可见会自动填入）；
   - 配对成功后会自动探测连接端口并建连；如 mDNS 未广播，从手机「无线调试」主页抄入上方连接端口即可。
4. **启用高级互联**：首次连上设备时会提示安装内置手机助手 `LengJiaoHelper.apk`（仅约 160 KB），按引导授予 **通知使用权** 与 **读取短信** 后即可开启通知浮窗、短信、媒体与应用列表同步；不安装助手仍可正常使用投屏与文件管理。

---

## 环境要求

- **系统**：Windows 10 / 11 x64
- **运行**：.NET 8 Desktop Runtime
- **开发构建**：.NET 8+ SDK；如需重编手机端助手 APK，需 Android SDK（`build-tools` + `platforms`）与 JDK
- **手机端**：Android 7.0+（API 24+）
- **内置组件**：仓库 `Tools/` 目录已自带 `adb.exe`、`fastboot.exe` 与 `scrcpy 3.3.4`

---

## 从源码构建

### Windows 客户端

```powershell
# 编译 Release
dotnet build .\LengJiaoConnect.Avalonia.csproj -c Release

# 发布绿色版（框架依赖）
dotnet publish .\LengJiaoConnect.Avalonia.csproj -c Release -r win-x64 --self-contained false -p:DebugType=None -p:DebugSymbols=false -o .\publish
```

产物位于 `bin\Release\net8.0\LengJiaoConnect.exe`（或 `publish\LengJiaoConnect.exe`），`Tools\` 与 `HelperApk\` 会自动随产物复制。

### Android 手机端助手（`v1.3 / versionCode 5`）

```powershell
cd .\HelperApk
powershell -ExecutionPolicy Bypass -File .\build.ps1

# 手动安装与授权（可选，软件内支持一键安装/升级与授权引导）
adb install -r -t -g .\LengJiaoHelper.apk
adb shell cmd notification allow_listener com.lengjiao.helper/com.lengjiao.helper.HelperService
adb shell pm grant com.lengjiao.helper android.permission.READ_SMS
```

> **开发注意**：
> - `HelperService.java` 使用 `android.jar` 作为 `-bootclasspath` 直接编译，不含 `LambdaMetafactory`，源码中请统一使用匿名内部类，勿写 Java Lambda。
> - `HelperApk\build.ps1` 须保持 **UTF-8 with BOM** 编码，避免 Windows PowerShell 5.1 按 GBK 解析中文注释。

---

## 通信协议

PC 端 [HelperClient.cs](Services/HelperClient.cs) 与手机端 [HelperService.java](HelperApk/HelperService.java) 通过 `adb forward tcp:8889 localabstract:lengjiao_helper` 建立长连接，采用 **UTF-8 按行 JSON（`\n` 分帧）** 协议，建连阶段强制执行 `ping/pong` 应用层握手确认服务存活。

**PC → 手机**

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

**手机 → PC**

```json
{"t":"pong"}
{"t":"clip","text":"..."}
{"t":"notif","app":"...","title":"...","text":"..."}
{"t":"sms","addr":"...","date":1730000000000,"body":"..."}
{"t":"app","pkg":"...","name":"...","icon":"<base64 png>"}
{"t":"apps_end","count":128}
{"t":"media","title":"...","artist":"...","playing":true,"pkg":"..."}
{"t":"perm","sms":true,"notif":true}
```

---

## 工程细节与已知说明

- **中文文件名传输**：Windows 版 `adb` 在目标参数为目录时易在 UTF-8 字符中间截断中文名。本项目全部文件传输均传完整目标路径，并通过 `ProcessStartInfo.ArgumentList` 传参、UTF-8 解码，不手工拼引号。
- **Android 10+ 剪贴板系统限制**：PC → 手机剪贴板在未投屏状态下也可通过助手实时写入；手机 → PC 剪贴板在 Android 10+ 后台受系统隐私策略限制可能无法后台读取（投屏期间可由 `scrcpy` 自身接管双向剪贴板）。
- **数据与日志位置**：配置、应用缓存与日志全部保存在本机 `%APPDATA%\LengJiaoConnect\`（`settings.json`、`recent-apps.json`、`cache\apps-*.json`、`logs\app-yyyyMMdd.log`）。
- **历史 WPF 版本**：旧 WPF 架构源码归档在 Git Tag [`v3.1.0`](https://github.com/error-10/LengJiaoConnect/tree/v3.1.0)。

---

## 目录结构

```text
.
├─ Assets/                  应用图标与字体资源
├─ HelperApk/               手机端助手源码、清单、无 Gradle 构建脚本与成品 APK
├─ Models/                  设备、应用、短信（含验证码提取）、文件、媒体与面包屑模型
├─ Services/                Adb / HelperClient / ScrcpyService / SettingsStore / Log
├─ MainWindow.axaml         主窗口界面与样式（含软件内更新中心与更新弹窗）
├─ MainWindow.*.cs          主窗口核心与各功能页面分片（Home/Devices/Files/Apps/Sms/Perf/Settings）
├─ ToastWindow.cs           桌面右下角独立通知浮窗
├─ Tools/                   内置 adb、fastboot 与 scrcpy 3.3.4
└─ docs/                    版本重构对照与核对记录
```

---

## 隐私与安全

- 所有 ADB 与本地 Socket 通信均仅发生在本机与你的手机之间，不采集、不上传任何设备数据或短信内容。
- 手机端助手仅声明 `READ_SMS` 与 `QUERY_ALL_PACKAGES`，通知同步通过系统 `NotificationListenerService` 显式授权，随时可在设置页一键卸载停用。
- 仅在执行版本更新检查时访问版本校验页（含服务器校时与内容校验），不携带任何设备标识。

## 作者

**by 一条橙鱼** · https://miqwq.com
