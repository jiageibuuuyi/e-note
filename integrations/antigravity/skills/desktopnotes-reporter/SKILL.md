---
name: desktopnotes-reporter
description: 连接电子便签(DesktopNotes)实现全生命周期智能协同：(1) 将AI生成的学习目录、章节学习计划、项目拆解或读书笔记批量导入为多级待办便签或笔记；(2) 支持按ID、标题或关键词安全删除任务与笔记(移入回收站或彻底删除)；(3) 读取真实SQLite便签数据自动生成结构化周报、月报、年报与工作复盘。
---

# 电子便签 (DesktopNotes) 智能计划导入、管理与汇报助手

本 Skill 为本地运行的 **电子便签 (DesktopNotes)** 提供**全生命周期智能数据协同能力**：
1. **正向导入（AI 规划 -> 电子便签）**：将 AI 生成的书籍/课程学习目录、章节学习计划、项目多级拆解（WBS）或知识卡片，一键导入到电子便签中（支持作为**多级树形待办便签**或**独立知识笔记**）。
2. **清理与删除（任务/笔记删除与回收站管理）**：支持按记录 ID、精确标题或关键词删除指定的任务（自动级联删除其子任务）或笔记，默认安全移入回收站，亦支持永久彻底删除或清空回收站。
3. **反向萃取（电子便签 -> 智能汇报）**：从电子便签 SQLite 数据库安全读取已结项任务、进行中待办与备忘，自动提炼生成高品质周报、月报、年报与复盘。

---

## 🚀 场景一：生成学习目录/章节计划并导入电子便签

当用户提出类似需求（如：*“帮我生成《XXX》的学习目录，制定章节学习计划并导入我的电子便签作为便签或笔记”*）时，按以下流程执行：

### 第一步：构建结构化章节目录与学习计划
为每个章节规划清晰的：
- **章节标题 (`title`)**：如 `第 1 章：核心概念与环境搭建`
- **学习指导详情 (`body`)**：包含本章核心知识点、精读建议、实践练习或产出要求（Markdown 格式）
- **子章节/小节拆解 (`children`)**：如 `1.1 ...`、`1.2 ...`、`课后实战练习`
- **时间排期 (`start_date` / `due_date`)**：可根据用户期望周期（如从明天起每章学习 3 天）开启自动排期参数 `auto_schedule_start_date` 与 `days_per_item`。

### 第二步：选择匹配的导入模式 (`import_as`)
1. **`task_tree`（多级待办便签树，默认推荐）**：
   - 将整个学习计划作为「顶层总任务 -> 章节子任务 -> 小节孙任务」导入到电子便签的**「待办」**视图，章节截止日期自动同步至**「任务月历」**视图。
2. **`note_series`（按章拆分为系列独立笔记）**：
   - 将每一章生成为电子便签**「笔记」**标签页下的一张独立长文本便签，小节大纲自动排版为笔记正文 Markdown。
3. **`note_single`（合并为单篇完整目录笔记）**：
   - 将整份学习目录与计划合并保存为一张完整的 Markdown 笔记便签。

### 第三步：调用导入工具落库
- **方式 A（结构化 JSON 树导入，推荐）**：调用 `desktopnotes_import_plan`
- **方式 B（Markdown 大纲直接解析导入）**：调用 `desktopnotes_import_markdown`
- **方式 C（单条快速写入）**：调用 `desktopnotes_create_record`
- **CLI 备用命令**：
  ```bash
  desktopnotes-mcp --import-md plan.md --import-as task_tree --start-date YYYY-MM-DD --days-per-item 3
  ```

---

## 🗑️ 场景二：删除任务与笔记 / 回收站管理

当用户需要删除某项任务、整个章节学习计划或某条笔记时：
1. **调用 `desktopnotes_delete_records` 工具**：
   - **按标题或关键词删除**：传入 `exact_title`（精确标题）或 `search_keyword`（模糊关键词），可配合 `kind` (`"task"` / `"note"` / `"all"`) 过滤。
   - **按 ID 精确删除**：若不确定标题唯一性，先调用 `desktopnotes_list_tasks` 检索目标记录的 `id`，再传入 `record_ids` 列表。
   - **级联子任务处理**：删除父任务时，工具会自动按后序遍历级联处理其下属的所有多级子任务。
2. **安全策略**：
   - 默认使用 `permanent=false`（安全移入电子便签回收站，用户可在客户端随时恢复）。
   - 仅当用户明确要求“永久删除/彻底删除”时设置 `permanent=true`，或要求“清空回收站”时设置 `empty_trash=true`。
3. **CLI 备用命令**：
   ```bash
   # 移入回收站 (默认安全模式)
   desktopnotes-mcp --delete-title "《XXX》章节学习计划" --kind task
   # 永久彻底删除
   desktopnotes-mcp --delete-keyword "测试" --kind note --permanent
   # 清空回收站
   desktopnotes-mcp --empty-trash
   ```

---

## 📊 场景三：提取便签数据生成周报、月报、年报

当用户希望生成周报、月报、年报、季度总结或盘点待办进展时：

### 第一步：明确统计周期与汇报风格
- 默认自然周期：周报 `weekly`（本周）、月报 `monthly`（本月）、年报 `yearly`（今年）。
- 支持 4 种专业模板风格：`standard`（通用职场）、`engineering`（研发技术交付）、`okr`（目标与关键结果复盘）、`executive`（管理层极简看板）。

### 第二步：调用工具提取真实数据
- 调用 MCP 工具 `desktopnotes_get_report_data`（获取结构化数据）或 `desktopnotes_generate_report`。
- 或执行 CLI 命令：`desktopnotes-mcp --report [weekly|monthly|yearly] --template [standard|engineering|okr|executive]`

### 第三步：深度润色与价值升华
1. **提炼业务与技术价值**：按项目/模块主线（父任务）聚类，将零散的任务标题升华为专业交付成果描述。
2. **量化指标看板**：呈现本期已结项数、P0/P1 关键攻坚数、进行中事项与逾期风险预警。
3. **双向归档闭环**：生成汇报后，可询问用户是否需要调用 `desktopnotes_create_record` 将报告直接保存回电子便签作为笔记归档，或将下周计划批量导入为新待办。
