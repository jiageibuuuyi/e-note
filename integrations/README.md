# 电子便签 (DesktopNotes) · AI Agent 跨平台双向 MCP 插件

面向多智能体与 AI 编程助手的开放接口扩展包，支持一键接入 **OpenCode**、**OpenAI Codex**、**DSH (DeepSeek Harness)**、**Google Antigravity** 以及各类标准 MCP 客户端（Cursor、Claude Code、Windsurf 等），实现与电子便签本地数据库的**双向智能协同**：

1. **📥 智能计划与笔记批量导入（AI -> 电子便签）**：让任意 Agent 为你制定学习目录、章节学习计划、项目多级拆解（WBS）或读书笔记，一键导入为电子便签中的**多级树形待办（带自动排期与月历联动）**或**系列知识笔记**。
2. **📤 周报/月报/年报自动萃取（电子便签 -> AI）**：从本地便签安全提取已完成成果、进行中任务与备忘灵感，自动聚类生成结构化周报、月报、年报与工作复盘。

---

## 🌟 核心特性

- **跨 Agent 标准协议**：严格遵循 [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) 规范（JSON-RPC 2.0 stdio），对所有主流 AI Agent 一视同仁，无特定平台绑定。
- **便携与零绝对路径**：支持安装为全局命令 `desktopnotes-mcp` 或使用相对路径/`${workspaceFolder}` 工作区变量，跨设备拉取即用。
- **零第三方运行时依赖**：基于 Python 3.10+ 标准库构建（内置 `sqlite3`, `json`, `datetime`），无需安装额外依赖库。
- **三种智能导入形态**：
  - `task_tree`（多级待办便签树）：按「总计划 -> 章节 -> 小节」生成多级树形待办，支持自动计算每章起止日期并同步至电子便签「任务月历」视图。
  - `note_series`（系列独立笔记）：按章节拆分为多张独立的长文本笔记便签，小节与要点自动排版为 Markdown 正文。
  - `note_single`（单篇完整大纲笔记）：将整份目录或计划合并保存为一张完整的 Markdown 笔记。
- **并发事务安全**：读取采用 `?mode=ro` 只读连接，写入遵循 SQLite WAL 事务与触发器校验，桌面端运行期间可安全并发读写，UI 自动刷新同步。

---

## 📦 快速安装 (二选一)

在代码仓库根目录打开终端：

### 选项 A：注册为本地系统命令（推荐，配置零路径）
```bash
pip install -e ./integrations/mcp
```
> 安装后系统即拥有 `desktopnotes-mcp` 命令，在任何 Agent 的配置文件中只需填写 `"command": "desktopnotes-mcp"`，无需任何路径。

### 选项 B：免安装直接调用相对路径脚本
无需 pip 安装，直接通过 Python 调用仓库内的相对路径脚本：
```bash
python ./integrations/mcp/desktopnotes_mcp.py
```

---

## 🔌 各 Agent 平台配置指南 (一视同仁)

所有支持 MCP 的平台均采用一致的服务配置方式。

### 1. OpenCode

在 OpenCode 配置文件（`opencode.json` 或 `~/.config/opencode/opencode.json`）中添加：

```json
{
  "mcpServers": {
    "desktopnotes": {
      "command": "desktopnotes-mcp"
    }
  }
}
```
*(若未执行 pip 安装，可将配置写为 `"command": "python", "args": ["${workspaceFolder}/integrations/mcp/desktopnotes_mcp.py"]`)*

**常用对话示例**：
- **导入学习计划**：`@desktopnotes 帮我生成一份《深入理解计算机系统》的章节学习目录，按每章 4 天制定学习计划，并作为多级待办导入到我的电子便签。`
- **生成工作汇报**：`@desktopnotes 读取本周已完成的任务与进行中待办，生成一份研发交付周报。`

---

### 2. OpenAI Codex / GitHub Copilot

