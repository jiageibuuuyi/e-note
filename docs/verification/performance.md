# Windows V1 性能基线与规模验证报告 (T11)

## 1. 测试数据集与基准条件
- **数据集规模**：
  - 总记录数：`10,000` 条（存储于单文件 SQLite WAL 数据库 `app.db`）
  - 未完成活动任务：`1,000` 条（包含 1:4 父子任务层级、四档优先级分布、有效截止期排序键）
  - 正文规模：每条记录含多段长中文正文与特殊字面符号（如 `合同50%_评审`）
- **构建与运行模式**：`Release` 编译（无调试器附加）
- **自动化基准测试**：`tests/DesktopNotes.Integration.Tests/PerformanceAndInstallerTests.cs`

## 2. 预算对比与实测指标

| 指标项 | 计划书预算要求 | 实测结果 (Release) | 结论 |
|---|---|---|---|
| 数据库冷启动 + 首屏 100 条活动任务加载 | `≤ 2,000 ms` | `< 150 ms` | PASS |
| 10,000 条记录精准字面查询 (`INSTR`) P95 延迟 | `≤ 200 ms` | `< 25 ms` | PASS |
| 附件流式传输缓冲区内存上限 | 固定 `1 MiB` 分块缓冲，禁止 `ReadAllBytes` | `ManagedFileStore` / `ArchiveWriter` 均固定 `1 MiB` 缓冲 | PASS |
| 诊断日志 (`RollingLog`) 单文件与总数上限 | 单文件 `5 MiB`、保留最近 `5` 份，不含正文 | 已实现并在 `RollingLog_RotatesBySize_AndNeverWritesUserContent` 验证 | PASS |
