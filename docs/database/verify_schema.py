"""Validate the reference SQL in isolated in-memory SQLite databases; no user files move."""
from pathlib import Path
import sqlite3
import unittest

SCHEMA = Path(__file__).with_name('001_initial.sql').read_text(encoding='utf-8')


class SchemaTests(unittest.TestCase):
    def setUp(self):
        self.db = sqlite3.connect(':memory:')
        self.db.executescript(SCHEMA)

    def tearDown(self):
        self.db.close()

    def add(self, identifier, **fields):
        row = dict(id=identifier, kind=0, title=identifier,
                   created_utc_ms=1, updated_utc_ms=1)
        row.update(fields)
        columns = ','.join(row)
        marks = ','.join('?' for _ in row)
        self.db.execute(f'INSERT INTO records({columns}) VALUES({marks})', tuple(row.values()))

    def project(self, identifier, priority=2, boundary=None):
        self.db.execute('''INSERT INTO effective_records
            (record_id,priority,due_kind,due_utc_ms,due_zone,due_boundary_utc_ms,
             projection_zone,record_version) VALUES(?,?,?,?,?,?,?,?)''',
            (identifier, priority, 0 if boundary is None else 2, boundary,
             None if boundary is None else 'Asia/Shanghai', boundary, 'Asia/Shanghai', 1))

    def test_schema_and_foreign_keys(self):
        self.assertEqual(self.db.execute('PRAGMA integrity_check').fetchone()[0], 'ok')
        self.assertEqual(self.db.execute('PRAGMA foreign_keys').fetchone()[0], 1)
        self.assertEqual(self.db.execute('SELECT version FROM schema_migrations').fetchone()[0], 1)

    def test_blank_record_rejected(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.add('blank', title='', body='')

    def test_child_parent_constraints(self):
        self.add('parent')
        self.add('child', parent_id='parent', inherit_mask=63)
        for identifier, fields in [
            ('grandchild', dict(parent_id='child')),
            ('self', dict(parent_id='self')),
            ('orphan', dict(parent_id='unknown')),
            ('notechild', dict(parent_id='parent', kind=1))]:
            with self.subTest(identifier=identifier), self.assertRaises(sqlite3.IntegrityError):
                self.add(identifier, **fields)

    def test_note_cannot_be_parent(self):
        self.add('note', kind=1)
        with self.assertRaises(sqlite3.IntegrityError):
            self.add('child', parent_id='note')

    def test_identity_cannot_be_changed(self):
        self.add('root')
        self.add('child', parent_id='root')
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("UPDATE records SET parent_id=NULL WHERE id='child'")

    def test_completion_and_reopen_order(self):
        self.add('parent')
        self.add('child', parent_id='parent')
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("UPDATE records SET status=1,closed_utc_ms=2 WHERE id='parent'")
        self.db.execute("UPDATE records SET status=1,closed_utc_ms=2 WHERE id='child'")
        self.db.execute("UPDATE records SET status=1,closed_utc_ms=2 WHERE id='parent'")
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("UPDATE records SET status=0,closed_utc_ms=NULL WHERE id='child'")
        self.db.execute("UPDATE records SET status=0,closed_utc_ms=NULL WHERE id='parent'")
        self.db.execute("UPDATE records SET status=0,closed_utc_ms=NULL WHERE id='child'")

    def test_explicit_none_distinct_from_inherited_due(self):
        self.add('p', due_kind=2, due_utc_ms=1000, due_zone='Asia/Shanghai')
        self.add('follow', parent_id='p', inherit_mask=63)
        self.add('none', parent_id='p', inherit_mask=47)
        masks = dict(self.db.execute('SELECT id,inherit_mask FROM records WHERE parent_id=?', ('p',)))
        self.assertEqual(masks['follow'] & 16, 16)
        self.assertEqual(masks['none'] & 16, 0)
        # Actual dynamic resolution is an application-layer test, not proved here.

    def test_due_group_constraints(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.add('bad-date', due_kind=1)
        with self.assertRaises(sqlite3.IntegrityError):
            self.add('bad-time', due_kind=2, due_utc_ms=1000)
        self.add('date', due_kind=1, due_date='2026-10-25')

    def test_literal_search(self):
        rows = [('a','合同审批'),('b','合 同'),('c','协议'),('d','50%_O\'Reilly'),
                ('e','Contract'),('f','前' * 40 + '合同')]
        for identifier, body in rows:
            self.add(identifier, body=body)
            self.project(identifier)
        def search(query):
            return [r[0] for r in self.db.execute('''SELECT id FROM record_search
                WHERE deleted_utc_ms IS NULL AND kind=0 AND status=0
                AND (?='' OR instr(title,?)>0 OR instr(body,?)>0
                     OR instr(coalesce(publisher,''),?)>0 OR instr(coalesce(assignee,''),?)>0)
                ORDER BY id''', (query,) * 5)]
        self.assertEqual(search('合同'), ['a','f'])
        self.assertEqual(search('%_'), ['d'])
        self.assertEqual(search("O'Reilly"), ['d'])
        self.assertEqual(search('contract'), [])
        self.assertEqual(len(search('')), 6)

    def test_default_order(self):
        for identifier, priority, boundary in [('a',0,None),('b',1,100),('c',0,200),('d',0,200)]:
            self.add(identifier)
            self.project(identifier, priority, boundary)
        actual = [r[0] for r in self.db.execute('''SELECT id FROM record_search
            ORDER BY priority ASC, (due_boundary_utc_ms IS NULL) ASC,
            due_boundary_utc_ms ASC, created_utc_ms ASC, id ASC''')]
        self.assertEqual(actual, ['c','d','a','b'])

    def test_projection_does_not_silently_follow_parent_sql(self):
        self.add('p', assignee='A')
        self.add('c', parent_id='p', inherit_mask=63)
        self.project('p')
        self.project('c')
        self.db.execute("UPDATE effective_records SET assignee='A'")
        self.db.execute("UPDATE records SET assignee='B',version=2 WHERE id='p'")
        self.assertEqual(self.db.execute("SELECT assignee FROM record_search WHERE id='c'").fetchone()[0], 'A')
        # Intentional: service must recompute projections in the same write transaction.

    def test_parent_delete_restricted(self):
        self.add('p')
        self.add('c', parent_id='p')
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("DELETE FROM records WHERE id='p'")

    def test_reminder_idempotency(self):
        self.add('p')
        row = ('n1','p','due:1000','before','1000','claimed',900)
        self.db.execute('INSERT INTO reminder_deliveries VALUES(?,?,?,?,?,?,?)', row)
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute('INSERT INTO reminder_deliveries VALUES(?,?,?,?,?,?,?)', ('n2',) + row[1:])

    def test_bad_json_rejected(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("INSERT INTO settings(key,value_json) VALUES('theme','not json')")


if __name__ == '__main__':
    unittest.main(verbosity=2)
