#!/usr/bin/env python3
"""Persist llama-server slot state as side-by-side forks, within 40 GB.

The GPU still has one live slot. Disk keeps a separate fork when a saved
prompt is not a continuation of an existing one. A request loads the fork
with the longest matching prefix instead of whichever file was written last.
"""
from __future__ import annotations

import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import time
import urllib.request
import urllib.error
import uuid

from service_root import discover_root

ROOT = discover_root()
CACHE = ROOT / "prefix-cache"
SLOTS = CACHE / "runtime-slots"
MANIFEST = SLOTS / "manifest.json"
STATUS = ROOT / "status" / "kv-disk-cache.json"
UPSTREAM = os.environ.get("LOCAL_AI_UPSTREAM", "http://127.0.0.1:18769")
LIMIT = 40_000_000_000
RESERVE = 2_000_000_000
POLICY = ROOT / "config" / "kv-cache-policy.json"
# Restoring a fork is worth it only when it covers this many more tokens than
# the slot already has. Smaller gaps stay on disk until a request needs them.
FORK_SWITCH_MIN_TOKENS = 1024


def policy():
    return json.loads(POLICY.read_text())


def sync_directory(path):
    # drvfs may not implement directory fsync. File data is always fsynced.
    try:
        descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    except OSError:
        pass


