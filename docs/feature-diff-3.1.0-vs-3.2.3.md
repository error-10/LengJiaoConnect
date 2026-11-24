# 旧版 3.1.0 → 新版 3.2.3 功能差异核对

- 旧版源码：`git archive 231b6d7`（56 个文件，WPF，`MainWindow.xaml` 1002 行 / `MainWindow.xaml.cs` 2471 行）
- 新版源码：当前 `main`（`49b6ec8`，Avalonia + SukiUI）
- 方法：抽取旧版 129 个命名控件、62 个事件处理器、全部 adb shell 命令、README 的「核心特性」承诺，逐项在新版定位；不看名字看行为。

## 0. 结论

| 类别 | 数量 |
| --- | --- |
| 功能缺失 | 4 |
| 体验退化 | 3 |
| 交互方式变化（非缺失） | 6 |
| 对齐或增强 | 30+ |

**必须先补的 5 项**：输入法防呆、应用列表缓存、键盘帮助、新手教程、作者链接。

## 1. 功能缺失（旧版有，新版没有）

| # | 功能 | 旧版证据 | 新版现状 | 影响 |
| --- | --- | --- | --- | --- |
| 1 | 输入法防呆：投屏时强制 US 键盘、退出还原 | `MainWindow.xaml.cs:1779-1789` P/Invoke `LoadKeyboardLayout` / `ActivateKeyboardLayout` / `WM_INPUTLANGCHANGEREQUEST`；`:1861` 加载 `00000409`；`:1901` 向 scrcpy 窗口发消息 | `Services/ScrcpyService.cs` 全文无键盘布局处理（0 处匹配） | 中文输入法下按键被系统截走，投屏里打字失灵——旧 README 明确承诺过 |
| 2 | 键盘帮助面板 | `PanelKeyboardHelp` + `BtnKeyboardHelp_Click` + `BtnCloseKeyboardHelp_Click`，解释为什么必须切英文、SDK 与 UHID 两种注入模式的区别 | 无对应界面/按钮 | 用户遇到打字问题无处查 |
| 3 | 新手教程 | `OobeTutorialPanel` + `BtnShowTutorial_Click` / `BtnCloseTutorial_Click`，含配对步骤与输入法说明 | 无 | 首次使用无引导（现只有连接浮层） |
| 4 | 作者链接 | `BtnAuthorLink_Click` → `miqwq.com`（旧 XAML「一条橙鱼」） | 只有 GitHub 按钮 | 关于页信息缺失 |

## 2. 体验退化（功能还在，但比以前差）

| # | 项目 | 旧版 | 新版 | 影响 |
| --- | --- | --- | --- | --- |
| 1 | 应用列表缓存 | `apps_cache_{serial}.json`（`MainWindow.xaml.cs:1174`），启动先读缓存再后台同步 | 无缓存，每次连接全量拉 261 个应用与图标 | 每次启动应用页空白数秒；离线时完全空白 |
| 2 | 常用应用维度 | `app_history_{serial}.json`，按设备统计 | `recent-apps.json`，全局一份 | 两台设备互相污染常用列表 |
| 3 | 权限说明 | `PanelPrivacyPolicy` 整页说明（读短信/应用列表/媒体、为什么需要、可撤销） | 仅安装确认弹窗里两句话 | 用户不清楚授权范围，容易拒绝 |

## 3. 交互方式变化（不是缺失，确认即可）

| 项目 | 旧版 | 新版 |
| --- | --- | --- |
| 首次连接 | OOBE 四步向导（有线/无线配对/连接/教程） | 顶栏设备胶囊 → 浮层（设备列表 + 无线连接 + 配对码） |
| 投屏设置 | 独立弹窗 `PanelScrcpySettings` | 首页投屏卡内联 chip + 开关 |
| 系统设置 | 独立弹窗 `PanelTransferSettings` | 左侧「设置」页 |
| 画质选择 | 三个大按钮 | 三枚 filter chip |
| 无线/USB 模式 | OOBE 里手动选 | 按 serial 自动判定，配对流程独立 |
| 文件操作 | 右键菜单（下载到桌面 / 彻底删除） | 工具栏按钮 + 下载对话框 |

## 4. 已对齐或增强（逐项核对过）

- 投屏：画质三档、息屏、音频、鼠标控制、UHID 键盘、停止投屏、单例窗口（`ScrcpyService`）
- 应用：全量列表、图标、搜索、常用置顶、点击启动 + 自动投屏（`MainWindow.Apps.cs`）
- 文件：拖拽上传、双击进入/下载、新建、重命名、删除、上级、路径跳转、隐藏文件、Root（`MainWindow.Files.cs`）
- 剪贴板双向、通知转发（含右下角浮窗 `ToastWindow`）、短信 50 条、媒体控制（`HelperClient` + `HelperService` v4）
- 设备：2 秒轮询、自动重连、多设备切换、无线连接、Android 11+ 配对码、断开（`MainWindow.Devices.cs`）
- 设备状态：电量、WiFi 名称与信号、蓝牙、连接方式（`ApplyDeviceUi`）
- 重启菜单四项（系统/Bootloader/Recovery/EDL，含 EDL 警告）
- 更新检查：电脑版（网页 version + 接头暗号校验）、手机助手版本比对与一键更新
- 托盘常驻、关闭到托盘、开机自启（跟随当前 exe 路径）、单实例、日志、全局异常兜底
- 助手 APK：v1.2 / versionCode 4 / 自定义图标 / 首次安装引导 / 权限引导（通知使用权、短信）

## 5. 建议修复顺序

1. **应用列表缓存**（体验最直观，离线也能看）
2. **输入法防呆**（README 承诺，影响投屏打字）
3. 键盘帮助 + 新手教程（同一套弹窗组件，一起做）
4. 作者链接（一行）
5. 权限说明页（可合并进首次安装引导）

> 除以上 5 项外，未发现其它旧版功能在新版缺失。

## 6. 修复状态（2026-11-24）

| 项目 | 状态 | 实现位置 |
| --- | --- | --- |
| 应用列表缓存 | 已补 | `MainWindow.Apps.cs` `LoadAppsCacheAsync` / `SaveAppsCache`，缓存到 `%APPDATA%\LengJiaoConnect\cache\apps-{serial}.json`，连接设备时先读缓存 |
| 输入法防呆 | 已补 | `Services/ScrcpyService.cs` `ApplyUsKeyboard` / `RestoreKeyboard`，窗口出现后 `WM_INPUTLANGCHANGEREQUEST` 切 US，退出还原 |
| 键盘帮助 | 已补 | 设置 → 关于 → 键盘帮助（`ShowKeyboardHelp_Click` + `InfoOverlay`） |
| 新手教程 | 已补 | 设置 → 关于 → 新手教程（`ShowTutorial_Click`） |
| 作者主页 | 已补 | 设置 → 关于 → 作者主页（`BtnOpenAuthor_Click` → `https://miqwq.com`） |
| 常用应用按设备隔离 | 未改 | 仍是全局 `recent-apps.json`（影响小，暂不动） |
| 权限说明页 | 未改 | 仍由首次安装引导承担（文案已说明两项权限） |

`dotnet build -c Release`：0 error。应用缓存与输入法防呆需真机回归（当前手机离线）。

