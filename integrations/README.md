# 电子便签 (DesktopNotes) · AI Agent 跨平台 MCP 插件

面向多智能体与 AI 编程助手的开放接口扩展包，支持一键接入 **OpenCode**、**OpenAI Codex**、**DSH (DeepSeek Harness)**、**Google Antigravity** 以及各类标准 MCP 客户端（Cursor、Claude Code 等），实现从本地便签提取工作数据并自动化生成周报、月报、年报与项目复盘。

---

## 🌟 核心特性

- **跨 Agent 标准协议**：严格遵循 [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) 规范（JSON-RPC 2.0 stdio），不绑定任何单一厂商平台。
- **便携与可移植性**：无任何硬编码绝对路径，支持 `${workspaceFolder}` 相对工作区变量或安装为全局命令 `desktopnotes-mcp`。
- **零外部运行时依赖**：基于 Python 3.10+ 标准库构建（内置 `sqlite3`, `json`, `datetime`），免安装任何第三方重量级依赖。
- **数据并发安全**：采用 `?mode=ro`（SQLite 只读事务 URI）读取 `%LOCALAPPDATA%\DesktopNotes\data\app.db`，与运行中的桌面客户端完全隔离，无锁冲突风险。
- **多模驱动**：既可作为 MCP 协议后台服务为 AI Agent 提供感知能力，亦可作为 CLI 命令行工具在终端中一键直出 Markdown 报告。

---

## 📦 快速安装 (二选一)

在代码仓库根目录打开终端：

### 选项 A：注册为本地全局命令（推荐，配置最简洁）
```bash
pip install -e ./integrations/mcp
```
> 安装后系统将拥有 `desktopnotes-mcp` 可执行命令，所有 Agent 配置文件中只需填一行 `"command": "desktopnotes-mcp"`，无需关心文件路径。

### 选项 B：免安装直接运行脚本
无需 pip 安装，直接通过 Python 解释器调用工作区中的相对路径脚本：
```bash
python ./integrations/mcp/desktopnotes_mcp.py
```

---

## 🔌 各 Agent 平台配置指南 (一视同仁)

各平台均采用标准 MCP 配置格式。你可以根据是否执行了 `pip install -e` 选择对应写法：

### 1. OpenCode

在 OpenCode 配置文件中（`opencode.json` 或 `~/.config/opencode/opencode.json`）：

```json
{
  "mcpServers": {
    "desktopnotes": {
      "command": "desktopnotes-mcp"
    }
  }
}
```
*(若未 pip 安装，则改用 `"command": "python", "args": ["${workspaceFolder}/integrations/mcp/desktopnotes_mcp.py"]`)*

**常用提示词**：
> `@desktopnotes 读取本周已完成的任务与进行中待办，帮我输出一份研发交付周报。`

---

### 2. OpenAI Codex / GitHub Copilot

在 Codex / Copilot 的 MCP 配置文件中：

```json
{
  "mcpServers": {
    "desktopnotes": {
      "command": "desktopnotes-mcp"
    }
  }
}
```
*(若未 pip 安装，则改用 `"command": "python", "args": ["./integrations/mcp/desktopnotes_mcp.py"]`)*

**常用提示词**：
> `调用 desktopnotes 工具，汇总本月便签中关闭的需求和缺陷，按模块分类生成月报。`

---

### 3. DSH (DeepSeek Harness)

在 DSH 的插件/扩展配置文件（如 `dsh.config.json`）中添加 MCP 配置：

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
*(若未 pip 安装，则改用 `"command": "python", "args": ["${workspaceFolder}/integrations/mcp/desktopnotes_mcp.py"]`)*

**常用提示词**：
> `利用 desktopnotes 插件梳理 2026 年度的全部便签事项，生成一份技术攻坚年度总结。`

---

### 4. Google Antigravity

在 Antigravity 配置文件（`~/.gemini/config/mcp_config.json`）中添加：

```json
{
  "mcpServers": {
    "desktopnotes": {
      "command": "desktopnotes-mcp"
    }
  }
}
```
*(同时支持将 `./integrations/antigravity/skills/desktopnotes-reporter/` 放置于 skills 目录作为辅助自然语言触发技能)*

**常用提示词**：
> `帮我根据本周便签生成周报，汇报人填【姓名】。`

---

### 5. 其他 MCP 客户端 (Cursor / Claude Code / Windsurf)

在对应客户端的 `mcp.json`（如 `.cursor/mcp.json` 或 `claude_desktop_config.json`）中：

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

## 🛠️ MCP 核心工具能力一览

连接成功后，Agent 会自动获得以下 5 个原子工具：

| 工具名称 | 功能描述 | 核心输入参数 |
| :--- | :--- | :--- |
| `desktopnotes_get_summary` | 获取便签总体指标（总数、完成数、进行中、优先级分布） | `period`: `today`/`this_week`/`last_week`/`this_month`/`this_year`/`custom` |
| `desktopnotes_list_tasks` | 检索任务清单（支持父子多级树形还原、状态、优先级过滤） | `status` (`all`/`active`/`completed`), `priority`, `search_keyword` |
| `desktopnotes_get_report_data` | 专门为写汇报准备的结构化上下文数据（按主线/项目智能聚合） | `report_type`: `weekly`/`monthly`/`yearly` |
| `desktopnotes_generate_report` | 直接产出排版精美的 Markdown 报告草稿 | `report_type`, `template`: `standard`/`okr`/`engineering`/`executive`, `author` |
| `desktopnotes_create_record` | 允许 Agent 将生成的报告或下周期计划反向写入便签归档 | `title`, `body`, `kind` (`task`/`note`), `priority` |

---

## 💻 独立命令行模式 (CLI)

不依赖任何 Agent，亦可直接在终端中运行：

```bash
# 查看统计概览
desktopnotes-mcp --summary

# 终端输出本周周报
desktopnotes-mcp --report weekly

# 导出月报为 Markdown 文件 (OKR 风格)
desktopnotes-mcp --report monthly --template okr --output 11月月报.md

# 导出年度总结复盘 (技术研发工程风格)
desktopnotes-mcp --report yearly --template engineering --output 2026年终总结.md

# 列出当前进行中的待办任务
desktopnotes-mcp --list-tasks --status active
```
*(若未执行 pip 安装，将命令前缀替换为 `python ./integrations/mcp/desktopnotes_mcp.py` 即可)*
