#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
DesktopNotes MCP Server & Report Generator
Connects '电子便签 (DesktopNotes)' SQLite database to AI Coding Agents:
  - Google Antigravity
  - OpenCode
  - OpenAI Codex / GitHub Copilot
  - DSH (DeepSeek Harness)

Supports:
  1. MCP (Model Context Protocol) JSON-RPC 2.0 stdio transport
  2. Standalone CLI report generation (Weekly, Monthly, Yearly)
"""

import sys
import os
import json
import sqlite3
import datetime
import uuid
import argparse
from typing import Dict, Any, List, Optional, Tuple

if sys.stdout and hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
if sys.stderr and hasattr(sys.stderr, "reconfigure"):
    try:
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass
if sys.stdin and hasattr(sys.stdin, "reconfigure"):
    try:
        sys.stdin.reconfigure(encoding="utf-8")
    except Exception:
        pass

PRIORITY_MAP = {
    0: "P0 (紧急最高)",
    1: "P1 (高)",
    2: "P2 (普通)",
    3: "P3 (低)"
}

STATUS_MAP = {
    0: "进行中 (Active)",
    1: "已完成 (Closed)"
}

KIND_MAP = {
    0: "任务 (Task)",
    1: "便签 (Note)"
}

def log(msg: str) -> None:
    """Log to stderr to avoid breaking stdio JSON-RPC stream."""
    sys.stderr.write(f"[DesktopNotes-MCP] {msg}\n")
    sys.stderr.flush()

def get_db_path() -> str:
    """Resolve DesktopNotes SQLite database path."""
    env_path = os.environ.get("DESKTOP_NOTES_DB")
    if env_path and os.path.isfile(env_path):
        return env_path
    
    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        default_path = os.path.join(local_app_data, "DesktopNotes", "data", "app.db")
        if os.path.isfile(default_path):
            return default_path
            
    # Fallback to local project directory
    candidates = [
        os.path.join(os.path.dirname(__file__), "..", "..", "publish", "data", "app.db"),
        os.path.join(os.getcwd(), "publish", "data", "app.db"),
        os.path.join(os.getcwd(), "data", "app.db")
    ]
    for c in candidates:
        if os.path.isfile(c):
            return os.path.abspath(c)
            
    # Default path even if it doesn't exist yet
    if local_app_data:
        return os.path.join(local_app_data, "DesktopNotes", "data", "app.db")
    return "app.db"

def get_readonly_connection(db_path: Optional[str] = None) -> sqlite3.Connection:
    """Create read-only connection to avoid SQLite database locks."""
    path = db_path or get_db_path()
    if not os.path.isfile(path):
        raise FileNotFoundError(f"DesktopNotes 数据库不存在: {path}")
    conn = sqlite3.connect(f"file:{os.path.abspath(path)}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    return conn

def get_readwrite_connection(db_path: Optional[str] = None) -> sqlite3.Connection:
    """Create read-write connection for inserting records."""
    path = db_path or get_db_path()
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    conn = sqlite3.connect(path, timeout=5000)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA busy_timeout = 5000;")
    conn.execute("PRAGMA foreign_keys = ON;")
    return conn

def ms_to_datetime_str(ms: Optional[int]) -> Optional[str]:
    """Convert UTC epoch milliseconds to local formatted string."""
    if not ms:
        return None
    try:
        dt = datetime.datetime.fromtimestamp(ms / 1000.0, tz=datetime.timezone.utc).astimezone()
        return dt.strftime("%Y-%m-%d %H:%M:%S")
    except Exception:
        return None

def resolve_date_range(period: str, start_date: Optional[str] = None, end_date: Optional[str] = None) -> Tuple[datetime.date, datetime.date, str]:
    """Resolve start and end dates based on period name."""
    now = datetime.datetime.now()
    today = now.date()
    
    if period == "today":
        return today, today, f"{today}"
    elif period == "this_week":
        monday = today - datetime.timedelta(days=today.weekday())
        sunday = monday + datetime.timedelta(days=6)
        return monday, sunday, f"本周 ({monday} 至 {sunday})"
    elif period == "last_week":
        monday = today - datetime.timedelta(days=today.weekday() + 7)
        sunday = monday + datetime.timedelta(days=6)
        return monday, sunday, f"上周 ({monday} 至 {sunday})"
    elif period == "this_month":
        start = today.replace(day=1)
        next_month = (start + datetime.timedelta(days=32)).replace(day=1)
        end = next_month - datetime.timedelta(days=1)
        return start, end, f"本月 ({start.strftime('%Y-%m')})"
    elif period == "last_month":
        first_this_month = today.replace(day=1)
        end = first_this_month - datetime.timedelta(days=1)
        start = end.replace(day=1)
        return start, end, f"上月 ({start.strftime('%Y-%m')})"
    elif period == "this_year":
        start = today.replace(month=1, day=1)
        end = today.replace(month=12, day=31)
        return start, end, f"今年 ({start.year}年度)"
    elif period == "custom" and start_date and end_date:
        d1 = datetime.datetime.strptime(start_date, "%Y-%m-%d").date()
        d2 = datetime.datetime.strptime(end_date, "%Y-%m-%d").date()
        return d1, d2, f"自定义时段 ({d1} 至 {d2})"
    elif period == "all":
        # All time
        return datetime.date(2000, 1, 1), datetime.date(2099, 12, 31), "全部时段"
    else:
        # Default to this week
        monday = today - datetime.timedelta(days=today.weekday())
        sunday = monday + datetime.timedelta(days=6)
        return monday, sunday, f"本周 ({monday} 至 {sunday})"

def date_range_to_ms(d_start: datetime.date, d_end: datetime.date) -> Tuple[int, int]:
    """Convert date range to start and end millisecond timestamps."""
    tz = datetime.datetime.now().astimezone().tzinfo
    dt_start = datetime.datetime.combine(d_start, datetime.time.min).replace(tzinfo=tz)
    dt_end = datetime.datetime.combine(d_end, datetime.time.max).replace(tzinfo=tz)
    start_ms = int(dt_start.astimezone(datetime.timezone.utc).timestamp() * 1000)
    end_ms = int(dt_end.astimezone(datetime.timezone.utc).timestamp() * 1000)
    return start_ms, end_ms

class DesktopNotesDB:
    """Encapsulates all database operations."""

    def __init__(self, db_path: Optional[str] = None):
        self.db_path = db_path or get_db_path()

    def get_summary(self, period: str = "this_week", start_date: Optional[str] = None, end_date: Optional[str] = None) -> Dict[str, Any]:
        """Get high level statistics for tasks and notes."""
        d_start, d_end, period_label = resolve_date_range(period, start_date, end_date)
        start_ms, end_ms = date_range_to_ms(d_start, d_end)
        
        with get_readonly_connection(self.db_path) as conn:
            c = conn.cursor()
            
            # Completed in period
            c.execute("""
                SELECT count(*) FROM records 
                WHERE deleted_utc_ms IS NULL AND kind = 0 AND status = 1
                  AND closed_utc_ms >= ? AND closed_utc_ms <= ?
            """, (start_ms, end_ms))
            completed_in_period = c.fetchone()[0]
            
            # Created in period
            c.execute("""
                SELECT count(*) FROM records 
                WHERE deleted_utc_ms IS NULL AND kind = 0
                  AND created_utc_ms >= ? AND created_utc_ms <= ?
            """, (start_ms, end_ms))
            created_in_period = c.fetchone()[0]

            # Current active tasks
            c.execute("SELECT count(*) FROM records WHERE deleted_utc_ms IS NULL AND kind = 0 AND status = 0")
            current_active = c.fetchone()[0]

            # Total notes
            c.execute("SELECT count(*) FROM records WHERE deleted_utc_ms IS NULL AND kind = 1")
            total_notes = c.fetchone()[0]

            # Priority breakdown for completed tasks in period
            c.execute("""
                SELECT priority, count(*) FROM records
                WHERE deleted_utc_ms IS NULL AND kind = 0 AND status = 1
                  AND closed_utc_ms >= ? AND closed_utc_ms <= ?
                GROUP BY priority
            """, (start_ms, end_ms))
            priority_counts = {PRIORITY_MAP.get(row[0], f"P{row[0]}"): row[1] for row in c.fetchall()}

            return {
                "period": period_label,
                "start_date": str(d_start),
                "end_date": str(d_end),
                "tasks_completed_in_period": completed_in_period,
                "tasks_created_in_period": created_in_period,
                "current_active_tasks": current_active,
                "total_notes": total_notes,
                "completed_priority_distribution": priority_counts,
                "database_path": self.db_path
            }

    def query_records(
        self,
        kind: str = "all",       # "all", "task", "note"
        status: str = "all",     # "all", "active", "completed"
        period: str = "all",     # "all", "today", "this_week", "last_week", "this_month", "last_month", "this_year", "custom"
        start_date: Optional[str] = None,
        end_date: Optional[str] = None,
        priority: Optional[int] = None,
        search_keyword: Optional[str] = None,
        include_tree: bool = True
    ) -> List[Dict[str, Any]]:
        """Query tasks and notes with extensive filtering."""
        with get_readonly_connection(self.db_path) as conn:
            c = conn.cursor()
            query = """
                SELECT r.id, r.parent_id, r.kind, r.title, r.body, r.status, r.priority,
                       r.created_utc_ms, r.closed_utc_ms, r.due_date, p.title AS parent_title
                FROM records r
                LEFT JOIN records p ON r.parent_id = p.id
                WHERE r.deleted_utc_ms IS NULL
            """
            params: List[Any] = []

            if kind == "task":
                query += " AND r.kind = 0"
            elif kind == "note":
                query += " AND r.kind = 1"

            if status == "active":
                query += " AND r.status = 0"
            elif status == "completed":
                query += " AND r.status = 1"

            if priority is not None:
                query += " AND r.priority = ?"
                params.append(priority)

            if search_keyword:
                query += " AND (r.title LIKE ? OR r.body LIKE ?)"
                params.extend([f"%{search_keyword}%", f"%{search_keyword}%"])

            if period != "all":
                d_start, d_end, _ = resolve_date_range(period, start_date, end_date)
                start_ms, end_ms = date_range_to_ms(d_start, d_end)
                if status == "completed":
                    query += " AND r.closed_utc_ms >= ? AND r.closed_utc_ms <= ?"
                    params.extend([start_ms, end_ms])
                else:
                    query += " AND ((r.created_utc_ms >= ? AND r.created_utc_ms <= ?) OR (r.closed_utc_ms >= ? AND r.closed_utc_ms <= ?))"
                    params.extend([start_ms, end_ms, start_ms, end_ms])

            query += " ORDER BY r.status ASC, r.priority ASC, r.created_utc_ms DESC"
            c.execute(query, params)
            
            records = []
            for row in c.fetchall():
                records.append({
                    "id": row["id"],
                    "parent_id": row["parent_id"],
                    "parent_title": row["parent_title"],
                    "kind": KIND_MAP.get(row["kind"], "Task"),
                    "title": row["title"],
                    "body": row["body"] or "",
                    "status": STATUS_MAP.get(row["status"], "Active"),
                    "status_code": row["status"],
                    "priority": PRIORITY_MAP.get(row["priority"], f"P{row['priority']}"),
                    "priority_level": row["priority"],
                    "due_date": row["due_date"],
                    "created_at": ms_to_datetime_str(row["created_utc_ms"]),
                    "closed_at": ms_to_datetime_str(row["closed_utc_ms"])
                })

            if not include_tree:
                return records

            # Build tree structure if requested
            id_to_record = {r["id"]: dict(r, children=[]) for r in records}
            tree = []
            for r in id_to_record.values():
                pid = r.get("parent_id")
                if pid and pid in id_to_record:
                    id_to_record[pid]["children"].append(r)
                else:
                    tree.append(r)
            return tree

    def get_report_context(
        self,
        report_type: str = "weekly",
        start_date: Optional[str] = None,
        end_date: Optional[str] = None
    ) -> Dict[str, Any]:
        """Aggregate data structured for Weekly/Monthly/Annual Report generation."""
        period_map = {
            "weekly": "this_week",
            "monthly": "this_month",
            "yearly": "this_year"
        }
        period_key = period_map.get(report_type, "this_week")
        d_start, d_end, period_label = resolve_date_range(period_key, start_date, end_date)
        start_ms, end_ms = date_range_to_ms(d_start, d_end)

        with get_readonly_connection(self.db_path) as conn:
            c = conn.cursor()

            # Completed tasks in period
            c.execute("""
                SELECT r.id, r.parent_id, r.title, r.body, r.priority, r.created_utc_ms,
                       r.closed_utc_ms, r.due_date, p.title AS parent_title
                FROM records r
                LEFT JOIN records p ON r.parent_id = p.id
                WHERE r.deleted_utc_ms IS NULL AND r.kind = 0 AND r.status = 1
                  AND r.closed_utc_ms >= ? AND r.closed_utc_ms <= ?
                ORDER BY r.closed_utc_ms ASC
            """, (start_ms, end_ms))
            completed_rows = c.fetchall()

            completed_tasks = []
            for r in completed_rows:
                completed_tasks.append({
                    "id": r["id"],
                    "title": r["title"],
                    "body": r["body"] or "",
                    "parent_title": r["parent_title"],
                    "priority": PRIORITY_MAP.get(r["priority"], f"P{r['priority']}"),
                    "priority_level": r["priority"],
                    "closed_at": ms_to_datetime_str(r["closed_utc_ms"]),
                    "due_date": r["due_date"]
                })

            # Active / In-progress tasks
            c.execute("""
                SELECT r.id, r.parent_id, r.title, r.body, r.priority, r.created_utc_ms,
                       r.due_date, p.title AS parent_title
                FROM records r
                LEFT JOIN records p ON r.parent_id = p.id
                WHERE r.deleted_utc_ms IS NULL AND r.kind = 0 AND r.status = 0
                ORDER BY r.priority ASC, r.created_utc_ms ASC
            """)
            active_rows = c.fetchall()

            in_progress_tasks = []
            today_str = datetime.date.today().strftime("%Y-%m-%d")
            for r in active_rows:
                due = r["due_date"]
                is_overdue = (due is not None and due < today_str)
                in_progress_tasks.append({
                    "id": r["id"],
                    "title": r["title"],
                    "body": r["body"] or "",
                    "parent_title": r["parent_title"],
                    "priority": PRIORITY_MAP.get(r["priority"], f"P{r['priority']}"),
                    "priority_level": r["priority"],
                    "due_date": due,
                    "is_overdue": is_overdue,
                    "created_at": ms_to_datetime_str(r["created_utc_ms"])
                })

            # Notes / Memos in period
            c.execute("""
                SELECT title, body, created_utc_ms FROM records
                WHERE deleted_utc_ms IS NULL AND kind = 1
                  AND created_utc_ms >= ? AND created_utc_ms <= ?
                ORDER BY created_utc_ms DESC
            """, (start_ms, end_ms))
            notes = [
                {
                    "title": r["title"],
                    "body": r["body"] or "",
                    "created_at": ms_to_datetime_str(r["created_utc_ms"])
                }
                for r in c.fetchall()
            ]

            # Categorize completed tasks by Parent (Project/Theme)
            grouped_completed: Dict[str, List[Dict[str, Any]]] = {}
            for t in completed_tasks:
                group_name = t["parent_title"] if t["parent_title"] else "常规事项 / 独立任务"
                if group_name not in grouped_completed:
                    grouped_completed[group_name] = []
                grouped_completed[group_name].append(t)

            return {
                "report_type": report_type,
                "period_label": period_label,
                "start_date": str(d_start),
                "end_date": str(d_end),
                "metrics": {
                    "completed_count": len(completed_tasks),
                    "active_count": len(in_progress_tasks),
                    "notes_count": len(notes),
                    "overdue_active_count": sum(1 for t in in_progress_tasks if t.get("is_overdue"))
                },
                "completed_tasks": completed_tasks,
                "completed_by_group": grouped_completed,
                "in_progress_tasks": in_progress_tasks,
                "notes": notes
            }

    def generate_report_markdown(
        self,
        report_type: str = "weekly",
        template: str = "standard",
        start_date: Optional[str] = None,
        end_date: Optional[str] = None,
        author: str = "开发者"
    ) -> str:
        """Generate a complete, professionally formatted markdown report."""
        data = self.get_report_context(report_type, start_date, end_date)
        period = data["period_label"]
        metrics = data["metrics"]
        completed_grouped = data["completed_by_group"]
        in_progress = data["in_progress_tasks"]
        notes = data["notes"]

        report_title_map = {
            "weekly": f"工作周报 · {period}",
            "monthly": f"工作月报 · {period}",
            "yearly": f"年度工作复盘与总结 · {period}"
        }
        main_title = report_title_map.get(report_type, f"工作汇报 · {period}")

        lines = []
        lines.append(f"# {main_title}")
        lines.append(f"> **汇报人**：{author}  |  **统计周期**：{period}  |  **数据源**：电子便签 (DesktopNotes)\n")
        lines.append("---")
        lines.append("## 📊 一、效能与交付数据概览\n")
        lines.append(f"| 指标项 | 统计值 | 状态与说明 |")
        lines.append(f"| :--- | :--- | :--- |")
        lines.append(f"| **本期已结项交付** | **{metrics['completed_count']}** 项 | ✅ 均已完成验收结项 |")
        lines.append(f"| **进行中攻坚任务** | **{metrics['active_count']}** 项 | ⏳ 正在推进中 |")
        if metrics['overdue_active_count'] > 0:
            lines.append(f"| **逾期风险预警** | <font color='red'>**{metrics['overdue_active_count']}**</font> 项 | ⚠️ 需重点关注排期 |")
        else:
            lines.append(f"| **逾期风险预警** | 0 项 | 🟢 进度正常，无逾期风险 |")
        lines.append(f"| **随手记/备忘灵感** | **{metrics['notes_count']}** 条 | 💡 已归档沉淀 |\n")

        lines.append("---")
        lines.append("## 🏆 二、本期重点完成成果 (Highlights)\n")

        if not completed_grouped:
            lines.append("_本统计周期内暂无已完成归档的任务。_\n")
        else:
            for group_name, tasks in completed_grouped.items():
                lines.append(f"### 📌 模块/主线：{group_name}")
                for idx, t in enumerate(tasks, 1):
                    p_badge = f"`{t['priority']}`" if "P0" in t["priority"] or "P1" in t["priority"] else ""
                    finish_time = f" *(完成于 {t['closed_at']})*" if t["closed_at"] else ""
                    lines.append(f"{idx}. **{t['title']}** {p_badge}{finish_time}")
                    if t["body"]:
                        clean_body = t['body'].strip().replace("\n", "\n   > ")
                        lines.append(f"   > {clean_body}")
                lines.append("")

        lines.append("---")
        lines.append("## ⏳ 三、当前推进中任务与后续计划 (In Progress & Plan)\n")

        if not in_progress:
            lines.append("_当前暂无进行中的挂起任务，所有待办均已结项！_\n")
        else:
            for idx, t in enumerate(in_progress, 1):
                overdue_flag = " ⚠️**[已逾期]**" if t.get("is_overdue") else ""
                due_info = f" (截止: {t['due_date']})" if t.get("due_date") else ""
                parent_info = f"【{t['parent_title']}】" if t.get("parent_title") else ""
                lines.append(f"{idx}. **{parent_info}{t['title']}** `{t['priority']}`{due_info}{overdue_flag}")
                if t["body"]:
                    clean_body = t['body'].strip().replace("\n", "\n   > ")
                    lines.append(f"   > {clean_body}")
            lines.append("")

        if notes:
            lines.append("---")
            lines.append("## 💡 四、备忘灵感与随手记沉淀\n")
            for idx, n in enumerate(notes, 1):
                lines.append(f"{idx}. **{n['title']}** *({n['created_at']})*")
                if n["body"]:
                    lines.append(f"   > {n['body'].strip()}")
            lines.append("")

        lines.append("---")
        lines.append("## 🎯 五、复盘思考与下阶段展望\n")
        if template == "okr":
            lines.append("1. **目标对齐 (Objective)**：围绕桌面端高质量用户体验与智能提效持续迭代。")
            lines.append("2. **关键成果复盘 (Key Results)**：本周期内完成各项核心交互优化，任务闭环率良好。")
            lines.append("3. **攻坚阻碍与求助**：暂无重大阻塞，按既定节奏稳步推进。\n")
        elif template == "engineering":
            lines.append("1. **架构与代码质量**：模块解耦稳定，持续守护单测保障与防退化体系。")
            lines.append("2. **遗留技术债**：关注窗口层级响应与并发写入场景边界。")
            lines.append("3. **下阶段技术攻坚**：推进智能 Agent 集成与自动化提效工作流。\n")
        else:
            lines.append("1. **整体进度评估**：本期任务达成符合预期，核心主线交付节奏平稳。")
            lines.append("2. **持续改进项**：进一步细化任务拆解颗粒度，提高长周期任务的即时更新频率。")
            lines.append("3. **下阶段核心聚焦**：集中攻坚后续未完成关键事项，保障高质量按期闭环。\n")

        return "\n".join(lines)

    def create_record(
        self,
        title: str,
        body: str = "",
        kind: str = "task",
        priority: int = 2,
        due_date: Optional[str] = None,
        parent_id: Optional[str] = None
    ) -> str:
        """Create a new task or note directly in DesktopNotes."""
        record_id = str(uuid.uuid4())
        kind_int = 0 if kind == "task" else 1
        now_ms = int(datetime.datetime.now(datetime.timezone.utc).timestamp() * 1000)
        due_kind = 1 if due_date else 0

        with get_readwrite_connection(self.db_path) as conn:
            # Insert into records table
            conn.execute("""
                INSERT INTO records (
                    id, parent_id, kind, title, body, status,
                    publisher, assignee, priority, start_utc_ms,
                    due_kind, due_date, due_utc_ms, due_zone,
                    attachment_root_id, inherit_mask,
                    created_utc_ms, updated_utc_ms, version
                ) VALUES (
                    ?, ?, ?, ?, ?, 0,
                    'AI Agent', NULL, ?, NULL,
                    ?, ?, NULL, NULL,
                    NULL, 0,
                    ?, ?, 1
                )
            """, (
                record_id, parent_id, kind_int, title, body,
                priority, due_kind, due_date, now_ms, now_ms
            ))

            # Insert into effective_records table
            due_boundary = None
            if due_date:
                # end of due date in local time
                dt_due = datetime.datetime.strptime(due_date, "%Y-%m-%d")
                dt_due_end = datetime.datetime.combine(dt_due.date(), datetime.time.max)
                due_boundary = int(dt_due_end.astimezone(datetime.timezone.utc).timestamp() * 1000)

            conn.execute("""
                INSERT INTO effective_records (
                    record_id, publisher, assignee, priority, start_utc_ms,
                    due_kind, due_date, due_utc_ms, due_zone, attachment_root_id,
                    due_boundary_utc_ms, projection_zone, record_version
                ) VALUES (
                    ?, 'AI Agent', NULL, ?, NULL,
                    ?, ?, NULL, NULL, NULL,
                    ?, 'Local', 1
                )
            """, (
                record_id, priority, due_kind, due_date, due_boundary
            ))
            conn.commit()
            return record_id

# ---------------------------------------------------------------------------
# MCP Protocol Handlers (JSON-RPC 2.0 over stdio)
# ---------------------------------------------------------------------------

def mcp_get_tools_spec() -> List[Dict[str, Any]]:
    """Return MCP tools specification list."""
    return [
        {
            "name": "desktopnotes_get_summary",
            "description": "获取桌面便签(DesktopNotes)的任务与笔记宏观统计数据(已完成、进行中、优先级分布等)。",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "period": {
                        "type": "string",
                        "enum": ["today", "this_week", "last_week", "this_month", "last_month", "this_year", "all", "custom"],
                        "description": "统计周期，默认为 this_week (本周)"
                    },
                    "start_date": {
                        "type": "string",
                        "description": "自定义开始日期 YYYY-MM-DD (仅当 period 为 custom 时需要)"
                    },
                    "end_date": {
                        "type": "string",
                        "description": "自定义结束日期 YYYY-MM-DD (仅当 period 为 custom 时需要)"
                    }
                }
            }
        },
        {
            "name": "desktopnotes_list_tasks",
            "description": "查询桌面便签中的任务清单，支持按完成状态、时间周期、优先级、关键字过滤及层级树形展示。",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "status": {
                        "type": "string",
                        "enum": ["all", "active", "completed"],
                        "description": "状态过滤：all(全部), active(进行中), completed(已结项完成)"
                    },
                    "period": {
                        "type": "string",
                        "enum": ["all", "today", "this_week", "last_week", "this_month", "last_month", "this_year", "custom"],
                        "description": "时间跨度过滤，默认 all"
                    },
                    "priority": {
                        "type": "integer",
                        "enum": [0, 1, 2, 3],
                        "description": "优先级过滤：0(P0紧急), 1(P1高), 2(P2普通), 3(P3低)"
                    },
                    "search_keyword": {
                        "type": "string",
                        "description": "在任务标题和备忘详情中模糊检索的关键词"
                    },
                    "include_tree": {
                        "type": "boolean",
                        "description": "是否以父子任务树形结构返回，默认为 true"
                    }
                }
            }
        },
        {
            "name": "desktopnotes_get_report_data",
            "description": "提取专为生成周报、月报、年报而聚合并结构化的完整上下文数据(包含按模块/项目分组的完成项、进行中待办、备忘灵感与指标看板)。",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "report_type": {
                        "type": "string",
                        "enum": ["weekly", "monthly", "yearly"],
                        "description": "报告类型：weekly(周报), monthly(月报), yearly(年报)"
                    },
                    "start_date": {
                        "type": "string",
                        "description": "可选自定义开始日期 YYYY-MM-DD"
                    },
                    "end_date": {
                        "type": "string",
                        "description": "可选自定义结束日期 YYYY-MM-DD"
                    }
                },
                "required": ["report_type"]
            }
        },
        {
            "name": "desktopnotes_generate_report",
            "description": "直接根据便签真实数据生成格式化排版的 Markdown 汇报草稿(周报、月报、年报)，支持多种模板样式。",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "report_type": {
                        "type": "string",
                        "enum": ["weekly", "monthly", "yearly"],
                        "description": "报告类型：weekly(周报), monthly(月报), yearly(年报)"
                    },
                    "template": {
                        "type": "string",
                        "enum": ["standard", "okr", "engineering", "executive"],
                        "description": "模板风格：standard(通用职场), okr(结果复盘), engineering(研发工程师交付), executive(管理层极简)"
                    },
                    "start_date": {
                        "type": "string",
                        "description": "可选自定义开始日期 YYYY-MM-DD"
                    },
                    "end_date": {
                        "type": "string",
                        "description": "可选自定义结束日期 YYYY-MM-DD"
                    },
                    "author": {
                        "type": "string",
                        "description": "汇报人姓名，默认为'开发者'"
                    }
                },
                "required": ["report_type"]
            }
        },
        {
            "name": "desktopnotes_create_record",
            "description": "向桌面便签(DesktopNotes)反向写入一条新任务或便签(例如归档生成的周报，或创建下周计划)。",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "title": {
                        "type": "string",
                        "description": "便签或任务标题"
                    },
                    "body": {
                        "type": "string",
                        "description": "详细内容(支持 Markdown 长文本)"
                    },
                    "kind": {
                        "type": "string",
                        "enum": ["task", "note"],
                        "description": "类型：task(待办任务), note(纯文本便签/备忘)"
                    },
                    "priority": {
                        "type": "integer",
                        "enum": [0, 1, 2, 3],
                        "description": "优先级：0(P0紧急), 1(P1高), 2(P2普通), 3(P3低)，默认为 2"
                    },
                    "due_date": {
                        "type": "string",
                        "description": "截止日期 YYYY-MM-DD (可选)"
                    },
                    "parent_id": {
                        "type": "string",
                        "description": "父任务 ID (若创建子任务，可选)"
                    }
                },
                "required": ["title"]
            }
        }
    ]

def handle_mcp_call(db: DesktopNotesDB, tool_name: str, args: Dict[str, Any]) -> Any:
    """Execute tool and return JSON-serializable result."""
    if tool_name == "desktopnotes_get_summary":
        return db.get_summary(
            period=args.get("period", "this_week"),
            start_date=args.get("start_date"),
            end_date=args.get("end_date")
        )
    elif tool_name == "desktopnotes_list_tasks":
        return db.query_records(
            kind="task",
            status=args.get("status", "all"),
            period=args.get("period", "all"),
            start_date=args.get("start_date"),
            end_date=args.get("end_date"),
            priority=args.get("priority"),
            search_keyword=args.get("search_keyword"),
            include_tree=args.get("include_tree", True)
        )
    elif tool_name == "desktopnotes_get_report_data":
        return db.get_report_context(
            report_type=args.get("report_type", "weekly"),
            start_date=args.get("start_date"),
            end_date=args.get("end_date")
        )
    elif tool_name == "desktopnotes_generate_report":
        md = db.generate_report_markdown(
            report_type=args.get("report_type", "weekly"),
            template=args.get("template", "standard"),
            start_date=args.get("start_date"),
            end_date=args.get("end_date"),
            author=args.get("author", "开发者")
        )
        return {"report_markdown": md}
    elif tool_name == "desktopnotes_create_record":
        rid = db.create_record(
            title=args["title"],
            body=args.get("body", ""),
            kind=args.get("kind", "task"),
            priority=args.get("priority", 2),
            due_date=args.get("due_date"),
            parent_id=args.get("parent_id")
        )
        return {"status": "success", "id": rid, "message": f"成功写入桌面便签: {args['title']}"}
    else:
        raise ValueError(f"未知的工具名称: {tool_name}")

def run_mcp_server():
    """Run JSON-RPC 2.0 stdio server loop."""
    log("DesktopNotes MCP Server starting over stdio...")
    db = DesktopNotesDB()
    log(f"Connected to database: {db.db_path}")

    # Set up UTF-8 for stdio
    if sys.platform == "win32":
        import io
        sys.stdin = io.TextIOWrapper(sys.stdin.buffer, encoding="utf-8")
        sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")

    while True:
        try:
            line = sys.stdin.readline()
            if not line:
                break
            line = line.strip()
            if not line:
                continue

            try:
                request = json.loads(line)
            except Exception as e:
                log(f"Malformed JSON: {e}")
                continue

            req_id = request.get("id")
            method = request.get("method")
            params = request.get("params", {})

            # Notification check
            if method == "notifications/initialized":
                log("MCP Client initialized successfully.")
                continue

            # Standard methods
            if method == "initialize":
                response = {
                    "jsonrpc": "2.0",
                    "id": req_id,
                    "result": {
                        "protocolVersion": "2024-11-05",
                        "capabilities": {
                            "tools": {}
                        },
                        "serverInfo": {
                            "name": "desktopnotes-mcp",
                            "version": "1.0.0"
                        }
                    }
                }
            elif method == "tools/list":
                response = {
                    "jsonrpc": "2.0",
                    "id": req_id,
                    "result": {
                        "tools": mcp_get_tools_spec()
                    }
                }
            elif method == "tools/call":
                tool_name = params.get("name")
                tool_args = params.get("arguments", {})
                try:
                    result_data = handle_mcp_call(db, tool_name, tool_args)
                    formatted_text = json.dumps(result_data, ensure_ascii=False, indent=2)
                    response = {
                        "jsonrpc": "2.0",
                        "id": req_id,
                        "result": {
                            "content": [
                                {
                                    "type": "text",
                                    "text": formatted_text
                                }
                            ]
                        }
                    }
                except Exception as err:
                    response = {
                        "jsonrpc": "2.0",
                        "id": req_id,
                        "error": {
                            "code": -32603,
                            "message": str(err)
                        }
                    }
            elif method == "ping":
                response = {
                    "jsonrpc": "2.0",
                    "id": req_id,
                    "result": {}
                }
            else:
                response = {
                    "jsonrpc": "2.0",
                    "id": req_id,
                    "error": {
                        "code": -32601,
                        "message": f"Method '{method}' not found."
                    }
                }

            out_line = json.dumps(response, ensure_ascii=False)
            sys.stdout.write(out_line + "\n")
            sys.stdout.flush()

        except Exception as ex:
            log(f"Fatal error in server loop: {ex}")
            break

# ---------------------------------------------------------------------------
# CLI Command Line Interface
# ---------------------------------------------------------------------------

def run_cli():
    """Command-line interface when invoked directly by the user."""
    parser = argparse.ArgumentParser(
        description="DesktopNotes 电子便签 - AI MCP 服务与报告生成工具",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
示例:
  # 作为 MCP 协议服务端运行 (供 Antigravity / OpenCode / Codex / DSH 接入):
  python desktopnotes_mcp.py

  # 命令行直接生成本周周报并打印到终端:
  python desktopnotes_mcp.py --report weekly

  # 生成月报并保存到文件:
  python desktopnotes_mcp.py --report monthly --template okr --output 月报.md

  # 查询当前进行中的任务:
  python desktopnotes_mcp.py --list-tasks --status active

  # 查看总体统计指标:
  python desktopnotes_mcp.py --summary
        """
    )
    parser.add_argument("--stdio", action="store_true", help="显式以 MCP stdio 模式启动服务端")
    parser.add_argument("--summary", action="store_true", help="打印当前统计摘要")
    parser.add_argument("--report", choices=["weekly", "monthly", "yearly"], help="生成周报/月报/年报")
    parser.add_argument("--template", choices=["standard", "okr", "engineering", "executive"], default="standard", help="报告模板风格")
    parser.add_argument("--list-tasks", action="store_true", help="查看任务列表")
    parser.add_argument("--status", choices=["all", "active", "completed"], default="all", help="配合 --list-tasks 过滤状态")
    parser.add_argument("--output", help="输出报告的目标文件路径 (例如 report.md)")
    parser.add_argument("--author", default="开发者", help="汇报人署名")
    parser.add_argument("--db", help="指定 SQLite 数据库路径 (可选)")

    args = parser.parse_args()

    # If no arguments provided or --stdio specified, run MCP server
    if args.stdio or (len(sys.argv) == 1):
        run_mcp_server()
        return

    db = DesktopNotesDB(args.db)

    if args.summary:
        summary = db.get_summary()
        print(json.dumps(summary, ensure_ascii=False, indent=2))
        return

    if args.list_tasks:
        tasks = db.query_records(kind="task", status=args.status, include_tree=False)
        print(f"共检索到 {len(tasks)} 条任务:")
        for t in tasks:
            print(f"- [{t['status']}] [{t['priority']}] {t['title']} (创建: {t['created_at']}, 结项: {t['closed_at']})")
        return

    if args.report:
        md = db.generate_report_markdown(
            report_type=args.report,
            template=args.template,
            author=args.author
        )
        if args.output:
            with open(args.output, "w", encoding="utf-8") as f:
                f.write(md)
            print(f"✅ 报告已成功输出至: {os.path.abspath(args.output)}")
        else:
            print(md)
        return

if __name__ == "__main__":
    run_cli()
