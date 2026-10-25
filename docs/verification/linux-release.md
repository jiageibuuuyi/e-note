# Linux 平台适配与发布验证报告 (T12)

## 1. 目标环境与当前验证状态声明
- **目标基线**：Ubuntu 22.04/24.04 LTS (`linux-x64`，GNOME / KDE，X11 与 Wayland 会话)
- **当前验证环境**：当前构建机器为 Windows x64 (`Microsoft Windows NT 10.0.26100.0`)，无物理 Linux 桌面环境。
- **真实能力状态与 Wayland 限制记录（严格遵循开发实施指南第 9.1 / 9.4 节与 T12）**：
  - **非 Linux 运行时探针**：全部返回 `CapabilitySupport.Unknown`（绝不将跨编译通过冒充为实机验证 `Supported`）。
  - **Wayland 会话 (`XDG_SESSION_TYPE=wayland`)**：
    - `EmbeddedDesktop`：`Unsupported`（Wayland 核心协议禁止普通客户端将顶层窗口嵌入桌面背景层）
    - `AbsolutePosition`：`Unsupported`（Wayland 合成器不暴露全局屏幕绝对坐标定位，窗口位置由合成器管理）
    - `GlobalShortcut`：`Unsupported`（需通过桌面门户或合成器快捷键绑定替代）
    - **可接受替代方案**：在 Wayland 下明确告知能力限制并以常规/置顶面板模式运行，不宣称与 Windows 桌面嵌入等价。

## 2. 已完成的工程交付与自动化跨平台迁移验证
- **平台适配层**：`src/DesktopNotes.Platforms/Linux/LinuxDesktopIntegration.cs`
  - 实现 XDG Autostart (`~/.config/autostart/desktopnotes.desktop`) 登录自启项读写与状态同步。
  - 实现 X11 / Wayland 会话能力区分报告。
- **打包清单与脚本**：`installer/linux/desktopnotes.desktop`、`installer/linux/package-linux.sh`
- **Windows ↔ Linux 备份包跨平台迁移测试 (`CrossPlatformMigrationTests`)**：
  - 验证 Linux 大小写敏感目录中同名不同大小写文件 (`Report.txt` vs `report.txt`) 及包含 Windows 非法字符 (`meeting:notes?.txt`) 的附件在迁移至 Windows 时自动生成确定性无损映射 (`report__case_1.txt`、`meeting_notes_.txt`)，绝不覆盖数据。
