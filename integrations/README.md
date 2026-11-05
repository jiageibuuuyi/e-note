# 电子便签 (DesktopNotes) · AI Agent 跨平台接入插件套件

> **支持环境**：Google Antigravity、OpenCode、OpenAI Codex / Copilot、DSH (DeepSeek Harness)  
> **底层协议**：Model Context Protocol (MCP) 标准 JSON-RPC 2.0 + Agent Skill  
> **数据源**：`%LOCALAPPDATA%\DesktopNotes\data\app.db`（SQLite WAL 事务安全 / 只读连接无冲突）

---

## 🌟 整体架构与设计思路

由于不同 AI 平台（Codex、DSH、OpenCode、Antigravity）的上层交互界面各异，但它们**全部统一兼容 Model Context Protocol (MCP) 行业标准协议**：

```
                    ┌──────────────────────────────┐
                    │ 电子便签 (DesktopNotes) SQLite │
                    │   %LOCALAPPDATA%\...\app.db  │
                    └──────────────┬───────────────┘
                                   │ (只读安全连接 / 并发事务)
                    ┌──────────────▼───────────────┐
                    │  DesktopNotes MCP 服务引擎    │
                    │  (integrations/mcp/...)      │
                    └──────────────┬───────────────┘
                                   │ (标准 JSON-RPC 2.0 stdio)
        ┌──────────────────┬───────┴──────────┬──────────────────┐
        ▼                  ▼                  ▼                  ▼
┌───────────────┐  ┌───────────────┐  ┌───────────────┐  ┌───────────────┐
│ Antigravity   │  │   OpenCode    │  │ OpenAI Codex  │  │      DSH      │
│ (Skill + MCP) │  │  (MCP Server) │  │  (MCP Client) │  │ (MCP Plugin)  │
└───────────────┘  └───────────────┘  └───────────────┘  └───────────────┘
        │                  │                  │                  │
        └──────────────────┼──────────────────┴──────────────────┘
                           ▼
            【自动产出高品质 周报 / 月报 / 年报 / 待办复盘】
```

1. **MCP 跨平台服务核心**：`integrations/mcp/desktopnotes_mcp.py`
   - **零第三方依赖**：纯 Python 标准库编写（内置 `sqlite3`, `json`, `datetime`），免安装任何额外 pip 包，Windows 开箱即用。
   - **双模运行**：既可作为 MCP stdio 后台服务驱动 AI Agent，又可作为独立 CLI 命令行直接打印或导出报告。
   - **安全机制**：读取采用 `?mode=ro` 只读连接，即使电子便签桌面客户端正在高频读写，也绝对不会发生文件锁冲突或破坏数据库。
2. **Antigravity 专属 Skill**：`integrations/antigravity/skills/desktopnotes-reporter/SKILL.md`
   - 为 Antigravity 注入专属自然语言理解，输入“帮我根据本周便签生成周报”即可自动调用工具并完成职场/研发维度的价值提炼。

---

## 🚀 各平台接入配置指南

### 1. Google Antigravity 接入（已完成全局配置）

Antigravity 原生支持 MCP 与 Skill 体系：

1. **MCP 服务器配置**（已写入全局 `~/.gemini/config/mcp_config.json`）：
   ```json
   {
     "mcpServers": {
       "desktopnotes": {
         "command": "python",
         "args": [
           "d:/个人/GPT-Project/桌面笔记/integrations/mcp/desktopnotes_mcp.py"
         ]
       }
     }
   }
   ```
2. **专属 Skill**（已安装到全局 `~/.gemini/config/skills/desktopnotes-reporter/SKILL.md`）：
   - 在 Antigravity 对话中直接说：
     - *“帮我根据便签生成本周周报”*
     - *“按 OKR 格式总结我上个月在便签里记录的产出”*
     - *“梳理年度任务生成年报复盘”*

---

### 2. OpenCode 接入

在 OpenCode 的配置文件（项目根目录 `opencode.json` 或 `~/.config/opencode/opencode.json`）中添加 MCP 配置：

```json
{
  "mcpServers": {
    "desktopnotes": {
      "command": "python",
      "args": [
        "d:/个人/GPT-Project/桌面笔记/integrations/mcp/desktopnotes_mcp.py"
      ]
    }
  }
}
```

**使用方式**：
在 OpenCode 对话框中键入：
> `@desktopnotes 请读取本周完成的所有任务和进行中待办，为我生成一份研发交付周报。`

---

### 3. OpenAI Codex / Copilot CLI / Claude Desktop / Cursor

在对应的 MCP 客户端配置文件中（例如 Cursor `~/.cursor/mcp.json` 或 Claude Desktop `claude_desktop_config.json`）：

```json
{
  "mcpServers": {
    "desktopnotes": {
      "command": "python",
      "args": [
        "d:/个人/GPT-Project/桌面笔记/integrations/mcp/desktopnotes_mcp.py"
      ]
    }
  }
}
```

**使用方式**：
> `使用 desktopnotes 插件工具，帮我统计 11 月份的任务结项情况并输出月度总结。`

---

### 4. DSH (DeepSeek Harness) 接入

在 DSH 的插件/扩展配置文件（如 `dsh.config.json` 或 `plugins.json`）中接入 MCP 服务：

```json
{
  "plugins": {
    "desktopnotes": {
      "type": "mcp",
      "command": "python",
      "args": [
        "d:/个人/GPT-Project/桌面笔记/integrations/mcp/desktopnotes_mcp.py"
      ]
    }
  }
}
```

---

## 🛠️ 暴露给 AI 的核心 MCP 工具清单

| 工具名称 | 作用说明 | 关键参数 |
| :--- | :--- | :--- |
| `desktopnotes_get_summary` | 获取便签总体指标（总任务数、已完成、进行中、优先级分布等） | `period` (`this_week`, `this_month`, `this_year`, `custom`) |
| `desktopnotes_list_tasks` | 检索任务清单（支持父子多级树形、状态、优先级过滤） | `status`, `priority`, `search_keyword`, `include_tree` |
| `desktopnotes_get_report_data` | 专门为周报/月报/年报准备的结构化上下文数据 | `report_type` (`weekly`, `monthly`, `yearly`) |
| `desktopnotes_generate_report` | 直接根据便签数据生成排版好的 Markdown 报告草稿 | `report_type`, `template` (`standard`, `okr`, `engineering`, `executive`) |
| `desktopnotes_create_record` | 允许 AI 将生成好的周报或下周规划反向存入电子便签 | `title`, `body`, `kind` (`task`/`note`), `priority` |

---

## 💻 本地无 Agent 时的直接命令行用法 (CLI)

即使不启动任何 AI 工具，你也可以直接在 Windows Terminal / PowerShell 中运行此脚本生成汇报：

```powershell
# 1. 快速查看本周效能指标看板
python integrations/mcp/desktopnotes_mcp.py --summary

# 2. 生成本周周报并打印到终端
python integrations/mcp/desktopnotes_mcp.py --report weekly

# 3. 生成本月月报并以 OKR 格式保存到文件
python integrations/mcp/desktopnotes_mcp.py --report monthly --template okr --output 11月工作月报.md

# 4. 生成年度工作复盘总结
python integrations/mcp/desktopnotes_mcp.py --report yearly --template engineering --output 2026年度总结.md

# 5. 查询当前所有进行中的待办任务
python integrations/mcp/desktopnotes_mcp.py --list-tasks --status active
```
