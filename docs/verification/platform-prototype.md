# 平台能力与桌面嵌入原型验证报告 (T01)

日期：2026-10-25
任务：T01 建立工程与平台风险原型

## 1. 构建与运行环境

- **操作系统**：Windows x64 (`Win32NT`)
- **.NET SDK**：`10.0.402` (`global.json` 锁定，`rollForward: latestPatch`)
- **UI 框架**：Avalonia `12.1.4`（共享 `net10.0` 目标框架）
- **数据库驱动**：`Microsoft.Data.Sqlite` `10.0.13`
- **锁文件状态**：已开启 `RestorePackagesWithLockFile=true`，全工程生成 `packages.lock.json`

## 2. 桌面宿主探测与平台能力 (`DesktopHostProbe`)

在当前 Windows 环境执行 `DesktopHostProbe.ProbeDesktopHost()` 自动化与窗口探针测试：

- **Progman / SHELLDLL_DefView 发现**：成功定位 `Progman` 与 `SHELLDLL_DefView` 宿主层级，返回 `CapabilitySupport.Supported`。
- **模式切换接口 (`IDesktopIntegration.ApplyModeAsync`)**：
  - `PlacementMode.Topmost`：先执行 `DetachFromDesktopHost` 释放桌面宿主 Owner 关系，再通过 `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)` 置顶，不抢占当前活动窗口焦点。
  - `PlacementMode.EmbeddedDesktop`：先清除 `HWND_TOPMOST`，将面板 Owner 关联至 `SHELLDLL_DefView` 所在宿主容器并置于 `HWND_BOTTOM`，从而在 `Win+D`（显示桌面）时保持可见，且保留独立窗口句柄以支持中文 IME 输入与 OLE 文件拖放。
- **多屏坐标自愈 (`FitToAvailableScreens`)**：当保存的监视器断开或坐标超出当前可用屏幕边界时，自动收回至主屏可见工作区。

## 3. 探针窗口验证项 (`PlatformProbeWindow`)

| 验证项 | 预期行为 | 验证结果 |
|---|---|---|
| 普通窗口基线 | 350×520 DIP 窗口正常渲染，控件可聚焦 | 通过 |
| 中文输入 (IME) | `ImeInputBox` 支持多行输入与焦点切换 | 通过 |
| 文件拖放 (`DataTransfer`) | 拖入文件/文件夹仅读取名称，不移动真实用户文件 | 通过 |
| 置顶与桌面嵌入切换 | 切换不崩溃、可恢复，宿主句柄不泄漏到 Domain 层 | 通过 |
| macOS / Linux 环境 | 共享工程跨平台目标 `net10.0` 编译通过；非 Windows 运行时返回 `CapabilitySupport.Unknown` | 未验证（当前无 macOS/Linux 物理环境，按规范标记为 Unknown/未验证） |
