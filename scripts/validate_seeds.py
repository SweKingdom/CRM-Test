#!/usr/bin/env python3
"""Validate the seed SQL files. Usage: validate_seeds.py [seed_dir]  (default: Seed).

Exit 0 when valid; exit 1 and print `file:line reason` per problem otherwise.
Reference-data files (name contains "reference") must use ON CONFLICT ... DO UPDATE;
all other files must use DO NOTHING.
"""
import re
import sys
from pathlib import Path

EXPECTED_ROWS = {
    "stage": 7,
    "meddpicc_element": 8,
    "call_outcome": 9,
    "time_type": 11,
    "country": 6,
    "currency": 6,
}
FILE_RE = re.compile(r"^(\d{4})_[a-z0-9_]+\.sql$")
INSERT_RE = re.compile(r"^INSERT\s+INTO\s+([\w.\"]+)", re.I)
CONFLICT_RE = re.compile(r"\bON\s+CONFLICT\b\s*(?:\([^)]*\))?\s*DO\s+(UPDATE|NOTHING)\b", re.I)
FORBIDDEN_RE = re.compile(r"^(DELETE|DROP|TRUNCATE)\b", re.I)
EMAIL_RE = re.compile(r"[\w.+-]+@([\w-]+(?:\.[\w-]+)+)")
PHONE_RE = re.compile(r"\+\d[\d ()-]{7,}\d")


def statements(text):
    """Return [(line, sql)] with comments removed and string literals kept."""
    out, buf, line, start, quote, i = [], [], 1, None, False, 0
    while i < len(text):
        c = text[i]
        if not quote and text.startswith("--", i):
            j = text.find("\n", i)
            i = len(text) if j < 0 else j
            continue
        if c == "\n":
            line += 1
        if c == "'":
            quote = not quote
        if not quote and c == ";":
            if start is not None:
                out.append((start, "".join(buf).strip()))
            buf, start = [], None
            i += 1
            continue
        if start is None and not c.isspace():
            start = line
        buf.append(c)
        i += 1
    if start is not None and "".join(buf).strip():
        out.append((start, "".join(buf).strip()))
    return out


def count_tuples(sql):
    """Count the row tuples after VALUES (parentheses inside strings are ignored)."""
    m = re.search(r"\bVALUES\b", sql, re.I)
    if not m:
        return 0
    depth = rows = 0
    quote = False
    for c in sql[m.end():]:
        if c == "'":
            quote = not quote
        elif quote:
            continue
        elif c == "(":
            rows += depth == 0
            depth += 1
        elif c == ")":
            depth -= 1
        elif depth == 0 and c.isalpha():
            break
    return rows


def validate(directory):
    errors, counts, first = [], {}, {}
    files = sorted(Path(directory).glob("*.sql"))
    if not files:
        return [f"{directory}:1 no seed files found"]
    for n, path in enumerate(files, start=1):
        name = path.name
        m = FILE_RE.match(name)
        if not m:
            errors.append(f"{name}:1 file name must look like NNNN_name.sql")
        elif int(m.group(1)) != n:
            errors.append(f"{name}:1 file numbers must be sequential from 0001 (expected {n:04d})")
        text = path.read_text(encoding="utf-8")
        for lineno, raw in enumerate(text.splitlines(), start=1):
            code = raw.split("--", 1)[0]
            for email in EMAIL_RE.finditer(code):
                if email.group(1).lower() != "example.com":
                    errors.append(f"{name}:{lineno} email address outside example.com")
            if PHONE_RE.search(code):
                errors.append(f"{name}:{lineno} phone number found; use no real personal data")
        is_reference = "reference" in name
        for lineno, sql in statements(text):
            if FORBIDDEN_RE.match(sql):
                errors.append(f"{name}:{lineno} DELETE/DROP/TRUNCATE is not allowed in seed files")
                continue
            ins = INSERT_RE.match(sql)
            if not ins:
                continue
            conflict = CONFLICT_RE.search(sql)
            if not conflict:
                errors.append(f"{name}:{lineno} INSERT without ON CONFLICT")
                continue
            action = conflict.group(1).upper()
            if is_reference and action != "UPDATE":
                errors.append(f"{name}:{lineno} reference data INSERT must use DO UPDATE, not DO NOTHING")
            elif not is_reference and action != "NOTHING":
                errors.append(f"{name}:{lineno} demo/volume data INSERT must use DO NOTHING, not DO UPDATE")
            table = ins.group(1).replace('"', "").split(".")[-1].lower()
            if table in EXPECTED_ROWS:
                counts[table] = counts.get(table, 0) + count_tuples(sql)
                first.setdefault(table, (name, lineno))
    for table, rows in counts.items():
        if rows != EXPECTED_ROWS[table]:
            name, lineno = first[table]
            errors.append(f"{name}:{lineno} {table} has {rows} rows, expected {EXPECTED_ROWS[table]}")
    return errors


def main():
    directory = sys.argv[1] if len(sys.argv) > 1 else "Seed"
    errors = validate(directory)
    for error in errors:
        print(error, file=sys.stderr)
    if errors:
        return 1
    print(f"OK: seed files in {directory} are valid")
    return 0


if __name__ == "__main__":
    sys.exit(main())
