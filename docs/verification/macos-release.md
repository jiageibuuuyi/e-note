# macOS 平台适配与发布验证报告 (T12)

## 1. 目标环境与当前验证状态声明
- **目标基线**：macOS 13+ (`osx-arm64` / `osx-x64`)
- **当前验证环境**：当前构建机器为 Windows x64 (`Microsoft Windows NT 10.0.26100.0`)，无物理 macOS 宿主环境。
- **真实能力状态（严格遵循开发实施指南第 9.1 / 9.4 节）**：
  - `EmbeddedDesktop`：`Unknown`（未在物理 macOS Spaces / 显示桌面环境实机验证，禁止以跨平台编译通过代替运行验证）
  - `AbsolutePosition`：`Unknown`（非 macOS 宿主运行时返回 `Unknown`）
  - `Tray` (NSStatusBar MenuBar)：`Unknown`
  - `GlobalShortcut`：`Unknown`

## 2. 已完成的工程交付与自动化跨平台迁移验证
- **平台适配层**：`src/DesktopNotes.Platforms/MacOS/MacOsDesktopIntegration.cs`
  - 实现 `~/Library/LaunchAgents/com.desktopnotes.app.plist` 登录启动项读写与状态同步。
  - 实现多显示器工作区安全边界回退 `FitToAvailableScreens`。
- **打包清单与脚本**：`installer/macos/Info.plist`、`installer/macos/package-macos.sh`
- **Windows ↔ macOS 备份包跨平台迁移测试 (`CrossPlatformMigrationTests`)**：
  - 验证包含 Unicode (NFC/NFD 组合字符及 Emoji)、大小写冲突文件、跨平台路径分隔符的完整备份包在 Windows 与模拟 macOS 存储根之间双向迁移；
  - 验证旧设备 `source_cleanup_pending` 作业在跨平台恢复后转为 `needs_review`，零次触发源清理。
