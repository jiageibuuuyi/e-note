# 附件移动、恢复与剪贴板验证报告 (T06 / T07)

日期：2026-10-25
任务：T06 可恢复复制基础 / T07 移动、恢复、拖入和剪切粘贴

## 1. 状态机与安全清源机制

- **状态流转**：`planned` → `copying` → `verified` → `published` → `registered` → `cleaning` → `done`（异常分支：`source_cleanup_pending` / `failed` / `cancelled` / `needs_review`）。
- **物理身份校验 (`WindowsSourceCleanup`)**：基于 Win32 `GetFileInformationByHandle` 读取 `VolumeSerialNumber + FileIndex`，并在删除前以 `FileShare.None` 独占句柄复验 SHA-256，防止“校验后、删除前”文件被外部替换。
- **目录非递归清源**：仅逐项清理已验证清单中的文件，随后自底向上移除已验证且确认为空的目录；绝不使用 `Directory.Delete(recursive: true)` 强删源树。

## 2. 故障矩阵与剪贴板验证结果

| 验证场景 | 预期结果 | 实测结果 |
|---|---|---|
| 嵌套文件夹、空目录与 1MiB 流式复制 | 目录结构、空文件夹与 SHA-256 完全一致 | 通过 (`AttachmentCopyTests`) |
| 故障A：`verified` 后强退 | 源未提前删除；重启对账后自动完成发布、登记与清源 | 通过 (`AttachmentRecoveryTests`) |
| 故障B：`published` 后、DB 更新前强退 | 重启识别已就位目标并复用同一 `attachment_id` 完成登记 | 通过 (`AttachmentRecoveryTests`) |
| 故障C：`registered` 后删源被拒绝 | 正式附件保留可用，源保留，状态标记为 `source_cleanup_pending` | 通过 (`AttachmentRecoveryTests`) |
| 故障D：目录复制期间新增文件 `X` | 清单内文件清源，新增文件 `X` 完整保留，状态报告 `SourceChanged` | 通过 (`AttachmentRecoveryTests`) |
| 故障E：清理前源文件被替换 | 检出身份/哈希变化，进入 `needs_review`，替换文件不删除 | 通过 (`AttachmentRecoveryTests`) |
| 故障F：清理中途取消 | 正式附件保留，停止剩余清源，标记 `source_cleanup_pending` | 通过 (`AttachmentRecoveryTests`) |
| 拖入与剪贴板复制/剪切/令牌隔离 | 拖入固定移动；无剪切标记默认复制；纯文本路径不作文件导入；传输中新复制内容不被覆盖 | 通过 (`AttachmentMoveTests`) |
