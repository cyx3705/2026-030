#!/usr/bin/env python3
"""Content-addressed prefix cache with weighted-LFU eviction.

The cache stores opaque payloads (for example tokenized Codex prefixes or a
runtime-specific prompt-cache artifact).  It deliberately does not inspect
or log the payload.  A caller must use ``get``/``touch`` for every reuse so
the usage weight is incremented before the entry is returned to the caller.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import shutil
import sqlite3
import sys
import tempfile
import time


from service_root import discover_root

ROOT = discover_root()
CACHE_ROOT = ROOT / "prefix-cache"
OBJECTS = CACHE_ROOT / "objects"
DB_PATH = CACHE_ROOT / "index.sqlite3"
CONFIG_PATH = CACHE_ROOT / "config.json"

# 40 GB in the storage vendor's decimal units.  This fits the current
# partition while leaving the configured reserve for the model/runtime.
MAX_BYTES = 40_000_000_000
RESERVE_FREE_BYTES = 2_000_000_000


def now() -> float:
    return time.time()


def ensure_layout() -> None:
    os.umask(0o077)
    CACHE_ROOT.mkdir(mode=0o700, parents=True, exist_ok=True)
    OBJECTS.mkdir(mode=0o700, parents=True, exist_ok=True)
    try:
        CACHE_ROOT.chmod(0o700)
        OBJECTS.chmod(0o700)
    except OSError:
        pass
    if not CONFIG_PATH.exists():
        CONFIG_PATH.write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "cache_kind": "codex_prefix",
                    "root": str(CACHE_ROOT),
                    "objects": str(OBJECTS),
                    "max_bytes": MAX_BYTES,
                    "max_size_label": "40 GB",
                    "reserve_free_bytes": RESERVE_FREE_BYTES,
                    "eviction": {
                        "policy": "weighted_lfu",
                        "on_use_weight_increment": 1,
                        "tie_breakers": ["oldest_last_used", "largest_size"],
                    },
                    "key": "sha256(model + tokenizer_revision + prefix_bytes)",
                    "payload_policy": "opaque_bytes",
                },
                ensure_ascii=False,
                indent=2,
            )
            + "\n"
        )
        try:
            CONFIG_PATH.chmod(0o600)
        except OSError:
            pass


def connect() -> sqlite3.Connection:
    ensure_layout()
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode=WAL")
    conn.execute("PRAGMA synchronous=NORMAL")
    conn.execute(
        """
        CREATE TABLE IF NOT EXISTS entries (
            key TEXT PRIMARY KEY,
            model TEXT NOT NULL,
            tokenizer_revision TEXT NOT NULL,
            prefix_sha256 TEXT NOT NULL,
            prefix_bytes INTEGER NOT NULL,
            payload_path TEXT NOT NULL,
            payload_bytes INTEGER NOT NULL,
            weight INTEGER NOT NULL DEFAULT 1,
            hits INTEGER NOT NULL DEFAULT 1,
            created_at REAL NOT NULL,
            last_used REAL NOT NULL,
            metadata_json TEXT NOT NULL DEFAULT '{}'
        )
        """
    )
    conn.commit()
    try:
        DB_PATH.chmod(0o600)
    except OSError:
        pass
    return conn


def cache_key(model: str, tokenizer: str, prefix: bytes) -> tuple[str, str]:
    prefix_hash = hashlib.sha256(prefix).hexdigest()
    h = hashlib.sha256()
    for value in (model.encode(), tokenizer.encode(), prefix):
        h.update(len(value).to_bytes(8, "big"))
        h.update(value)
    return h.hexdigest(), prefix_hash


def total_bytes(conn: sqlite3.Connection) -> int:
    row = conn.execute("SELECT COALESCE(SUM(payload_bytes), 0) AS n FROM entries").fetchone()
    return int(row["n"]) + runtime_slot_bytes()


def runtime_slot_bytes() -> int:
    directory = CACHE_ROOT / "runtime-slots"
    return sum(p.stat().st_size for p in directory.glob("*") if p.is_file())


def stats_dict() -> dict:
    conn = connect()
    row = conn.execute(
        "SELECT COUNT(*) AS entries, COALESCE(SUM(payload_bytes), 0) AS bytes, COALESCE(SUM(hits), 0) AS hits FROM entries"
    ).fetchone()
    usage = shutil.disk_usage(CACHE_ROOT)
    result = {
        "root": str(CACHE_ROOT),
        "max_bytes": MAX_BYTES,
        "used_bytes": int(row["bytes"]) + runtime_slot_bytes(),
        "text_payload_bytes": int(row["bytes"]),
        "kv_snapshot_bytes": runtime_slot_bytes(),
        "entries": int(row["entries"]),
        "hits": int(row["hits"]),
        "weight_policy": "weighted_lfu",
        "free_bytes_on_filesystem": usage.free,
        "reserve_free_bytes": RESERVE_FREE_BYTES,
    }
    conn.close()
    return result


def row_json(row: sqlite3.Row) -> dict:
    result = dict(row)
    result["payload_path"] = str(CACHE_ROOT / result["payload_path"])
    return result


def remove_row(conn: sqlite3.Connection, row: sqlite3.Row) -> int:
    path = CACHE_ROOT / row["payload_path"]
    try:
        path.unlink()
    except FileNotFoundError:
        pass
    conn.execute("DELETE FROM entries WHERE key = ?", (row["key"],))
    return int(row["payload_bytes"])


def evict_for(conn: sqlite3.Connection, incoming: int, protected: str | None = None) -> list[dict]:
    """Evict lowest-weight entries until quota and the free-space reserve hold."""
    removed: list[dict] = []
    while True:
        current = total_bytes(conn)
        free = shutil.disk_usage(CACHE_ROOT).free
        quota_ok = current + incoming <= MAX_BYTES
        space_ok = free >= incoming + RESERVE_FREE_BYTES
        if quota_ok and space_ok:
            break
        rows = conn.execute(
            """
            SELECT * FROM entries
            WHERE key != ?
            ORDER BY weight ASC, last_used ASC, payload_bytes DESC
            """,
            (protected or "",),
        ).fetchall()
        if not rows:
            break
        row = rows[0]
        removed.append(row_json(row))
        remove_row(conn, row)
        conn.commit()
    return removed


def put(args: argparse.Namespace) -> int:
    prefix = pathlib.Path(args.prefix_file).read_bytes()
    payload_path = pathlib.Path(args.payload_file)
    payload_size = payload_path.stat().st_size
    key, prefix_hash = cache_key(args.model, args.tokenizer_revision, prefix)
    conn = connect()
    old = conn.execute("SELECT * FROM entries WHERE key = ?", (key,)).fetchone()
    if old:
        remove_row(conn, old)
        conn.commit()
    removed = evict_for(conn, payload_size)
    free = shutil.disk_usage(CACHE_ROOT).free
    if free < payload_size + RESERVE_FREE_BYTES or total_bytes(conn) + payload_size > MAX_BYTES:
        conn.close()
        raise RuntimeError("insufficient free space after weighted eviction")

    rel = pathlib.Path("objects") / key[:2] / key[2:4] / f"{key}.bin"
    destination = CACHE_ROOT / rel
    destination.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=f".{key}.", dir=str(destination.parent))
    try:
        with os.fdopen(fd, "wb") as out, payload_path.open("rb") as src:
            shutil.copyfileobj(src, out, length=1024 * 1024)
            out.flush()
            os.fsync(out.fileno())
        os.replace(temporary, destination)
    finally:
        try:
            os.unlink(temporary)
        except FileNotFoundError:
            pass
    timestamp = now()
    conn.execute(
        """
        INSERT INTO entries(
            key, model, tokenizer_revision, prefix_sha256, prefix_bytes,
            payload_path, payload_bytes, weight, hits, created_at, last_used,
            metadata_json
        ) VALUES (?, ?, ?, ?, ?, ?, ?, 1, 1, ?, ?, ?)
        """,
        (
            key,
            args.model,
            args.tokenizer_revision,
            prefix_hash,
            len(prefix),
            str(rel),
            payload_size,
            timestamp,
            timestamp,
            json.dumps({"kind": args.kind}, ensure_ascii=False),
        ),
    )
    conn.commit()
    result = {"status": "stored", "key": key, "weight": 1, "payload_bytes": payload_size, "evicted": removed}
    print(json.dumps(result, ensure_ascii=False, indent=2))
    conn.close()
    return 0


def record_bytes(model: str, tokenizer_revision: str, prefix: bytes, kind: str = "codex-prefix") -> dict:
    """Record one prefix use, storing the prefix bytes on first sight.

    This is the small in-process API used by the local-only HTTP proxy.  A
    repeated key is a hit and increments its weight without rewriting data.
    """
    key, prefix_hash = cache_key(model, tokenizer_revision, prefix)
    conn = connect()
    row = conn.execute("SELECT * FROM entries WHERE key = ?", (key,)).fetchone()
    if row and (CACHE_ROOT / row["payload_path"]).is_file():
        timestamp = now()
        conn.execute(
            "UPDATE entries SET weight = weight + 1, hits = hits + 1, last_used = ? WHERE key = ?",
            (timestamp, key),
        )
        conn.commit()
        current = conn.execute("SELECT * FROM entries WHERE key = ?", (key,)).fetchone()
        result = {"status": "hit", "key": key, "weight": current["weight"], "hits": current["hits"]}
        conn.close()
        return result
    if row:
        remove_row(conn, row)
        conn.commit()
    removed = evict_for(conn, len(prefix))
    free = shutil.disk_usage(CACHE_ROOT).free
    if free < len(prefix) + RESERVE_FREE_BYTES or total_bytes(conn) + len(prefix) > MAX_BYTES:
        conn.close()
        return {"status": "skipped_no_space", "key": key, "evicted": len(removed)}
    rel = pathlib.Path("objects") / key[:2] / key[2:4] / f"{key}.bin"
    destination = CACHE_ROOT / rel
    destination.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=f".{key}.", dir=str(destination.parent))
    try:
        with os.fdopen(fd, "wb") as out:
            out.write(prefix)
            out.flush()
            os.fsync(out.fileno())
        os.replace(temporary, destination)
    finally:
        try:
            os.unlink(temporary)
        except FileNotFoundError:
            pass
    timestamp = now()
    conn.execute(
        """
        INSERT INTO entries(
            key, model, tokenizer_revision, prefix_sha256, prefix_bytes,
            payload_path, payload_bytes, weight, hits, created_at, last_used,
            metadata_json
        ) VALUES (?, ?, ?, ?, ?, ?, ?, 1, 1, ?, ?, ?)
        """,
        (
            key,
            model,
            tokenizer_revision,
            prefix_hash,
            len(prefix),
            str(rel),
            len(prefix),
            timestamp,
            timestamp,
            json.dumps({"kind": kind}, ensure_ascii=False),
        ),
    )
    conn.commit()
    conn.close()
    return {"status": "stored", "key": key, "weight": 1, "hits": 1, "evicted": len(removed)}


def use(args: argparse.Namespace) -> int:
    conn = connect()
    row = conn.execute("SELECT * FROM entries WHERE key = ?", (args.key,)).fetchone()
    if not row:
        print(json.dumps({"status": "miss", "key": args.key}, ensure_ascii=False))
        conn.close()
        return 1
    timestamp = now()
    conn.execute(
        "UPDATE entries SET weight = weight + 1, hits = hits + 1, last_used = ? WHERE key = ?",
        (timestamp, args.key),
    )
    conn.commit()
    row = conn.execute("SELECT * FROM entries WHERE key = ?", (args.key,)).fetchone()
    print(json.dumps({"status": "hit", **row_json(row)}, ensure_ascii=False, indent=2))
    conn.close()
    return 0


def evict(args: argparse.Namespace) -> int:
    conn = connect()
    removed = evict_for(conn, 0)
    conn.commit()
    print(json.dumps({"status": "evicted", "count": len(removed), "entries": removed}, ensure_ascii=False, indent=2))
    conn.close()
    return 0


def remove(args: argparse.Namespace) -> int:
    conn = connect()
    row = conn.execute("SELECT * FROM entries WHERE key = ?", (args.key,)).fetchone()
    if not row:
        print(json.dumps({"status": "missing", "key": args.key}, ensure_ascii=False))
        conn.close()
        return 1
    bytes_removed = remove_row(conn, row)
    conn.commit()
    print(json.dumps({"status": "removed", "key": args.key, "payload_bytes": bytes_removed}, ensure_ascii=False))
    conn.close()
    return 0


def stats(args: argparse.Namespace) -> int:
    print(json.dumps(stats_dict(), ensure_ascii=False, indent=2))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("put", help="store an opaque prefix-cache payload")
    p.add_argument("--model", required=True)
    p.add_argument("--tokenizer-revision", default="unknown")
    p.add_argument("--prefix-file", required=True)
    p.add_argument("--payload-file", required=True)
    p.add_argument("--kind", default="opaque")
    p.set_defaults(func=put)
    p = sub.add_parser("use", aliases=["get", "touch"], help="record one use and return the payload path")
    p.add_argument("key")
    p.set_defaults(func=use)
    p = sub.add_parser("evict", help="apply quota/free-space eviction")
    p.set_defaults(func=evict)
    p = sub.add_parser("remove", help="remove one cache entry")
    p.add_argument("key")
    p.set_defaults(func=remove)
    p = sub.add_parser("stats", help="show cache accounting")
    p.set_defaults(func=stats)
    args = parser.parse_args()
    try:
        return args.func(args)
    except Exception as exc:
        print(f"prefix-cache: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