def api(path, body=None, timeout=30):
    request = urllib.request.Request(
        UPSTREAM + path,
        data=None if body is None else json.dumps(body).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.load(response)


def atomic_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    with temporary.open("w") as out:
        json.dump(value, out, ensure_ascii=False, indent=2)
        out.flush()
        os.fsync(out.fileno())
    temporary.replace(path)
    sync_directory(path.parent)


def load_manifest():
    manifest = {"version": 1, "snapshots": []}
    for path in [MANIFEST, MANIFEST.with_suffix(".backup.json")]:
        try:
            value = json.loads(path.read_text())
            if isinstance(value.get("snapshots"), list):
                manifest = value
                break
        except (OSError, ValueError, TypeError, AttributeError):
            continue
    # Each committed binary has independently durable metadata. A power failure
    # between publishing its sidecar and the index cannot lose that snapshot.
    indexed = {s["filename"]: s for s in manifest["snapshots"]}
    for path in SLOTS.glob("kv-*.bin.json"):
        try:
            item = json.loads(path.read_text())
            if path.name == item["filename"] + ".json":
                item.pop("token_prefix", None)
                indexed.setdefault(item["filename"], item)
        except (OSError, ValueError, KeyError, TypeError):
            continue
    manifest["snapshots"] = sorted(
        (s for s in indexed.values() if Path(s["filename"]).name == s["filename"]
         and (SLOTS / s["filename"]).is_file()), key=lambda s: s["saved_at"])
    return manifest


def commit_manifest(manifest):
    if MANIFEST.is_file():
        try:
            previous = json.loads(MANIFEST.read_text())
            if isinstance(previous.get("snapshots"), list):
                atomic_json(MANIFEST.with_suffix(".backup.json"), previous)
        except (OSError, ValueError, AttributeError):
            pass
    atomic_json(MANIFEST, manifest)


def capacity_independent_identity(identity):
    """Only allocation capacity may differ; weights, RoPE and KV types must match."""
    result = dict(identity)
    result.pop("n_ctx", None)
    args = iter(identity["argv"])
    result["argv"] = []
    for argument in args:
        if argument in {"-c", "--ctx-size"}:
            next(args, None)
        elif not argument.startswith("--ctx-size="):
            result["argv"].append(argument)
    return result


def runtime(details=False):
    """Stable compatibility fingerprint and a separate process incarnation."""
    props = api("/props")
    pid = None
    argv = []
    for entry in Path("/proc").iterdir():
        if not entry.name.isdigit():
            continue
        try:
            args = (entry / "cmdline").read_bytes().decode().strip("\0").split("\0")
            if args and Path(args[0]).name == "llama-server" and "--port" in args:
                if args[args.index("--port") + 1] == "18769":
                    pid, argv = int(entry.name), args
                    break
        except (OSError, ValueError, UnicodeError, IndexError):
            continue
    if pid is None:
        raise RuntimeError("Cannot identify the local llama-server process")
    identity = {
        "argv": [str(Path(a).resolve()) if a.startswith("/") else a for a in argv],
        "n_ctx": props["default_generation_settings"]["n_ctx"],
        "template": props.get("chat_template"),
    }
    for label, path in [("model", props["model_path"]), ("runtime", argv[0])]:
        stat = Path(path).stat()
        identity[label] = [str(Path(path).resolve()), stat.st_size, stat.st_mtime_ns]
    # Shared libraries determine the state serialization ABI too.
    identity["libraries"] = sorted(
        (p.name, p.stat().st_size, p.stat().st_mtime_ns)
        for p in Path(argv[0]).parent.glob("libllama*.so*") if p.is_file()
    )
    fingerprint = hashlib.sha256(json.dumps(identity, sort_keys=True).encode()).hexdigest()
    capacity_fingerprint = hashlib.sha256(json.dumps(
        capacity_independent_identity(identity), sort_keys=True).encode()).hexdigest()
    boot = Path('/proc/sys/kernel/random/boot_id').read_text().strip()
    incarnation = f"{boot}:{pid}:{Path(f'/proc/{pid}/stat').read_text().split()[21]}"
    if details:
        return {"fingerprint": fingerprint, "incarnation": incarnation,
                "capacity_fingerprint": capacity_fingerprint,
                "context_capacity": identity["n_ctx"]}
    return fingerprint, incarnation


def total_bytes():
    return sum(p.stat().st_size for p in CACHE.rglob("*") if p.is_file())


def file_digest(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(4 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def state_header(path):
    with path.open("rb") as source:
        header = source.read(12)
    if len(header) != 12:
        raise ValueError("Truncated sequence state header")
    magic, version, packed_tokens = struct.unpack("<III", header)
    if magic != 0x67677371 or version != 4 or not 0 < packed_tokens <= 1_000_000:
        raise ValueError("Unsupported sequence state header")
    if path.stat().st_size <= 12 + packed_tokens * 4:
        raise ValueError("Truncated sequence state payload")
    return {"version": version, "packed_tokens": packed_tokens}


def read_token_prefix(path):
    """Prompt token ids at the front of a slot save.

    llama-server stores the slot's token tracker as a byte blob padded to
    token width: a null marker, a version, then the real token list. Comparing
    the raw blob makes every save look different at the third integer.
    """
    try:
        with path.open("rb") as source:
            header = source.read(12)
            if len(header) != 12:
                return []
            magic, version, count = struct.unpack("<III", header)
            if magic != 0x67677371 or version != 4 or not 0 < count <= 1_000_000:
                return []
            raw = source.read(count * 4)
    except OSError:
        return []
    if len(raw) != count * 4:
        return []
    words = list(struct.unpack(f"<{count}i", raw))
    if len(words) >= 3 and words[0] == -1 and words[1] == 1:
        n_tokens = words[2]
        if not 0 < n_tokens <= len(words) - 3:
            return []
        return words[3:3 + n_tokens]
    return words


def common_prefix_len(left, right):
    limit = min(len(left), len(right))
    for index in range(limit):
        if left[index] != right[index]:
            return index
    return limit


def load_token_prefix(item):
    """Prefix recorded beside the snapshot, or read from the save itself."""
    sidecar = SLOTS / (item["filename"] + ".json")
    try:
        stored = json.loads(sidecar.read_text()).get("token_prefix")
        # A list that still starts with the null marker is the wrapped blob.
        if isinstance(stored, list) and stored and stored[0] != -1:
            return stored
    except (OSError, ValueError, AttributeError):
        pass
    tokens = read_token_prefix(SLOTS / item["filename"])
    if not tokens:
        return []
    try:
        previous = json.loads(sidecar.read_text()) if sidecar.is_file() else dict(item)
        if isinstance(previous, dict):
            previous["token_prefix"] = tokens
            atomic_json(sidecar, previous)
    except (OSError, ValueError):
        pass
    return tokens


def fork_tips(snapshots, prefixes):
    """Longest snapshot of each branch. A branch is a pure prefix chain.

    Two saves that share only a head and then differ are separate tips and
    both stay. A later continuation of the same tokens does not create a fork.
    """
    indexed = [item for item in snapshots if prefixes.get(item["filename"])]
    parent = list(range(len(indexed)))

    def find(index):
        while parent[index] != index:
            parent[index] = parent[parent[index]]
            index = parent[index]
        return index

    for left in range(len(indexed)):
        left_tokens = prefixes[indexed[left]["filename"]]
        for right in range(left):
            right_tokens = prefixes[indexed[right]["filename"]]
            if common_prefix_len(left_tokens, right_tokens) == min(len(left_tokens), len(right_tokens)):
                parent[find(left)] = find(right)
    groups = {}
    for index, item in enumerate(indexed):
        groups.setdefault(find(index), []).append(item)
    tips = set()
    for group in groups.values():
        tip = max(group, key=lambda item: (len(prefixes[item["filename"]]), item["saved_at"]))
        tips.add(tip["filename"])
    return tips


def branch_protection(manifest):
    prefixes = {item["filename"]: load_token_prefix(item) for item in manifest["snapshots"]}
    return fork_tips(manifest["snapshots"], prefixes)


def resident_score(resident_tokens, request_tokens, slot_tokens):
    """How many request tokens the live slot can already reuse.

    When the loaded fork is a prefix of the request and the slot is at least
    that long, the slot may have continued that same prompt past the last
    save. A shorter slot has lost that tail and must not block a disk copy.
    """
    matched = common_prefix_len(resident_tokens, request_tokens)
    if (resident_tokens and matched == len(resident_tokens)
            and (slot_tokens or 0) >= len(resident_tokens)):
        return max(matched, slot_tokens or 0)
    if resident_tokens and matched == len(resident_tokens):
        return slot_tokens or 0
    return matched


def select_fork(snapshots, prefixes, request_tokens, resident_tokens, slot_tokens,
                slot_holds_resident=True):
    """Return the disk fork to load, or None when the live slot is already better."""
    if not request_tokens:
        ranked = sorted(snapshots, key=lambda item: (item.get("tokens", 0), item.get("saved_at", 0)), reverse=True)
        return ranked[0] if ranked else None
    # A slot overwritten by another prompt still reports a large token count.
    # That count belongs to the other prompt, so it cannot defend the resident.
    if slot_holds_resident:
        live = resident_score(resident_tokens or [], request_tokens, slot_tokens or 0)
    else:
        live = 0
    # Replacing a populated slot has to win by a wide margin. An empty slot
    # takes the longest match it has, including a short one.
    minimum_gain = FORK_SWITCH_MIN_TOKENS if (slot_tokens or 0) > 0 else 1
    best = None
    best_match = live
    for item in snapshots:
        matched = common_prefix_len(prefixes.get(item["filename"]) or [], request_tokens)
        if matched > best_match and matched - live >= minimum_gain:
            best = item
            best_match = matched
    return best


def remove_snapshot(manifest, item):
    (SLOTS / item["filename"]).unlink(missing_ok=True)
    (SLOTS / (item["filename"] + ".json")).unlink(missing_ok=True)
    manifest["snapshots"].remove(item)
    commit_manifest(manifest)


def retain_tiers(manifest, settings, timestamp=None):
    """Keep recent rollback copies plus the newest snapshot in each time bucket."""
    timestamp = time.time() if timestamp is None else timestamp
    ordered = sorted(manifest["snapshots"], key=lambda s: s["saved_at"], reverse=True)
    groups = {}
    for item in ordered:
        groups.setdefault(item["fingerprint"], []).append(item)
    keep = set()
    for entries in groups.values():
        keep.update(s["filename"] for s in entries[:settings["retain_latest"]])
        buckets = set()
        for item in entries:
            age = max(0, timestamp - item["saved_at"])
            for tier, rule in enumerate(settings["tiers"]):
                if age <= rule["max_age_seconds"]:
                    bucket = (tier, int(item["saved_at"] // rule["bucket_seconds"]))
                    if bucket not in buckets:
                        keep.add(item["filename"])
                        buckets.add(bucket)
                    break
    # Divergent forks stay beside the newest file. Time buckets alone would
    # keep only the latest branch and drop an older prompt that still matches.
    keep.update(branch_protection(manifest))
    for item in list(manifest["snapshots"]):
        if item["filename"] not in keep:
            remove_snapshot(manifest, item)


def prune(manifest, incoming=0, keep=None):
    """Evict only our committed snapshots, never unrelated service data.

    Branch tips are dropped only after redundant copies. The 40 GB budget is
    the limit, not a single live slot.
    """
    entries = manifest["snapshots"]
    protected = branch_protection(manifest)
    for item in sorted(list(entries), key=lambda s: (
            s["filename"] in protected, s.get("hits", 0), s["saved_at"])):
        if total_bytes() + incoming <= LIMIT and shutil.disk_usage(CACHE).free >= incoming + RESERVE:
            break
        if item["filename"] == keep:
            continue
        remove_snapshot(manifest, item)
    if total_bytes() + incoming > LIMIT or shutil.disk_usage(CACHE).free < incoming + RESERVE:
        raise RuntimeError("40 GB cache quota or 2 GB filesystem reserve would be exceeded")


def status(**values):
    try:
        previous = json.loads(STATUS.read_text())
    except (OSError, ValueError):
        previous = {}
    # Preserve the last good snapshot's details while a newer save is queued.
    for key in ["last_saved_at", "last_saved_snapshot", "last_saved_tokens",
                "last_saved_bytes", "resident_snapshot", "snapshot",
                "slot_holds_resident"]:
        if key in previous:
            values.setdefault(key, previous[key])
    values.update(updated_at=time.time(), max_bytes=LIMIT, root=str(SLOTS))
    atomic_json(STATUS, values)
    print(json.dumps(values, ensure_ascii=False), flush=True)


def inference_busy(slots):
    if any(slot['is_processing'] for slot in slots):
        return True
    try:
        native = api('/v1/kv/op', {'op': 'runtime'}, timeout=5)
        return bool(native.get('active_sequences') or native.get('active_branches'))
    except urllib.error.HTTPError as error:
        if error.code in (404, 501):
            return False  # An older provider has no extra sequence IDs.
        raise


def save(wait=False, scheduled=False):
    facts = runtime(details=True)
    fingerprint, incarnation = facts["fingerprint"], facts["incarnation"]
    # llama-server resumes only one deferred task per slot release, and a slot
    # save does not release the slot again. Posting a save while a request is
    # running therefore leaves every later chat completion queued forever
    # while the slot sits idle. Wait here, in this process, and post only once
    # the slot is idle so the save cannot occupy that single resume.
    deadline = time.monotonic() + (7200 if wait else 0)
    while True:
        slots = api("/slots")
        slot = slots[0]
        if not inference_busy(slots):
            break
        if time.monotonic() >= deadline:
            return False
        time.sleep(min(policy()["poll_seconds"], 2))
    if slot.get("n_prompt_tokens", 0) < 64:
        return False
    manifest = load_manifest()
    if scheduled and manifest["snapshots"]:
        if time.time() - manifest["snapshots"][-1]["saved_at"] < policy()["save_interval_seconds"]:
            return False
    latest = next((s for s in reversed(manifest["snapshots"])
                   if s["incarnation"] == incarnation and s["task_id"] == slot["id_task"]), None)
    if latest:
        return False
    # Conservative initial headroom. Actual bytes include hybrid recurrent
    # state and context checkpoints; retained files are checked again afterward.
    estimate = max(1_000_000_000, slot.get("n_prompt_tokens", 0) * 100_000)
    keep = manifest["snapshots"][-1]["filename"] if manifest["snapshots"] else None
    prune(manifest, estimate, keep)
    name = f"kv-{time.time_ns()}-{uuid.uuid4().hex[:8]}.bin"
    partial = name + ".partial"
    status(state="saving", model_incarnation=incarnation, task_id=slot["id_task"])
    path = SLOTS / partial
    try:
        result = api("/slots/0?action=save", {"filename": partial}, timeout=7200)
        if runtime()[1] != incarnation:
            raise RuntimeError("Model process changed during snapshot save")
        if result.get("n_saved", 0) < 64 or result.get("n_written", 0) <= 0:
            raise RuntimeError(f"Slot save did not contain a reusable prompt: {result}")
        # llama-server closes the file before responding. Never publish partial
        # files or evict the last good copy to make room for an invalid save.
        with path.open("rb") as source:
            os.fsync(source.fileno())
        size = path.stat().st_size
        if size != result["n_written"]:
            raise RuntimeError("Saved file length differs from the runtime result")
        header = state_header(path)
        checksum = file_digest(path)
        prune(manifest, incoming=16_384, keep=keep)
    except Exception as error:
        path.unlink(missing_ok=True)
        # The model queue rechecks admission atomically. A new A or public
        # generation can win after our idle probe; retry on the next poll.
        if isinstance(error, urllib.error.HTTPError) and error.code == 400:
            detail = error.read().decode('utf-8', 'replace')
            if 'model_busy_for_snapshot' in detail:
                status(state="deferred_busy", model_incarnation=incarnation)
                return False
        raise
    path.replace(SLOTS / name)
    item = {
        "filename": name, "fingerprint": fingerprint, "incarnation": incarnation,
        "task_id": slot["id_task"], "tokens": result["n_saved"], "bytes": size,
        "saved_at": time.time(), "hits": 0, "sha256": checksum,
        "state_header": header,
        "capacity_fingerprint": facts["capacity_fingerprint"],
        "context_capacity": facts["context_capacity"],
    }
    atomic_json(SLOTS / (name + ".json"), dict(item, token_prefix=read_token_prefix(SLOTS / name)))
    manifest["snapshots"].append(item)
    commit_manifest(manifest)
    retain_tiers(manifest, policy())
    prune(manifest, keep=name)
    status(state="saved", snapshot=name, tokens=result["n_saved"], bytes=size,
           model_incarnation=incarnation, save_result=result, used_bytes=total_bytes(),
           last_saved_at=item["saved_at"], last_saved_snapshot=name,
           last_saved_tokens=item["tokens"], last_saved_bytes=size,
           resident_snapshot=name, slot_holds_resident=True, schedule=policy(),
           retained_snapshots=len(manifest["snapshots"]))
    return True


def compatible_snapshots(manifest, facts):
    return [item for item in manifest["snapshots"]
            if item["tokens"] <= facts["context_capacity"] and (
                item["fingerprint"] == facts["fingerprint"] or
                item.get("capacity_fingerprint") == facts["capacity_fingerprint"])]


def restore(request_tokens=None, replace=False):
    facts = runtime(details=True)
    incarnation = facts["incarnation"]
    slots = api("/slots")
    slot = slots[0]
    # Public slots and native A/B leases share one GPU context. An idle slot may be replaced
    # when another disk fork matches the next prompt by a wide margin.
    if inference_busy(slots):
        return False
    if slot.get("n_prompt_tokens", 0) > 0 and not replace:
        return False
    manifest = load_manifest()
    candidates = compatible_snapshots(manifest, facts)
    prefixes = {item["filename"]: load_token_prefix(item) for item in candidates}
    resident_name = None
    resident_tokens = []
    try:
        current = json.loads(STATUS.read_text())
        resident_name = current.get("resident_snapshot")
        if not resident_name and current.get("state") in {"restored", "saved"}:
            resident_name = current.get("snapshot")
        holds_resident = bool(current.get("slot_holds_resident", False))
    except (OSError, ValueError, AttributeError):
        current = {}
        holds_resident = False
    if resident_name and resident_name in prefixes:
        resident_tokens = prefixes[resident_name]
    # A populated slot with no recorded fork must not be overwritten while it
    # still holds that fork. After another prompt takes the slot, reload.
    if (replace and holds_resident and slot.get("n_prompt_tokens", 0) > 0
            and not resident_tokens):
        return False
    chosen = select_fork(
        candidates, prefixes, request_tokens, resident_tokens, slot.get("n_prompt_tokens", 0),
        slot_holds_resident=holds_resident)
    if chosen is None:
        return False
    if request_tokens:
        ordered = [chosen]
    else:
        ordered = sorted(
            candidates, key=lambda item: (item.get("tokens", 0), item.get("saved_at", 0)), reverse=True)
    for item in ordered:
        path = SLOTS / item["filename"]
        if not path.is_file() or path.stat().st_size != item["bytes"]:
            continue
        if (holds_resident and item.get("restored_into") == incarnation
                and item["filename"] == resident_name):
            continue
        if not replace and item["incarnation"] == incarnation:
            continue
        if item.get("sha256") and file_digest(path) != item["sha256"]:
            status(state="restore_error", snapshot=item["filename"], error="Snapshot checksum mismatch")
            continue
        status(state="restoring", snapshot=item["filename"], model_incarnation=incarnation)
        try:
            result = api("/slots/0?action=restore", {"filename": item["filename"]}, timeout=600)
        except Exception as exc:
            if isinstance(exc, urllib.error.HTTPError) and exc.code == 400:
                detail = exc.read().decode('utf-8', 'replace')
                if 'model_busy_for_snapshot' in detail:
                    status(state="deferred_busy", model_incarnation=incarnation)
                    return False
            status(state="restore_error", snapshot=item["filename"], error=str(exc))
            continue
        if result.get("n_restored", 0) != item["tokens"]:
            raise RuntimeError(f"Unexpected restored token count: {result}")
        item["hits"] += 1
        item["restored_into"] = incarnation
        sidecar_path = SLOTS / (item["filename"] + ".json")
        try:
            sidecar = json.loads(sidecar_path.read_text())
        except (OSError, ValueError):
            sidecar = dict(item)
        sidecar.update(hits=item["hits"], restored_into=incarnation)
        atomic_json(sidecar_path, sidecar)
        commit_manifest(manifest)
        status(state="restored", snapshot=item["filename"], tokens=item["tokens"],
               model_incarnation=incarnation, restore_result=result,
               resident_snapshot=item["filename"], slot_holds_resident=True)
        return True
    return False


def note_slot_outcome(cached_tokens, prompt_tokens):
    """Remember whether the slot still contains the resident fork.

    Reusing the resident means this request cached at least that whole fork.
    A shorter hit is only the shared head of a different prompt; the next
    request must be allowed to load the other fork.
    """
    try:
        cached = int(cached_tokens or 0)
    except (TypeError, ValueError):
        return
    try:
        current = json.loads(STATUS.read_text())
    except (OSError, ValueError):
        current = {}
    resident_tokens = int(current.get("tokens") or current.get("last_saved_tokens") or 0)
    if resident_tokens >= FORK_SWITCH_MIN_TOKENS:
        holds = cached + 64 >= resident_tokens
    else:
        holds = cached >= FORK_SWITCH_MIN_TOKENS
    current["slot_holds_resident"] = holds
    current["updated_at"] = time.time()
    atomic_json(STATUS, current)


def align_request(request_tokens):
    """Load the side-by-side fork that covers the most of this prompt."""
    if not request_tokens:
        return False
    with (SLOTS / "operation.lock").open("a") as lock:
        # A scheduled save can wait for a busy model while holding this lock.
        # Request alignment is an optional cache hint, never an admission gate.
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return False
        return restore(request_tokens=request_tokens, replace=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["save", "restore", "watch", "stats"])
    parser.add_argument("--wait", action="store_true")
    args = parser.parse_args()
    os.umask(0o077)
    SLOTS.mkdir(parents=True, exist_ok=True)
    if args.command == "stats":
        print(json.dumps({"manifest": load_manifest(), "used_bytes": total_bytes(), "max_bytes": LIMIT}))
        return
    # Separate watcher ownership from operation serialization, so service stop
    # can request an explicit final save while the watcher remains installed.
    watch_lock = None
    if args.command == "watch":
        watch_lock = (SLOTS / "watch.lock").open("a")
        try:
            fcntl.flock(watch_lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            return
    while True:
        try:
            with (SLOTS / "operation.lock").open("a") as lock:
                fcntl.flock(lock, fcntl.LOCK_EX)
                if args.command in {"restore", "watch"}:
                    restore()
                if args.command in {"save", "watch"}:
                    # The watcher may block until the slot is idle, then saves.
                    # It must not enqueue a save behind a running request.
                    save(wait=args.wait or args.command == "watch", scheduled=args.command == "watch")
        except Exception as exc:
            status(state="error", error=f"{type(exc).__name__}: {exc}")
            if args.command != "watch":
                raise
        if args.command != "watch":
            return
        time.sleep(policy()["poll_seconds"])


if __name__ == "__main__":
    main()
