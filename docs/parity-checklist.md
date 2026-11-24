# 旧版功能对照表（WPF 3.1.0 → Avalonia 3.2.3）

来源：`git show 231b6d7:MainWindow.xaml.cs` 里全部 62 个 `*_Click` 处理器。
规则：每一条都必须有下落，标 ❌ 的必须补上或由老大签字删除。

状态说明：✅ 已有 · 🔁 合并/改名 · ❌ 缺失 · — 设计上取消（需确认）

| # | 旧入口 | 新位置 | 状态 |
|---|--------|--------|------|
| 1 | BtnAddNewDeviceFromMenu | 顶栏设备浮层 → 无线连接 | 🔁 |
| 2 | BtnAdvancedToggle | 设置 → 高级互联 | 🔁 |
| 3 | BtnApps | 左侧导航 → 应用 | 🔁 |
| 4 | BtnAppsDashboard | 首页快捷入口 → 应用 | 🔁 |
| 5 | BtnAuthorLink | 无 | ❌ |
| 6 | BtnCancelRevoke | 停用助手对话框「取消」 | 🔁 |
| 7 | BtnCloseKeyboardHelp | 无（键盘帮助整块缺失） | ❌ |
| 8 | BtnCloseOobe | 无 OOBE，自动连接 | — |
| 9 | BtnClosePopup | 对话框遮罩/取消 | 🔁 |
| 10 | BtnCloseTutorial | 无（新手教程整块缺失） | ❌ |
| 11 | BtnConfirmNewItem | 文件页 → 新建 | ✅ |
| 12 | BtnConfirmRevoke | 设置 → 停用助手 | ✅ |
| 13 | BtnDeviceMenu | 顶栏设备浮层 | 🔁 |
| 14 | BtnDirUp | 文件页 → 上一级 | ✅ |
| 15 | BtnDisconnect | 首页 → 断开连接 | ✅ |
| 16 | BtnEnterAdvanced | 首页快捷入口 → 设置 | 🔁 |
| 17 | BtnExitApps | 左侧导航 | 🔁 |
| 18 | BtnExitFileManager | 左侧导航 | 🔁 |
| 19 | BtnExitSms | 左侧导航 | 🔁 |
| 20 | BtnFileTransfer | 左侧导航 → 文件 | 🔁 |
| 21 | BtnGithubLink | 设置 → GitHub | ✅ |
| 22 | BtnKeyboardHelp | 无 | ❌ |
| 23 | BtnMediaNext | 媒体卡 → 下一首 | ✅ |
| 24 | BtnMediaPlayPause | 媒体卡 → 播放/暂停 | ✅ |
| 25 | BtnMediaPrev | 媒体卡 → 上一首 | ✅ |
| 26 | BtnOobeActionConnect | 设备浮层 → 连接 | 🔁 |
| 27 | BtnOobeActionPair | 设备浮层 → 配对并连接 | 🔁 |
| 28 | BtnOobeBack | 无 OOBE | — |
| 29 | BtnOobeSelectWired | 无 OOBE，USB 自动识别 | — |
| 30 | BtnOobeSelectWireless | 无 OOBE，走设备浮层 | — |
| 31 | BtnOpenPermissionSettings | 设置 → 安装助手时的授权引导 | ✅ |
| 32 | BtnOpenTerminal | 设置 → 打开终端 | ✅ |
| 33 | BtnPermManualContinue | 授权引导轮询（90s） | ✅ |
| 34 | BtnPrivacyAgree | 安装助手确认框 | 🔁 |
| 35 | BtnQuality | 投屏卡 → 流畅/均衡/高清 | ✅ |
| 36 | BtnRebootMenu | 首页 → 重启（四项） | ✅ 3.2.3 补回 |
| 37 | BtnRefreshApps | 应用页 → 刷新 | ✅ |
| 38 | BtnRefreshDir | 文件页 → 刷新 | ✅ |
| 39 | BtnRefreshSms | 短信页 → 刷新 | ✅ |
| 40 | BtnScrcpySettings | 投屏卡（参数内联） | 🔁 |
| 41 | BtnScreenCast | 首页 → 开始投屏 | ✅ |
| 42 | BtnShowNewItemPopup | 文件页 → 新建 | ✅ |
| 43 | BtnShowRevokeAuth | 设置 → 停用助手 | 🔁 |
| 44 | BtnShowTutorial | 无 | ❌ |
| 45 | BtnSms | 左侧导航 → 短信 | 🔁 |
| 46 | BtnSmsDashboard | 首页快捷入口 → 短信 | 🔁 |
| 47 | BtnToggleAudio | 投屏卡 → 声音 | ✅ |
| 48 | BtnToggleAutoStart | 设置 → 开机自启动 | ✅ |
| 49 | BtnToggleControl | 投屏卡 → 鼠标控制 | ✅ |
| 50 | BtnToggleHiddenFiles | 文件页 → 显示隐藏文件 | ✅ |
| 51 | BtnToggleKeyboardMode | 投屏卡 → UHID 键盘 | ✅ |
| 52 | BtnToggleRoot | 文件页 → Root 模式 | ✅ |
| 53 | BtnToggleScreenMode | 投屏卡 → 息屏投屏 | ✅ |
| 54 | BtnToggleWirelessMode | 设备浮层（USB/WiFi 自动判断） | — |
| 55 | BtnTransferSettings | 文件页工具栏 | 🔁 |
| 56 | BtnUpdateApk | 设置 → 手机助手版本 + 更新按钮 | ✅ 3.2.2 补回 |
| 57 | MenuDelete | 文件页 → 删除 | ✅ |
| 58 | MenuDownload | 文件页 → 下载 | ✅ |
| 59 | MenuRebootEDL | 重启 → EDL | ✅ 3.2.3 |
| 60 | MenuRebootFastboot | 重启 → Bootloader | ✅ 3.2.3 |
| 61 | MenuRebootNormal | 重启 → 系统 | ✅ 3.2.3 |
| 62 | MenuRebootRecovery | 重启 → Recovery | ✅ 3.2.3 |

## 仍缺（必须在下一版处理或签字删除）

1. 键盘帮助（`BtnKeyboardHelp` / `BtnCloseKeyboardHelp`）——投屏按键说明面板
2. 新手教程（`BtnShowTutorial` / `BtnCloseTutorial`）——首次使用引导
3. 作者链接（`BtnAuthorLink`）——旧版指向 miqwq.com

## 验收口径

- 不是"编译通过"，是这张表逐行勾掉。
- 每条 ✅ 都要在真机上按用户动线走一遍：点应用要投屏、点重启要弹菜单、收托盘要能收通知。
- 删除任何一条，必须老大签字。
