# Windows 桌面驻留、多显示器恢复与单实例验证报告 (T09)

## 1. 验证范围
- **配置持久化与防抖 (`SettingsService` / `SqliteStore.Settings`)**：
  - 首次启动初始化 `schemaVersion=1`、`deviceId`、默认窗口模式 `desktop_hosted`、默认附件模式 `copy`、提醒提前 30 分钟与每日 09:00 汇总。
  - 位置锁定 (`IsPositionLocked=true`) 时忽略窗口拖拽坐标更新。
  - 窗口移动/缩放坐标采用 500ms 防抖合并写入 SQLite `settings` 表，避免拖动过程中高频落盘。
- **多显示器断开与分辨率变化恢复 (`ValidateAndRecoverWindowPositionAsync`)**：
  - 当保存的窗口坐标脱离所有活动显示器工作区（或可见重叠面积不足 30%）时，自动重定位至主显示器工作区右上角安全区域 `(Right - Width - 32, Top + 32)` 并持久化。
- **单实例激活 (`SingleInstanceGuard`)**：
  - 基于用户级 Named Mutex + Named Pipe 实现单实例守卫；重复启动进程时向主实例发送 `ACTIVATE` 信号唤醒主面板并安全退出。
- **开机自启 (`DesktopHostProbe`)**：
  - 以操作系统当前用户注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 实际状态为准，并在安装器初始化了 `.startup_intent` 标记文件时自动同步。