在 Codex / Copilot 的 MCP 配置文件（如 `.codex/config.json` 或客户端 MCP 设置）中添加：

```json
{
  "mcpServers": {
    "desktopnotes": {
      "command": "desktopnotes-mcp"
    }
  }
}
```
*(若未执行 pip 安装，可改用 `"command": "python", "args": ["./integrations/mcp/desktopnotes_mcp.py"]`)*

**常用对话示例**：
- **导入为系列笔记**：`帮我梳理 Rust 核心语法各章节的学习目录和重点知识大纲，调用 desktopnotes_import_plan 以 note_series 模式导入我的电子便签作为系列笔记。`
- **生成月度总结**：`调用 desktopnotes 工具，汇总本月完成的事项并按模块分类输出月报。`

---

### 3. DSH (DeepSeek Harness)

在 DSH 的插件/扩展配置文件（如 `dsh.config.json`）中添加：

```json
{
  "plugins": {
    "desktopnotes": {
      "type": "mcp",
      "command": "desktopnotes-mcp"
    }
  }
}
```
*(若未执行 pip 安装，可改用 `"command": "python", "args": ["${workspaceFolder}/integrations/mcp/desktopnotes_mcp.py"]`)*

**常用对话示例**：
- **Markdown 目录一键导入**：`请设计一份为期 2 周的 Go 并发编程章节学习计划，并调用 desktopnotes_import_markdown 直接导入我的电子便签。`
- **生成年度总结**：`利用 desktopnotes 插件梳理本年度全部便签事项，输出年度技术复盘。`

---

### 4. Google Antigravity

在 Antigravity 的 MCP 配置文件（`~/.gemini/config/mcp_config.json`）中添加：

```json
{
  "mcpServers": {
    "desktopnotes": {
      "command": "desktopnotes-mcp"
    }
  }
}
```
*(可选：将 `./integrations/antigravity/skills/desktopnotes-reporter/` 复制到 skills 目录即可启用 `/desktopnotes-reporter` 快捷技能引导)*

**常用对话示例**：
- **导入章节计划**：`帮我制定一份《算法导论》核心章节学习计划，从明天开始每章学习 3 天，导入到我的电子便签。`
- **生成个人周报**：`帮我根据本周便签生成周报，汇报人填【姓名】。`

---

### 5. 其他 MCP 客户端 (Cursor / Claude Code / Windsurf)

在对应客户端的 MCP 配置文件（如 `.cursor/mcp.json` 或 `claude_desktop_config.json`）中添加：

```json
{
  "mcpServers": {
    "desktopnotes": {
      "command": "desktopnotes-mcp"
    }
  }
}
```

---

## 🛠️ MCP 暴露的核心工具清单 (8 大工具)

| 方向 | 工具名称 | 功能描述 | 核心参数 |
| :---: | :--- | :--- | :--- |
| **📥 导入** | `desktopnotes_import_plan` | **批量导入结构化章节学习计划/多级项目拆解/系列笔记**（支持自动按天排期并同步日历） | `items` (支持多级 `children`), `import_as` (`task_tree`/`note_series`/`note_single`), `root_title`, `auto_schedule_start_date`, `days_per_item` |
| **📥 导入** | `desktopnotes_import_markdown` | **将 Markdown 目录大纲直接解析并导入电子便签**（自动识别 `#` 标题层级与 `-` 列表层级） | `markdown_text`, `import_as` (`task_tree`/`note_series`/`note_single`), `auto_schedule_start_date`, `days_per_item` |
| **📥 导入** | `desktopnotes_create_record` | 创建单条待办任务或纯文本笔记便签 | `title`, `body`, `kind` (`task`/`note`), `priority` (`0-3`), `start_date`, `due_date`, `parent_id` |
| **🗑️ 删除** | `desktopnotes_delete_records` | **删除指定的待办任务（自动级联子任务）或笔记便签**（默认移入回收站，支持彻底删除与清空回收站） | `record_ids`, `exact_title`, `search_keyword`, `kind` (`task`/`note`/`all`), `permanent` (`false`/`true`), `empty_trash` |
| **📤 导出** | `desktopnotes_get_summary` | 获取便签总体统计指标（完成数、进行中、优先级分布） | `period` (`today`/`this_week`/`this_month`/`this_year`/`custom`) |
| **📤 导出** | `desktopnotes_list_tasks` | 查询任务或笔记清单（支持父子多级树形还原与关键词检索） | `kind` (`task`/`note`/`all`), `status`, `priority`, `search_keyword`, `include_tree` |
| **📤 导出** | `desktopnotes_get_report_data` | 提取专为周报/月报/年报聚类的结构化上下文数据 | `report_type` (`weekly`/`monthly`/`yearly`), `start_date`, `end_date` |
| **📤 导出** | `desktopnotes_generate_report` | 直接生成排版精美的 Markdown 周报/月报/年报草稿 | `report_type`, `template` (`standard`/`okr`/`engineering`/`executive`), `author` |

