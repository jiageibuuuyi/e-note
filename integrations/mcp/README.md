# DesktopNotes MCP Server

Model Context Protocol (MCP) server for DesktopNotes (电子便签), supporting bi-directional study/task plan import and weekly/monthly/annual report generation across OpenCode, OpenAI Codex, DSH (DeepSeek Harness), Google Antigravity, Cursor, and Claude Code.

## Installation

```bash
pip install -e .
```

## CLI Usage

```bash
# Export / Report
desktopnotes-mcp --summary
desktopnotes-mcp --report weekly
desktopnotes-mcp --report monthly
desktopnotes-mcp --report yearly

# Import Study Plans / Notes
desktopnotes-mcp --import-md study_plan.md --import-as task_tree --start-date 2026-11-11 --days-per-item 3
desktopnotes-mcp --import-md study_plan.md --import-as note_series
desktopnotes-mcp --import-json plan.json
```

## MCP Protocol Usage

Start stdio server:
```bash
desktopnotes-mcp
```
or
```bash
python desktopnotes_mcp.py
```
