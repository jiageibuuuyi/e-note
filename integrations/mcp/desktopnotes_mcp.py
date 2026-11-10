#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
DesktopNotes MCP Server & Report/Import Engine
Connects '电子便签 (DesktopNotes)' SQLite database to AI Coding Agents:
  - OpenCode
  - OpenAI Codex / GitHub Copilot
  - DSH (DeepSeek Harness)
  - Google Antigravity
  - Cursor / Claude Code / Windsurf

Supports:
  1. MCP (Model Context Protocol) JSON-RPC 2.0 stdio transport
  2. Bi-directional capabilities:
     - Read & Aggregate: Weekly/Monthly/Yearly reports, task trees, summaries
     - Write & Import: Multi-level study plans, chapter syllabi, project task trees, Markdown outlines, and sticky notes
  3. Standalone CLI mode for direct terminal execution
"""

import sys
import os
import re
import json
import sqlite3
import datetime
import uuid
import argparse
import contextlib
from typing import Dict, Any, List, Optional, Tuple, Iterator

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

    candidates = [
        os.path.join(os.path.dirname(__file__), "..", "..", "publish", "data", "app.db"),
        os.path.join(os.getcwd(), "publish", "data", "app.db"),
        os.path.join(os.getcwd(), "data", "app.db")
    ]
    for c in candidates:
        if os.path.isfile(c):
            return os.path.abspath(c)

    if local_app_data:
        return os.path.join(local_app_data, "DesktopNotes", "data", "app.db")
    return "app.db"


@contextlib.contextmanager
def get_readonly_connection(db_path: Optional[str] = None) -> Iterator[sqlite3.Connection]:
    """Create read-only connection to avoid SQLite database locks and guarantee close on exit."""
    path = db_path or get_db_path()
    if not os.path.isfile(path):
        raise FileNotFoundError(f"DesktopNotes 数据库不存在: {path}")
    conn = sqlite3.connect(f"file:{os.path.abspath(path)}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    try:
        yield conn
    finally:
        conn.close()


@contextlib.contextmanager
def get_readwrite_connection(db_path: Optional[str] = None) -> Iterator[sqlite3.Connection]:
    """Create read-write connection for inserting records and guarantee close on exit."""
    path = db_path or get_db_path()
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    conn = sqlite3.connect(path, timeout=5.0)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA busy_timeout = 5000;")
    conn.execute("PRAGMA foreign_keys = ON;")
    try:
        yield conn
    finally:
        conn.close()


def ms_to_datetime_str(ms: Optional[int]) -> Optional[str]:
    """Convert UTC epoch milliseconds to local formatted string."""
    if not ms:
        return None
    try:
        dt = datetime.datetime.fromtimestamp(ms / 1000.0, tz=datetime.timezone.utc).astimezone()
        return dt.strftime("%Y-%m-%d %H:%M:%S")
    except Exception:
        return None


def normalize_date_str(date_str: Optional[str]) -> Optional[str]:
    """Validate and normalize date string to YYYY-MM-DD."""
    if not date_str or not str(date_str).strip():
        return None
    raw = str(date_str).strip()[:10]
    dt = datetime.datetime.strptime(raw, "%Y-%m-%d").date()
    return dt.strftime("%Y-%m-%d")


def calc_due_boundary_ms(due_date_str: Optional[str]) -> Optional[int]:
    """
    Calculate due_boundary_utc_ms matching C# DeadlineRules.ResolveLocalMidnightUtc(date.AddDays(1), displayZone).
    """
    norm = normalize_date_str(due_date_str)
    if not norm:
        return None
    dt_due = datetime.datetime.strptime(norm, "%Y-%m-%d").date()
    next_day = dt_due + datetime.timedelta(days=1)
    tz = datetime.datetime.now().astimezone().tzinfo
    dt_midnight = datetime.datetime.combine(next_day, datetime.time.min).replace(tzinfo=tz)
    return int(dt_midnight.astimezone(datetime.timezone.utc).timestamp() * 1000)


def calc_start_ms(start_date_str: Optional[str], due_boundary_ms: Optional[int] = None) -> Optional[int]:
    """Calculate start_utc_ms for a given YYYY-MM-DD start date, ensuring start < due_boundary."""
    norm = normalize_date_str(start_date_str)
    if not norm:
        return None
    dt_start = datetime.datetime.strptime(norm, "%Y-%m-%d").date()
    tz = datetime.datetime.now().astimezone().tzinfo
    dt_midnight = datetime.datetime.combine(dt_start, datetime.time.min).replace(tzinfo=tz)
    start_ms = int(dt_midnight.astimezone(datetime.timezone.utc).timestamp() * 1000)
    if due_boundary_ms is not None and start_ms >= due_boundary_ms:
        start_ms = due_boundary_ms - 86400000
    return start_ms


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
        return datetime.date(2000, 1, 1), datetime.date(2099, 12, 31), "全部时段"
    else:
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


def parse_markdown_to_tree(markdown_text: str, default_priority: int = 2) -> Tuple[Optional[str], Optional[str], List[Dict[str, Any]]]:
    """
    Parse a Markdown document (with #/##/### headings and - / * / 1. list items)
    into (root_title, root_body, items_tree).
    Supports inline priority tags like [P0], [P1], [P2], [P3] and due dates like @due(2026-11-20) or (截止: 2026-11-20).
    """
    lines = markdown_text.splitlines()
    root_title: Optional[str] = None
    root_body_lines: List[str] = []

    items: List[Dict[str, Any]] = []
    # Stack of (level, node_dict)
    # Heading levels: H1=1, H2=2, H3=3, H4=4; Bullet list levels start at 10 + indent
    stack: List[Tuple[int, Dict[str, Any]]] = []

    prio_pattern = re.compile(r"\[(P[0-3])\]", re.IGNORECASE)
    due_pattern = re.compile(r"(?:@due\(|[\(（]截止[:：]\s*)(\d{4}-\d{2}-\d{2})[\)）]")
    start_pattern = re.compile(r"(?:@start\(|[\(（]开始[:：]\s*)(\d{4}-\d{2}-\d{2})[\)）]")

    def extract_meta(text: str) -> Tuple[str, int, Optional[str], Optional[str]]:
        prio = default_priority
        m_prio = prio_pattern.search(text)
        if m_prio:
            p_str = m_prio.group(1).upper()
            prio = int(p_str[1])
            text = prio_pattern.sub("", text)

        due_dt = None
        m_due = due_pattern.search(text)
        if m_due:
            due_dt = m_due.group(1)
            text = due_pattern.sub("", text)

        start_dt = None
        m_start = start_pattern.search(text)
        if m_start:
            start_dt = m_start.group(1)
            text = start_pattern.sub("", text)

        # Strip checkbox markers like [ ] or [x]
        text = re.sub(r"^\[[ xX]\]\s*", "", text.strip())
        return text.strip(), prio, start_dt, due_dt

    in_code_block = False
    for raw_line in lines:
        stripped = raw_line.strip()
        if stripped.startswith("```"):
            in_code_block = not in_code_block
            if stack:
                stack[-1][1]["body"] = (stack[-1][1].get("body", "") + "\n" + raw_line).strip()
            elif root_title:
                root_body_lines.append(raw_line)
            continue

        if in_code_block:
            if stack:
                stack[-1][1]["body"] = (stack[-1][1].get("body", "") + "\n" + raw_line).strip()
            elif root_title:
                root_body_lines.append(raw_line)
            continue

        # Check Heading
        m_heading = re.match(r"^(#{1,6})\s+(.+)$", stripped)
        if m_heading:
            h_level = len(m_heading.group(1))
            h_text = m_heading.group(2).strip()
            clean_title, prio, start_dt, due_dt = extract_meta(h_text)
            if not clean_title:
                continue

            if h_level == 1 and root_title is None and not items:
                root_title = clean_title
                continue

            node: Dict[str, Any] = {
                "title": clean_title,
                "body": "",
                "priority": prio,
                "start_date": start_dt,
                "due_date": due_dt,
                "children": []
            }
            while stack and stack[-1][0] >= h_level:
                stack.pop()
            if stack:
                stack[-1][1]["children"].append(node)
            else:
                items.append(node)
            stack.append((h_level, node))
            continue

        # Check Bullet or Numbered List Item
        m_list = re.match(r"^(\s*)(?:[-*+]|\d+\.)\s+(.+)$", raw_line)
        if m_list:
            indent = len(m_list.group(1).expandtabs(2))
            item_text = m_list.group(2).strip()
            clean_title, prio, start_dt, due_dt = extract_meta(item_text)
            if not clean_title:
                continue

            list_level = 10 + indent
            node = {
                "title": clean_title,
                "body": "",
                "priority": prio,
                "start_date": start_dt,
                "due_date": due_dt,
                "children": []
            }
            while stack and stack[-1][0] >= list_level:
                stack.pop()
            if stack:
                stack[-1][1]["children"].append(node)
            else:
                items.append(node)
            stack.append((list_level, node))
            continue

        # Regular paragraph / blockquote line -> append to current node's body or root_body
        if stripped:
            clean_line = re.sub(r"^>\s?", "", stripped)
            if stack:
                cur_body = stack[-1][1].get("body", "")
                stack[-1][1]["body"] = f"{cur_body}\n{clean_line}".strip() if cur_body else clean_line
            elif root_title is not None:
                root_body_lines.append(clean_line)

    root_body = "\n".join(root_body_lines).strip() if root_body_lines else None
    return root_title, root_body, items


def format_children_as_markdown(children: List[Dict[str, Any]], depth: int = 0) -> str:
    """Format nested children items into clean Markdown for Note body."""
    md_lines: List[str] = []
    indent = "  " * depth
    for idx, child in enumerate(children, 1):
        title = child.get("title", "").strip()
        due = child.get("due_date")
        start = child.get("start_date")
        body = child.get("body", "").strip()
        sub = child.get("children") or []

        date_tag = ""
        if start and due:
            date_tag = f" *(计划: {start} ~ {due})*"
        elif due:
            date_tag = f" *(截止: {due})*"
        elif start:
            date_tag = f" *(开始: {start})*"

        if depth == 0:
            md_lines.append(f"### {idx}. {title}{date_tag}")
        else:
            md_lines.append(f"{indent}- [ ] **{title}**{date_tag}")

        if body:
            for b_line in body.splitlines():
                md_lines.append(f"{indent}  > {b_line}")

        if sub:
            md_lines.append(format_children_as_markdown(sub, depth + 1))

        if depth == 0:
            md_lines.append("")
    return "\n".join(md_lines).strip()


class DesktopNotesDB:
    """Encapsulates all database operations."""

    def __init__(self, db_path: Optional[str] = None):
        self.db_path = db_path or get_db_path()

    def _get_projection_zone(self, conn: sqlite3.Connection) -> str:
        """Query existing projection_zone from effective_records to stay consistent with DesktopNotes."""
        try:
            row = conn.execute("SELECT projection_zone FROM effective_records LIMIT 1").fetchone()
            if row and row[0]:
                return str(row[0])
        except Exception:
            pass
        return "China Standard Time"

    def _insert_record_internal(
        self,
        conn: sqlite3.Connection,
        title: str,
        body: str,
        kind_int: int,
        priority: int,
        start_date: Optional[str],
        due_date: Optional[str],
        parent_id: Optional[str],
        now_ms: int,
        projection_zone: str,
        publisher: str = "AI Agent"
    ) -> Dict[str, Any]:
        """Low-level transactional record + effective_record insertion respecting all SQLite triggers & checks."""
        record_id = str(uuid.uuid4())
        clean_title = (title or "").strip()
        clean_body = (body or "").strip()
        if not clean_title and not clean_body:
            raise ValueError("任务或便签的标题与内容不能同时为空。")

        # Enforce SQLite constraint: parent_id IS NULL OR (kind = 0 AND parent_id <> id)
        if parent_id is not None:
            kind_int = 0

        prio = max(0, min(3, int(priority if priority is not None else 2)))
        norm_due = normalize_date_str(due_date) if kind_int == 0 else None
        due_kind = 1 if norm_due else 0
        due_boundary = calc_due_boundary_ms(norm_due) if norm_due else None
        start_ms = calc_start_ms(start_date, due_boundary) if (kind_int == 0 and start_date) else None

        conn.execute("""
            INSERT INTO records (
                id, parent_id, kind, title, body, status,
                publisher, assignee, priority, start_utc_ms,
                due_kind, due_date, due_utc_ms, due_zone,
                attachment_root_id, inherit_mask,
                created_utc_ms, updated_utc_ms, version
            ) VALUES (
                ?, ?, ?, ?, ?, 0,
                ?, NULL, ?, ?,
                ?, ?, NULL, NULL,
                NULL, 0,
                ?, ?, 1
            )
        """, (
            record_id, parent_id, kind_int, clean_title, clean_body,
            publisher, prio, start_ms,
            due_kind, norm_due,
            now_ms, now_ms
        ))

        conn.execute("""
            INSERT INTO effective_records (
                record_id, publisher, assignee, priority, start_utc_ms,
                due_kind, due_date, due_utc_ms, due_zone, attachment_root_id,
                due_boundary_utc_ms, projection_zone, record_version
            ) VALUES (
                ?, ?, NULL, ?, ?,
                ?, ?, NULL, NULL, NULL,
                ?, ?, 1
            )
        """, (
            record_id, publisher, prio, start_ms,
            due_kind, norm_due,
            due_boundary, projection_zone
        ))

        return {
            "id": record_id,
            "parent_id": parent_id,
            "title": clean_title,
            "kind": "task" if kind_int == 0 else "note",
            "priority": PRIORITY_MAP.get(prio, f"P{prio}"),
            "start_date": normalize_date_str(start_date) if start_ms else None,
            "due_date": norm_due
        }

    def get_summary(self, period: str = "this_week", start_date: Optional[str] = None, end_date: Optional[str] = None) -> Dict[str, Any]:
        """Get high level statistics for tasks and notes."""
        d_start, d_end, period_label = resolve_date_range(period, start_date, end_date)
        start_ms, end_ms = date_range_to_ms(d_start, d_end)

        with get_readonly_connection(self.db_path) as conn:
            c = conn.cursor()

            c.execute("""
                SELECT count(*) FROM records 
                WHERE deleted_utc_ms IS NULL AND kind = 0 AND status = 1
                  AND closed_utc_ms >= ? AND closed_utc_ms <= ?
            """, (start_ms, end_ms))
            completed_in_period = c.fetchone()[0]

            c.execute("""
                SELECT count(*) FROM records 
                WHERE deleted_utc_ms IS NULL AND kind = 0
                  AND created_utc_ms >= ? AND created_utc_ms <= ?
            """, (start_ms, end_ms))
            created_in_period = c.fetchone()[0]

            c.execute("SELECT count(*) FROM records WHERE deleted_utc_ms IS NULL AND kind = 0 AND status = 0")
            current_active = c.fetchone()[0]

            c.execute("SELECT count(*) FROM records WHERE deleted_utc_ms IS NULL AND kind = 1")
            total_notes = c.fetchone()[0]

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
        kind: str = "all",
        status: str = "all",
        period: str = "all",
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

            query += " ORDER BY r.status ASC, r.priority ASC, r.created_utc_ms ASC"
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
        lines.append("| 指标项 | 统计值 | 状态与说明 |")
        lines.append("| :--- | :--- | :--- |")
        lines.append(f"| **本期已结项交付** | **{metrics['completed_count']}** 项 | ✅ 均已完成验收结项 |")
        lines.append(f"| **进行中攻坚任务** | **{metrics['active_count']}** 项 | ⏳ 正在推进中 |")
        if metrics['overdue_active_count'] > 0:
            lines.append(f"| **逾期风险预警** | **{metrics['overdue_active_count']}** 项 | ⚠️ 需重点关注排期 |")
        else:
            lines.append("| **逾期风险预警** | 0 项 | 🟢 进度正常，无逾期风险 |")
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
        start_date: Optional[str] = None,
        due_date: Optional[str] = None,
        parent_id: Optional[str] = None
    ) -> Dict[str, Any]:
        """Create a single task or note directly in DesktopNotes."""
        kind_int = 0 if kind == "task" else 1
        now_ms = int(datetime.datetime.now(datetime.timezone.utc).timestamp() * 1000)

        with get_readwrite_connection(self.db_path) as conn:
            proj_zone = self._get_projection_zone(conn)
            created = self._insert_record_internal(
                conn=conn,
                title=title,
                body=body,
                kind_int=kind_int,
                priority=priority,
                start_date=start_date,
                due_date=due_date,
                parent_id=parent_id,
                now_ms=now_ms,
                projection_zone=proj_zone
            )
            conn.commit()
            return created

    def import_plan(
        self,
        items: List[Dict[str, Any]],
        import_as: str = "task_tree",
        root_title: Optional[str] = None,
        root_body: Optional[str] = None,
        root_priority: int = 1,
        root_due_date: Optional[str] = None,
        parent_id: Optional[str] = None,
        auto_schedule_start_date: Optional[str] = None,
        days_per_item: Optional[int] = None
    ) -> Dict[str, Any]:
        """
        Batch-import a study syllabus, chapter learning plan, project breakdown, or note collection.
        Modes (`import_as`):
          - 'task_tree': Import as multi-level hierarchical Tasks (Parent -> Chapter -> Section) with deadlines & calendar sync.
          - 'note_series': Import each top-level chapter/item as a standalone sticky Note (kind=1) with children formatted as Markdown body.
          - 'note_single': Merge the entire plan/catalog into one comprehensive Markdown sticky Note (kind=1).
        """
        if not items and not root_title:
            raise ValueError("导入内容不能为空，请至少提供 items 列表或 root_title。")

        # Optional automatic chapter scheduling
        if auto_schedule_start_date and days_per_item and days_per_item > 0 and items:
            cur_date = datetime.datetime.strptime(normalize_date_str(auto_schedule_start_date), "%Y-%m-%d").date()
            for item in items:
                item_days = int(item.get("duration_days") or days_per_item)
                item_start = cur_date
                item_end = cur_date + datetime.timedelta(days=max(1, item_days) - 1)
                if not item.get("start_date"):
                    item["start_date"] = item_start.strftime("%Y-%m-%d")
                if not item.get("due_date"):
                    item["due_date"] = item_end.strftime("%Y-%m-%d")

                # Also distribute dates across immediate sub-sections if they don't have due_date
                children = item.get("children") or []
                if children:
                    sub_count = len(children)
                    for idx, child in enumerate(children):
                        if not child.get("due_date"):
                            offset = int(((idx + 1) * max(1, item_days)) / sub_count) - 1
                            offset = max(0, min(max(1, item_days) - 1, offset))
                            sub_due = item_start + datetime.timedelta(days=offset)
                            child["due_date"] = sub_due.strftime("%Y-%m-%d")
                cur_date = item_end + datetime.timedelta(days=1)

            if not root_due_date and items:
                last_due = items[-1].get("due_date")
                if last_due:
                    root_due_date = last_due

        base_ms = int(datetime.datetime.now(datetime.timezone.utc).timestamp() * 1000)
        seq = 0
        created_records: List[Dict[str, Any]] = []

        with get_readwrite_connection(self.db_path) as conn:
            proj_zone = self._get_projection_zone(conn)

            # Mode 1: Merge everything into a single comprehensive Markdown Note (kind=1)
            if import_as == "note_single":
                note_title = root_title or (items[0].get("title", "学习与工作计划") if len(items) == 1 else "导入的学习计划与目录大纲")
                md_parts: List[str] = []
                if root_body:
                    md_parts.append(root_body.strip())
                    md_parts.append("")
                if items:
                    md_parts.append(format_children_as_markdown(items, depth=0))
                full_body = "\n".join(md_parts).strip()

                rec = self._insert_record_internal(
                    conn=conn,
                    title=note_title,
                    body=full_body,
                    kind_int=1,
                    priority=root_priority,
                    start_date=None,
                    due_date=None,
                    parent_id=None,
                    now_ms=base_ms,
                    projection_zone=proj_zone
                )
                created_records.append(rec)
                conn.commit()
                return {
                    "status": "success",
                    "import_as": "note_single",
                    "total_imported": len(created_records),
                    "root_record_id": rec["id"],
                    "records": created_records
                }

            # Mode 2: Import each top-level chapter/item as a standalone Note (kind=1)
            if import_as == "note_series":
                prefix = f"【{root_title}】" if root_title else ""
                if root_title and root_body:
                    overview_rec = self._insert_record_internal(
                        conn=conn,
                        title=f"{prefix}总览与说明",
                        body=root_body,
                        kind_int=1,
                        priority=root_priority,
                        start_date=None,
                        due_date=None,
                        parent_id=None,
                        now_ms=base_ms + seq,
                        projection_zone=proj_zone
                    )
                    seq += 1
                    created_records.append(overview_rec)

                for item in items:
                    item_title = f"{prefix}{item.get('title', '未命名章节').strip()}"
                    body_parts: List[str] = []
                    if item.get("start_date") or item.get("due_date"):
                        s_str = item.get("start_date") or "即日起"
                        d_str = item.get("due_date") or "待定"
                        body_parts.append(f"> 📅 **计划周期**：{s_str} 至 {d_str}")
                    if item.get("body"):
                        body_parts.append(str(item["body"]).strip())
                    children = item.get("children") or []
                    if children:
                        body_parts.append("\n" + format_children_as_markdown(children, depth=1))

                    rec = self._insert_record_internal(
                        conn=conn,
                        title=item_title,
                        body="\n\n".join(body_parts).strip(),
                        kind_int=1,
                        priority=int(item.get("priority", root_priority)),
                        start_date=None,
                        due_date=None,
                        parent_id=None,
                        now_ms=base_ms + seq,
                        projection_zone=proj_zone
                    )
                    seq += 1
                    created_records.append(rec)

                conn.commit()
                return {
                    "status": "success",
                    "import_as": "note_series",
                    "total_imported": len(created_records),
                    "records": created_records
                }

            # Mode 3 (Default): Import as multi-level hierarchical Task Tree (kind=0)
            active_root_id = parent_id
            if root_title:
                root_rec = self._insert_record_internal(
                    conn=conn,
                    title=root_title,
                    body=root_body or "",
                    kind_int=0,
                    priority=root_priority,
                    start_date=auto_schedule_start_date,
                    due_date=root_due_date,
                    parent_id=parent_id,
                    now_ms=base_ms + seq,
                    projection_zone=proj_zone
                )
                seq += 1
                active_root_id = root_rec["id"]
                created_records.append(root_rec)

            def insert_tree_recursive(nodes: List[Dict[str, Any]], cur_parent_id: Optional[str]) -> None:
                nonlocal seq
                for node in nodes:
                    children = node.get("children") or []
                    req_kind = str(node.get("kind", "task")).lower()
                    # If it has a parent or has children, it MUST be a task (kind=0) in DesktopNotes
                    if cur_parent_id is not None or children:
                        k_int = 0
                    else:
                        k_int = 1 if req_kind == "note" else 0

                    rec = self._insert_record_internal(
                        conn=conn,
                        title=node.get("title", "未命名条目"),
                        body=node.get("body", ""),
                        kind_int=k_int,
                        priority=int(node.get("priority", 2)),
                        start_date=node.get("start_date"),
                        due_date=node.get("due_date"),
                        parent_id=cur_parent_id if k_int == 0 else None,
                        now_ms=base_ms + seq,
                        projection_zone=proj_zone
                    )
                    seq += 1
                    created_records.append(rec)
                    if children:
                        insert_tree_recursive(children, rec["id"])

            insert_tree_recursive(items, active_root_id)
            conn.commit()

            return {
                "status": "success",
                "import_as": "task_tree",
                "total_imported": len(created_records),
                "root_record_id": active_root_id,
                "records": created_records
            }

    def import_markdown(
        self,
        markdown_text: str,
        import_as: str = "task_tree",
        root_title: Optional[str] = None,
        root_priority: int = 1,
        root_due_date: Optional[str] = None,
        parent_id: Optional[str] = None,
        auto_schedule_start_date: Optional[str] = None,
        days_per_item: Optional[int] = None
    ) -> Dict[str, Any]:
        """Parse a Markdown outline/syllabus and import into DesktopNotes."""
        parsed_title, parsed_body, parsed_items = parse_markdown_to_tree(markdown_text, default_priority=root_priority)
        final_root_title = root_title or parsed_title
        return self.import_plan(
            items=parsed_items,
            import_as=import_as,
            root_title=final_root_title,
            root_body=parsed_body,
            root_priority=root_priority,
            root_due_date=root_due_date,
            parent_id=parent_id,
            auto_schedule_start_date=auto_schedule_start_date,
            days_per_item=days_per_item
        )

    def delete_records(
        self,
        record_ids: Optional[List[str]] = None,
        exact_title: Optional[str] = None,
        search_keyword: Optional[str] = None,
        kind: str = "all",
        permanent: bool = False,
        empty_trash: bool = False
    ) -> Dict[str, Any]:
        """
        Delete tasks or notes (along with any descendant subtasks).
        - By default (`permanent=False`), moves records safely into the Recycle Bin (回收站) so they can be restored if needed.
        - When `permanent=True`, permanently deletes the records from SQLite in post-order (children before parents).
        - When `empty_trash=True`, permanently purges all items currently in the Recycle Bin.
        """
        now_ms = int(datetime.datetime.now(datetime.timezone.utc).timestamp() * 1000)

        with get_readwrite_connection(self.db_path) as conn:
            proj_zone = self._get_projection_zone(conn)
            seed_ids: List[str] = []

            if empty_trash:
                rows = conn.execute("SELECT id FROM records WHERE deleted_utc_ms IS NOT NULL").fetchall()
                seed_ids.extend(r["id"] for r in rows)
                permanent = True
            else:
                if record_ids:
                    for rid in record_ids:
                        if rid and str(rid).strip():
                            seed_ids.append(str(rid).strip())

                if exact_title or search_keyword:
                    query = "SELECT id FROM records WHERE 1=1"
                    params: List[Any] = []
                    if not permanent:
                        query += " AND deleted_utc_ms IS NULL"
                    if kind == "task":
                        query += " AND kind = 0"
                    elif kind == "note":
                        query += " AND kind = 1"

                    if exact_title:
                        query += " AND title = ?"
                        params.append(exact_title.strip())
                    if search_keyword:
                        query += " AND (title LIKE ? OR body LIKE ?)"
                        params.extend([f"%{search_keyword.strip()}%", f"%{search_keyword.strip()}%"])

                    matched_rows = conn.execute(query, params).fetchall()
                    for r in matched_rows:
                        seed_ids.append(r["id"])

                if not seed_ids:
                    return {
                        "status": "no_match",
                        "deleted_count": 0,
                        "mode": "permanent" if permanent else "trash",
                        "deleted_records": [],
                        "message": "未找到匹配的便签或任务（请提供有效的 record_ids、exact_title 或 search_keyword）。"
                    }

            # Post-order DFS traversal: ensures all children/descendants appear BEFORE their parents
            ordered_ids: List[str] = []
            seen: set[str] = set()

            def collect_postorder(rec_id: str) -> None:
                if rec_id in seen:
                    return
                seen.add(rec_id)
                children = conn.execute("SELECT id FROM records WHERE parent_id = ?", (rec_id,)).fetchall()
                for ch in children:
                    collect_postorder(ch["id"])
                ordered_ids.append(rec_id)

            for sid in seed_ids:
                exists = conn.execute("SELECT id FROM records WHERE id = ?", (sid,)).fetchone()
                if exists:
                    collect_postorder(sid)

            if not ordered_ids:
                return {
                    "status": "no_match",
                    "deleted_count": 0,
                    "mode": "permanent" if permanent else "trash",
                    "deleted_records": [],
                    "message": "未找到可删除的便签或任务记录。"
                }

            deleted_info: List[Dict[str, Any]] = []
            batch_id = str(uuid.uuid4())

            for rid in ordered_ids:
                row = conn.execute(
                    "SELECT id, parent_id, kind, title, status, version, deleted_utc_ms FROM records WHERE id = ?",
                    (rid,)
                ).fetchone()
                if not row:
                    continue

                rec_summary = {
                    "id": row["id"],
                    "parent_id": row["parent_id"],
                    "kind": "task" if row["kind"] == 0 else "note",
                    "title": row["title"]
                }

                if permanent:
                    # Clean up attachment jobs and attachments referencing this record
                    conn.execute("""
                        DELETE FROM attachment_job_items
                        WHERE job_id IN (SELECT id FROM attachment_jobs WHERE record_id = ?)
                    """, (rid,))
                    conn.execute("DELETE FROM attachment_jobs WHERE record_id = ?", (rid,))
                    conn.execute("DELETE FROM attachments WHERE record_id = ?", (rid,))
                    conn.execute("DELETE FROM records WHERE id = ?", (rid,))
                    deleted_info.append(rec_summary)
                else:
                    # Soft delete (move to Recycle Bin) if not already trashed
                    if row["deleted_utc_ms"] is None:
                        next_ver = int(row["version"]) + 1
                        conn.execute("""
                            UPDATE records
                            SET deleted_utc_ms = ?, deletion_batch_id = ?, updated_utc_ms = ?, version = ?
                            WHERE id = ?
                        """, (now_ms, batch_id, now_ms, next_ver, rid))
                        conn.execute("""
                            UPDATE effective_records
                            SET record_version = ?, projection_zone = ?
                            WHERE record_id = ?
                        """, (next_ver, proj_zone, rid))
                        conn.execute("""
                            DELETE FROM reminder_deliveries
                            WHERE record_id = ? AND delivery_state = 'claimed'
                        """, (rid,))
                        deleted_info.append(rec_summary)

            conn.commit()
            mode_label = "永久彻底删除 (permanent)" if permanent else "移入回收站 (trash)"
            return {
                "status": "success",
                "mode": "permanent" if permanent else "trash",
                "deleted_count": len(deleted_info),
                "deleted_records": deleted_info,
                "message": f"已成功执行{mode_label}，共处理 {len(deleted_info)} 条记录（含关联子任务）。"
            }


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
            "description": "查询桌面便签中的任务或笔记清单，支持按完成状态、时间周期、优先级、关键字过滤及层级树形展示。",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "kind": {
                        "type": "string",
                        "enum": ["all", "task", "note"],
                        "description": "类型过滤：task(待办任务), note(纯笔记便签), all(全部)，默认 task"
                    },
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
                        "description": "在标题和详情中模糊检索的关键词"
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
            "description": "向桌面便签(DesktopNotes)写入单条新待办任务或笔记便签。",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "title": {
                        "type": "string",
                        "description": "便签或任务标题"
                    },
                    "body": {
                        "type": "string",
                        "description": "详细内容(支持 Markdown 格式)"
                    },
                    "kind": {
                        "type": "string",
                        "enum": ["task", "note"],
                        "description": "类型：task(可勾选待办任务), note(纯文本笔记便签)"
                    },
                    "priority": {
                        "type": "integer",
                        "enum": [0, 1, 2, 3],
                        "description": "优先级：0(P0紧急), 1(P1高), 2(P2普通), 3(P3低)，默认为 2"
                    },
                    "start_date": {
                        "type": "string",
                        "description": "计划开始日期 YYYY-MM-DD (可选)"
                    },
                    "due_date": {
                        "type": "string",
                        "description": "计划截止日期 YYYY-MM-DD (可选，设置后将自动同步到电子便签月历视图)"
                    },
                    "parent_id": {
                        "type": "string",
                        "description": "父任务 ID (若创建子任务，可选)"
                    }
                },
                "required": ["title"]
            }
        },
        {
            "name": "desktopnotes_import_plan",
            "description": "批量导入结构化的章节学习计划、课程目录、多级项目拆解或系列读书笔记到桌面便签(DesktopNotes)。支持多级父子任务树(task_tree)、系列独立笔记(note_series)或单篇汇总大纲笔记(note_single)，并支持按天自动排期。",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "import_as": {
                        "type": "string",
                        "enum": ["task_tree", "note_series", "note_single"],
                        "description": "导入模式：task_tree(作为多级可勾选待办任务树导入，支持截止日与月历追踪，默认推荐), note_series(按章节拆分为多张独立笔记便签), note_single(合并为单篇完整 Markdown 笔记便签)"
                    },
                    "root_title": {
                        "type": "string",
                        "description": "顶层总标题(例如：《深入理解计算机系统》章节学习计划)"
                    },
                    "root_body": {
                        "type": "string",
                        "description": "顶层总说明或学习目标概述(支持 Markdown)"
                    },
                    "root_priority": {
                        "type": "integer",
                        "enum": [0, 1, 2, 3],
                        "description": "顶层优先级：0(P0紧急), 1(P1高), 2(P2普通), 3(P3低)，默认 1"
                    },
                    "root_due_date": {
                        "type": "string",
                        "description": "顶层总截止日期 YYYY-MM-DD (可选)"
                    },
                    "auto_schedule_start_date": {
                        "type": "string",
                        "description": "自动排期起始日期 YYYY-MM-DD (可选：配合 days_per_item 自动为每个章节推算开始与截止日期)"
                    },
                    "days_per_item": {
                        "type": "integer",
                        "description": "自动排期时每个一级章节默认分配的天数 (例如 3 表示每3天学完一章)"
                    },
                    "parent_id": {
                        "type": "string",
                        "description": "可选：挂载到已存在的某个父任务 ID 下"
                    },
                    "items": {
                        "type": "array",
                        "description": "章节或任务条目列表，每个条目可递归包含 children 子条目",
                        "items": {
                            "type": "object",
                            "properties": {
                                "title": {"type": "string", "description": "章节或小节标题"},
                                "body": {"type": "string", "description": "学习重点、课后练习或笔记详情 (Markdown)"},
                                "priority": {"type": "integer", "enum": [0, 1, 2, 3], "description": "优先级 0-3"},
                                "start_date": {"type": "string", "description": "开始日期 YYYY-MM-DD"},
                                "due_date": {"type": "string", "description": "截止日期 YYYY-MM-DD"},
                                "duration_days": {"type": "integer", "description": "可选：开启自动排期时单独指定本章耗时天数"},
                                "children": {
                                    "type": "array",
                                    "description": "子小节或子任务列表 (结构与父条目相同，支持多级嵌套)",
                                    "items": {"type": "object"}
                                }
                            },
                            "required": ["title"]
                        }
                    }
                },
                "required": ["items"]
            }
        },
        {
            "name": "desktopnotes_import_markdown",
            "description": "将 Markdown 格式的目录大纲、章节学习计划或任务清单直接解析并导入到桌面便签(DesktopNotes)中。支持自动识别 #/##/### 标题层级与 - 列表层级。",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "markdown_text": {
                        "type": "string",
                        "description": "包含章节目录或学习计划的 Markdown 文本(支持 # 标题层级、- 列表层级、[P0]-[P3] 优先级标记、@due(YYYY-MM-DD) 截止日标记)"
                    },
                    "import_as": {
                        "type": "string",
                        "enum": ["task_tree", "note_series", "note_single"],
                        "description": "导入模式：task_tree(多级待办任务树，默认), note_series(按章节拆分为多条独立笔记), note_single(单条完整笔记)"
                    },
                    "root_title": {
                        "type": "string",
                        "description": "可选：指定顶层总标题(若不指定则自动提取 Markdown 中的一级标题 #)"
                    },
                    "root_priority": {
                        "type": "integer",
                        "enum": [0, 1, 2, 3],
                        "description": "默认优先级 0-3，默认 1"
                    },
                    "auto_schedule_start_date": {
                        "type": "string",
                        "description": "可选：自动排期开始日期 YYYY-MM-DD"
                    },
                    "days_per_item": {
                        "type": "integer",
                        "description": "可选：自动排期时每个一级章节分配的天数"
                    }
                },
                "required": ["markdown_text"]
            }
        },
        {
            "name": "desktopnotes_delete_records",
            "description": "删除桌面便签(DesktopNotes)中的任务或笔记(若删除父任务则自动级联处理其所有子任务)。默认移入回收站(软删除，可随时恢复)，也可指定 permanent=true 永久彻底删除，或指定 empty_trash=true 清空回收站。",
            "inputSchema": {
                "type": "object",
                "properties": {
                    "record_ids": {
                        "type": "array",
                        "items": {"type": "string"},
                        "description": "要删除的任务或便签 ID 列表 (可选)"
                    },
                    "exact_title": {
                        "type": "string",
                        "description": "按精确标题匹配删除 (可选，例如：'《Rust 核心编程实战》学习目录')"
                    },
                    "search_keyword": {
                        "type": "string",
                        "description": "按标题或内容包含的关键词匹配删除 (可选)"
                    },
                    "kind": {
                        "type": "string",
                        "enum": ["all", "task", "note"],
                        "description": "按类型过滤要删除的记录：task(仅删任务), note(仅删笔记), all(全部，默认)"
                    },
                    "permanent": {
                        "type": "boolean",
                        "description": "是否永久彻底删除：false(默认推荐，移入回收站可恢复), true(从数据库彻底物理删除)"
                    },
                    "empty_trash": {
                        "type": "boolean",
                        "description": "是否清空回收站中所有已删除的记录，默认 false"
                    }
                }
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
            kind=args.get("kind", "task"),
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
        created = db.create_record(
            title=args["title"],
            body=args.get("body", ""),
            kind=args.get("kind", "task"),
            priority=args.get("priority", 2),
            start_date=args.get("start_date"),
            due_date=args.get("due_date"),
            parent_id=args.get("parent_id")
        )
        return {
            "status": "success",
            "record": created,
            "message": f"成功写入桌面便签: {created['title']}"
        }
    elif tool_name == "desktopnotes_import_plan":
        return db.import_plan(
            items=args.get("items", []),
            import_as=args.get("import_as", "task_tree"),
            root_title=args.get("root_title"),
            root_body=args.get("root_body"),
            root_priority=args.get("root_priority", 1),
            root_due_date=args.get("root_due_date"),
            parent_id=args.get("parent_id"),
            auto_schedule_start_date=args.get("auto_schedule_start_date"),
            days_per_item=args.get("days_per_item")
        )
    elif tool_name == "desktopnotes_import_markdown":
        return db.import_markdown(
            markdown_text=args["markdown_text"],
            import_as=args.get("import_as", "task_tree"),
            root_title=args.get("root_title"),
            root_priority=args.get("root_priority", 1),
            root_due_date=args.get("root_due_date"),
            parent_id=args.get("parent_id"),
            auto_schedule_start_date=args.get("auto_schedule_start_date"),
            days_per_item=args.get("days_per_item")
        )
    elif tool_name == "desktopnotes_delete_records":
        return db.delete_records(
            record_ids=args.get("record_ids"),
            exact_title=args.get("exact_title"),
            search_keyword=args.get("search_keyword"),
            kind=args.get("kind", "all"),
            permanent=bool(args.get("permanent", False)),
            empty_trash=bool(args.get("empty_trash", False))
        )
    else:
        raise ValueError(f"未知的工具名称: {tool_name}")


def run_mcp_server():
    """Run JSON-RPC 2.0 stdio server loop."""
    log("DesktopNotes MCP Server starting over stdio...")
    db = DesktopNotesDB()
    log(f"Connected to database: {db.db_path}")

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

            if method == "notifications/initialized":
                log("MCP Client initialized successfully.")
                continue

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
                            "version": "1.1.0"
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
        description="DesktopNotes 电子便签 - AI MCP 服务与双向数据导入/报告工具",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
示例:
  # 1. 作为 MCP 协议服务端运行 (供 OpenCode / Codex / DSH / Antigravity 接入):
  desktopnotes-mcp

  # 2. 命令行生成本周周报:
  desktopnotes-mcp --report weekly --author 徐礼佳

  # 3. 将 Markdown 学习目录/章节计划导入为多级便签待办树 (并自动按每章3天排期):
  desktopnotes-mcp --import-md study_plan.md --import-as task_tree --start-date 2026-11-11 --days-per-item 3

  # 4. 将 Markdown 目录拆分为系列独立笔记便签导入:
  desktopnotes-mcp --import-md study_plan.md --import-as note_series

  # 5. 将 JSON 结构化计划导入电子便签:
  desktopnotes-mcp --import-json plan.json
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
    parser.add_argument("--import-md", help="从 Markdown 文件导入学习目录/章节计划到电子便签")
    parser.add_argument("--import-json", help="从 JSON 文件导入多级计划或笔记列表到电子便签")
    parser.add_argument("--import-as", choices=["task_tree", "note_series", "note_single"], default="task_tree", help="导入模式：task_tree(多级待办树), note_series(系列笔记), note_single(单篇汇总笔记)")
    parser.add_argument("--root-title", help="导入时指定的顶层总标题 (可选)")
    parser.add_argument("--start-date", help="导入学习计划时的自动排期起始日期 YYYY-MM-DD (可选)")
    parser.add_argument("--days-per-item", type=int, help="自动排期时每个一级章节分配的天数 (可选)")
    parser.add_argument("--delete-id", action="append", help="按记录 ID 删除任务或笔记 (可多次指定)")
    parser.add_argument("--delete-title", help="按精确标题删除任务或笔记 (包含其所有子任务)")
    parser.add_argument("--delete-keyword", help="按关键词匹配删除任务或笔记 (包含其所有子任务)")
    parser.add_argument("--kind", choices=["all", "task", "note"], default="all", help="配合删除或查询过滤类型：task(任务), note(笔记), all(全部)")
    parser.add_argument("--permanent", action="store_true", help="永久彻底删除记录 (默认不加此参数则安全移入回收站)")
    parser.add_argument("--empty-trash", action="store_true", help="清空回收站中所有已删除的记录")
    parser.add_argument("--db", help="指定 SQLite 数据库路径 (可选)")

    args = parser.parse_args()

    if args.stdio or (len(sys.argv) == 1):
        run_mcp_server()
        return

    db = DesktopNotesDB(args.db)

    if args.delete_id or args.delete_title or args.delete_keyword or args.empty_trash:
        res = db.delete_records(
            record_ids=args.delete_id,
            exact_title=args.delete_title,
            search_keyword=args.delete_keyword,
            kind=args.kind,
            permanent=args.permanent,
            empty_trash=args.empty_trash
        )
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    if args.summary:
        summary = db.get_summary()
        print(json.dumps(summary, ensure_ascii=False, indent=2))
        return

    if args.list_tasks:
        tasks = db.query_records(kind="task", status=args.status, include_tree=False)
        print(f"共检索到 {len(tasks)} 条任务:")
        for t in tasks:
            print(f"- [{t['status']}] [{t['priority']}] {t['title']} (创建: {t['created_at']}, 截止: {t['due_date']}, 结项: {t['closed_at']})")
        return

    if args.import_md:
        with open(args.import_md, "r", encoding="utf-8") as f:
            md_content = f.read()
        res = db.import_markdown(
            markdown_text=md_content,
            import_as=args.import_as,
            root_title=args.root_title,
            auto_schedule_start_date=args.start_date,
            days_per_item=args.days_per_item
        )
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    if args.import_json:
        with open(args.import_json, "r", encoding="utf-8") as f:
            payload = json.load(f)
        if isinstance(payload, list):
            res = db.import_plan(
                items=payload,
                import_as=args.import_as,
                root_title=args.root_title,
                auto_schedule_start_date=args.start_date,
                days_per_item=args.days_per_item
            )
        else:
            res = db.import_plan(
                items=payload.get("items", []),
                import_as=payload.get("import_as", args.import_as),
                root_title=payload.get("root_title", args.root_title),
                root_body=payload.get("root_body"),
                root_priority=payload.get("root_priority", 1),
                root_due_date=payload.get("root_due_date"),
                parent_id=payload.get("parent_id"),
                auto_schedule_start_date=payload.get("auto_schedule_start_date", args.start_date),
                days_per_item=payload.get("days_per_item", args.days_per_item)
            )
        print(json.dumps(res, ensure_ascii=False, indent=2))
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
