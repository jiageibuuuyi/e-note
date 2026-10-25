# Windows V1 交付验收与验证矩阵报告 (T11)

## 1. 环境与构建信息
- **操作系统**：Windows 10/11 x64 (`Microsoft Windows NT 10.0.26100.0`)
- **SDK 与框架**：`.NET SDK 10.0.402` (`net10.0`)，`Avalonia 12.1.4`，`Microsoft.Data.Sqlite 10.0.13`
- **构建配置**：`Release`（启用 `NuGet.Config` 与 `packages.lock.json` 锁定依赖）
- **安装器脚本**：`installer/windows/DesktopNotes.iss`（Inno Setup 用户级安装 `PrivilegesRequired=lowest`，应用安装目录与 `%LOCALAPPDATA%\DesktopNotes\data\app.db` 及附件根目录严格分离，卸载默认保留用户数据库与受管附件）

## 2. 第 12 节验证矩阵逐项结果

| 类别 | 必测用例 | 验证方式与测试位置 | 结果 |
|---|---|---|---|
| 业务 | 空白校验、30 字素+emoji 标题截断、四级排序、空 deadline 置底、同值稳定排序 | `RecordRulesTests`、`QueryTests` | PASS |
| 搜索 | `合同`/`合 同`/`%_`、大小写精确匹配、单引号、正文尾部、空查询 | `RecordRulesTests`、`SqliteSchemaTests`、`QueryTests` | PASS |
| 继承 | 父修改实时传播、独立空值 (`DueSpec.Empty`)、同值覆盖、恢复继承、日期冲突回滚、完成快照冻结 | `RecordRulesTests`、`InheritanceTransactionTests`、`RecordServiceTests` | PASS |
| 状态 | 完成/重开、父子批量完成、回收站删除批次 (`delete_batch_id`)、父子恢复 | `RecordServiceTests`、`PanelAndDetailTests` | PASS |
| 附件 | 同盘/跨盘流式复制、嵌套目录与空目录、同名独立 UUID、空间不足/只读/自导入拒绝 | `AttachmentCopyTests` | PASS |
| 崩溃 | `planned`/`copying`/`verified`/`published`/`registered`/`cleaning` 故障矩阵 A–F | `AttachmentRecoveryTests` | PASS |
| 剪贴板 | Explorer `Copy`/`Move`、未知标志降级 `Copy`、拖拽固定 `Move`、`ClipboardToken` 隔离 | `AttachmentMoveTests`、`docs/verification/clipboard.md` | PASS |
| 日期与提醒 | 日期型次日 `00:00` 边界、精确时间 UTC 瞬时、夏令时转换日、先 claim 后投递防重 | `DeadlineRulesTests`、`ReminderTests`、`CalendarQueryTests` | PASS |
| 窗口与设置 | 宿主探测与置顶回退、多屏断开坐标恢复、500ms 位置防抖、单实例锁、开机启动状态同步 | `PlatformProbeTests`、`SettingsAndShellTests`、`docs/verification/windows-shell.md` | PASS |
| 备份与恢复 | 在线备份 7 份轮转、双根目录+空目录导出恢复、`../` 路径穿越拒绝、大小写冲突映射、跨设备旧作业零次清源调用、恢复中断日志回滚/续传 | `BackupRestoreTests`、`RestorePathTests` | PASS |
| 性能与安装 | 10,000 条记录（1,000 活动任务含子任务与长中文正文）Release 启动与字面检索 P95、安装器首启意图导入、滚动日志脱敏轮转 | `PerformanceAndInstallerTests`、`docs/verification/performance.md` | PASS |