---

## 📖 学习目录与章节计划导入模式详解

当你在任意 Agent 中生成了一份学习目录或计划时，可以通过指定导入模式决定它在电子便签中的呈现形态：

| 导入模式 (`import_as`) | 适用场景 | 在电子便签客户端中的表现效果 |
| :--- | :--- | :--- |
| **`task_tree`** *(默认推荐)* | **可追踪进度的章节学习计划、课程大纲、项目拆解** | 在「**待办**」视图下生成「课程总任务 ➔ 第 N 章 ➔ N.M 小节」的**多级可折叠任务树**；支持开启自动排期（如每章 3 天），各章节截止日会**自动显示在「任务月历」视图**中并带有 🌱 标记。 |
| **`note_series`** | **分章节的知识卡片、读书笔记目录、专题复习提纲** | 在「**笔记**」视图下为每一章生成一张**独立的笔记便签**（如 `【《XXX》学习计划】第一章：...`），该章下的小节与要求会自动排版为笔记内部的 Markdown 清单。 |
| **`note_single`** | **单篇完整学习路线图、汇总备忘录** | 在「**笔记**」视图下生成**单张完整的 Markdown 汇总笔记**，包含所有章节层级与学习说明。 |

---

## 💻 独立命令行模式 (CLI)

不依赖任何 AI 客户端，也可以直接在终端使用 `desktopnotes-mcp` 完成导入与报告导出：

```bash
# 1. 将 Markdown 学习目录导入为多级待办任务树，并从 2026-11-11 起按每章 3 天自动排期
desktopnotes-mcp --import-md study_plan.md --import-as task_tree --start-date 2026-11-11 --days-per-item 3

# 2. 将 Markdown 目录按章节拆分为系列独立笔记导入
desktopnotes-mcp --import-md study_plan.md --import-as note_series

# 3. 将 JSON 结构化计划文件批量导入电子便签
desktopnotes-mcp --import-json plan.json

# 4. 终端直接生成本周周报
desktopnotes-mcp --report weekly --author 徐礼佳

# 5. 导出 OKR 风格月报 / 研发风格年报
desktopnotes-mcp --report monthly --template okr --output 11月月报.md
desktopnotes-mcp --report yearly --template engineering --output 2026年度总结.md

# 6. 删除指定标题的任务或笔记（默认安全移入回收站，加 --permanent 则彻底删除）
desktopnotes-mcp --delete-title "《深入理解计算机系统》章节学习计划" --kind task
desktopnotes-mcp --delete-keyword "测试" --kind note --permanent

# 7. 清空电子便签回收站
desktopnotes-mcp --empty-trash
```
*(若未执行 pip 安装，将 `desktopnotes-mcp` 替换为 `python ./integrations/mcp/desktopnotes_mcp.py` 即可)*
