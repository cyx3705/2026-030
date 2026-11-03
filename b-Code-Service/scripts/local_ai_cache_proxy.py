#!/usr/bin/env python3
"""Local llama-server proxy: stable prefixes, immediate SSE, cache diagnostics.

The on-disk text index is accounting, not a KV cache. llama-server owns the
actual token/state cache. No raw prompts or response text enter diagnostics.
"""

from __future__ import annotations

import copy
import http.client
import http.server
import json
import os
import pathlib
import re
import select
import socket
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request


SCRIPT_DIR = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(SCRIPT_DIR))
from service_root import discover_root

ROOT = discover_root()
import prefix_cache  # noqa: E402


UPSTREAM = os.environ.get("LOCAL_AI_UPSTREAM", "http://127.0.0.1:18769")
UPSTREAM_TIMEOUT_SECONDS = int(os.environ.get("LOCAL_AI_UPSTREAM_TIMEOUT_SECONDS", "1800"))
# A real prefill keeps the slot processing and may emit no tokens for minutes.
# An idle slot with a silent request is the false "still running" state.
IDLE_STALL_SECONDS = float(os.environ.get("LOCAL_AI_IDLE_STALL_SECONDS", "12"))
LIVENESS_POLL_SECONDS = float(os.environ.get("LOCAL_AI_LIVENESS_POLL_SECONDS", "0.25"))
WAKE_CONTENT = "liveness-wake"
IDLE_ERROR = "本地模型没有在计算这个请求，调用已失败，不会继续显示为正在运行。"
LISTEN_HOST = "127.0.0.1"
LISTEN_PORT = 11435
MIN_PREFIX_BYTES = 1024
METRICS_PATH = ROOT / "linux-cache" / "local-ai-request-metrics.json"
METRICS_LOCK = threading.Lock()
try:
    RECENT_REQUESTS = json.loads(METRICS_PATH.read_text())[-100:]
except (OSError, ValueError, TypeError):
    RECENT_REQUESTS = []
HOP_HEADERS = {
    "connection",
    "keep-alive",
    "proxy-authenticate",
    "proxy-authorization",
    "te",
    "trailers",
    "transfer-encoding",
    "upgrade",
    "host",
    "content-length",
}


def upstream_json(path: str, payload: dict) -> dict:
    request = urllib.request.Request(
        UPSTREAM.rstrip("/") + path,
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        parsed = json.loads(response.read().decode())
    if not isinstance(parsed, dict):
        raise ValueError("upstream json")
    return parsed


def align_fork(body: dict) -> None:
    """Load the disk fork whose saved tokens cover the most of this chat.

    Tokenizing stays on llama-server's HTTP thread and does not take the GPU
    slot. A busy slot, a short gain, or any lookup failure leaves the request
    on whatever context is already loaded.
    """
    try:
        import kv_disk_cache
        if "prompt" in body and "messages" not in body:
            prompt = body["prompt"]
            if isinstance(prompt, list) and prompt and all(type(t) is int for t in prompt):
                kv_disk_cache.align_request(prompt)
                return
            add_special = True
        else:
            rendered = upstream_json("/apply-template", body)
            prompt = rendered.get("prompt")
            add_special = False
        if not isinstance(prompt, str) or not prompt:
            return
        tokenized = upstream_json("/tokenize", {
            "content": prompt, "add_special": add_special, "parse_special": True,
        })
        tokens = tokenized.get("tokens")
        if isinstance(tokens, list) and tokens and all(isinstance(item, int) for item in tokens):
            kv_disk_cache.align_request(tokens)
    except Exception as exc:
        print(f"fork align skipped: {type(exc).__name__}: {exc}", flush=True)
        return


def text_value(value) -> str:
    if isinstance(value, str):
        return value
    if isinstance(value, list):
        parts = []
        for item in value:
            if isinstance(item, str):
                parts.append(item)
            elif isinstance(item, dict) and isinstance(item.get("text"), str):
                parts.append(item["text"])
        return "\n".join(parts)
    return ""


def stable_prefix(body: dict) -> bytes:
    """Return canonical stable prompt material without user/tool outputs."""
    prefix: dict = {}
    instructions = body.get("instructions")
    if instructions:
        prefix["instructions"] = instructions

    stable_messages = []
    messages = body.get("messages")
    if isinstance(messages, list):
        for item in messages:
            if isinstance(item, dict) and item.get("role") in {"system", "developer"}:
                stable_messages.append(
                    {"role": item.get("role"), "content": item.get("content", "")}
                )
    inputs = body.get("input")
    if isinstance(inputs, list):
        for item in inputs:
            if isinstance(item, dict) and item.get("role") in {"system", "developer"}:
                stable_messages.append(
                    {"role": item.get("role"), "content": item.get("content", "")}
                )
    elif isinstance(inputs, str) and instructions:
        # A bare user input is intentionally excluded from the stable prefix.
        pass
    if stable_messages:
        prefix["messages"] = stable_messages
    if body.get("tools"):
        prefix["tools"] = body["tools"]
    if body.get("response_format"):
        prefix["response_format"] = body["response_format"]
    if body.get("text"):
        prefix["text"] = body["text"]
    if not prefix:
        return b""
    return json.dumps(prefix, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()


TOOLING_HEADER = "## Tooling\n"
TOOLING_FOOTER = "The AGENTS.md Tools section guides usage; it never grants availability."
TOOL_LINE = re.compile(r"(?m)^- ([A-Za-z0-9][A-Za-z0-9_.:-]{0,127})(?= \(|: )")
# A tool delta of a few names belongs after the conversation. A whole catalog
# is too long to move and stays where the caller put it.
MAX_TOOL_SURFACE_SUFFIX_CHARS = 8000
TOOL_SURFACE_LOCK = threading.Lock()
TOOL_SURFACE = None


def reset_tool_surface() -> None:
    global TOOL_SURFACE
    with TOOL_SURFACE_LOCK:
        TOOL_SURFACE = None


def tool_name(tool) -> str | None:
    if not isinstance(tool, dict):
        return None
    function = tool.get("function")
    if isinstance(function, dict) and isinstance(function.get("name"), str):
        return function["name"]
    name = tool.get("name")
    return name if isinstance(name, str) else None


def tool_names(tools, tooling: str | None) -> list[str]:
    found = []
    for name in TOOL_LINE.findall(tooling or ""):
        if name not in found:
            found.append(name)
    for tool in tools or []:
        name = tool_name(tool)
        if name and name not in found:
            found.append(name)
    return found


def split_tooling(content: str) -> tuple[str, str | None, str]:
    start = content.find(TOOLING_HEADER)
    if start < 0:
        return content, None, ""
    footer = content.find(TOOLING_FOOTER, start)
    if footer < 0:
        return content, None, ""
    end = footer + len(TOOLING_FOOTER)
    return content[:start], content[start:end], content[end:]


def tool_surface_suffix(base_names, current_names, current_tools, current_tooling) -> tuple[str, int, int]:
    removed = [name for name in base_names if name not in current_names]
    added = [name for name in current_names if name not in base_names]
    if not removed and not added:
        return "", 0, 0
    lines = [
        "## Tool Surface Update",
        "The tool list above is the cached list. Apply only this change for the current turn.",
    ]
    if removed:
        lines.append("Unavailable now: " + ", ".join(removed) + ".")
        lines.append("Do not call an unavailable tool.")
    added_lines = [
        line for line in (current_tooling or "").splitlines()
        if any(line.startswith(f"- {name}") for name in added)
    ]
    added_tools = [tool for tool in current_tools or [] if tool_name(tool) in added]
    if added_lines or added_tools:
        lines.append("Additional tools:")
        lines.extend(added_lines)
        for tool in added_tools:
            lines.append(json.dumps(tool, ensure_ascii=False, sort_keys=True, separators=(",", ":")))
        lines.append("Call an additional tool in the normal tool-call format.")
    return "\n".join(lines), len(added), len(removed)


def reuse_history_tool_surface(messages: list[dict], tools, tail_moved: bool) -> tuple[list, int, int, int]:
    """Keep the cached tool list and put the whole tool delta after the conversation.

    History does not have to grow. A tool change outweighs a history change.
    The delta stays in front only when the whole suffix would be too long.
    """
    global TOOL_SURFACE
    if not messages or messages[0].get("role") != "system":
        return tools, 0, 0, 0
    content = messages[0].get("content")
    if not isinstance(content, str):
        return tools, 0, 0, 0
    if "## OpenClaw Control" not in content or "## Workspace Files (injected)" not in content:
        return tools, 0, 0, 0
    before, tooling, after = split_tooling(content)
    current_tools = tools if isinstance(tools, list) else []
    current_names = tool_names(current_tools, tooling)
    snapshot = {
        "before": before,
        "tooling": tooling,
        "after": after,
        "tools": copy.deepcopy(current_tools),
        "names": current_names,
    }
    with TOOL_SURFACE_LOCK:
        pinned = TOOL_SURFACE
        if pinned is None or before != pinned["before"] or after != pinned["after"]:
            TOOL_SURFACE = snapshot
            return tools, 0, 0, 0
        suffix, added, removed = tool_surface_suffix(
            pinned["names"], current_names, current_tools, tooling)
        if not suffix:
            return tools, 0, 0, 0
        if len(suffix) > MAX_TOOL_SURFACE_SUFFIX_CHARS:
            TOOL_SURFACE = snapshot
            return tools, 0, 0, 0
        pinned_tools = copy.deepcopy(pinned["tools"])
        pinned_tooling = pinned["tooling"]
        pinned_before = pinned["before"]
        pinned_after = pinned["after"]
    if pinned_tooling is None:
        messages[0]["content"] = pinned_before + pinned_after
    else:
        messages[0]["content"] = pinned_before + pinned_tooling + pinned_after
    notice = {"role": "system", "content": suffix}
    if tail_moved:
        messages.insert(-1, notice)
    else:
        messages.append(notice)
    return pinned_tools, added, removed, len(suffix)


def model_name(body: dict) -> str:
    value = body.get("model")
    return value if isinstance(value, str) and value else "local-unknown"


def prepare_request(body: dict) -> tuple[dict, dict]:
    """Keep all instruction text; move only OpenClaw's volatile tail later.

    Qwen ChatML supports multiple system messages. A fresh temporal/channel
    suffix in the first system message otherwise invalidates all history KV.
    The tail remains a system message after the conversation history.
    Tool-call/result pairs are never split. Other clients keep their ordering.
    """
    body = dict(body)
    body.setdefault("cache_prompt", True)
    messages = [dict(m) for m in body.get("messages", [])]
    moved = 0
    if messages and messages[0].get("role") == "system":
        content = messages[0].get("content")
        boundary = "\n## Temporal Context\n"
        if (isinstance(content, str) and "## OpenClaw Control" in content
                and "## Workspace Files (injected)" in content and boundary in content):
            position = content.index(boundary)
            tail = content[position:]
            messages[0]["content"] = content[:position]
            # Never insert changing metadata ahead of conversation history or
            # split a tool-call/result pair. No instruction text is dropped.
            messages.append({"role": "system", "content": tail})
            moved = len(tail)
    tools, tools_added, tools_removed, suffix_chars = reuse_history_tool_surface(
        messages, body.get("tools"), moved > 0)
    if tools is not body.get("tools"):
        body["tools"] = tools
    body["messages"] = messages
    if body.get("stream"):
        options = dict(body.get("stream_options") or {})
        options["include_usage"] = True
        body["stream_options"] = options
    role_chars: dict[str, int] = {}
    for message in messages:
        role = message.get("role", "unknown")
        role_chars[role] = role_chars.get(role, 0) + len(text_value(message.get("content")))
    return body, {
        "model": model_name(body), "message_count": len(messages),
        "message_chars_by_role": role_chars,
        "tools_chars": len(json.dumps(body.get("tools", []), ensure_ascii=False)),
        "moved_system_tail_chars": moved, "cache_prompt": body["cache_prompt"],
        "tool_surface_added": tools_added, "tool_surface_removed": tools_removed,
        "tool_surface_suffix_chars": suffix_chars,
    }


def prepare_completion(body):
    """ECHO-SVC-001: raw user-turn probabilities, without chat templating."""
    body = dict(body)
    body.setdefault("cache_prompt", True)
    prompt = body.get("prompt", "")
    return body, {"model": model_name(body), "request_kind": "raw_completion",
                  "prompt_chars": len(prompt) if isinstance(prompt, str) else 0,
                  "cache_prompt": body["cache_prompt"],
                  "probabilities": body.get("n_probs", 0)}


def echo_capabilities():
    return {"protocol": "echo-external-http", "version": 1,
            "model_lifecycle": "provider_owned", "shared_model": True,
            "chat": {"endpoint": "/v1/chat/completions", "stream": True, "json_schema": True},
            "prediction": {"endpoint": "/completion", "raw_prompt": True,
                           "probabilities": "completion_probabilities[].top_logprobs"},
            "cancellation": {"streaming": True, "non_streaming": True},
            "kv": {"fork_at": False, "sequence_handles": False,
                   "cache_owner": "llama-server", "disk_fork_matching": True}}


def save_metrics(record: dict) -> None:
    with METRICS_LOCK:
        RECENT_REQUESTS.append(record)
        del RECENT_REQUESTS[:-100]
        try:
            METRICS_PATH.parent.mkdir(parents=True, exist_ok=True)
            temporary = METRICS_PATH.with_suffix(".tmp")
            temporary.write_text(json.dumps(RECENT_REQUESTS, ensure_ascii=False, indent=2))
            temporary.replace(METRICS_PATH)
        except OSError as exc:
            print(f"request metrics skipped: {type(exc).__name__}", flush=True)


def note_reuse(cached_tokens, prompt_tokens) -> None:
    try:
        import kv_disk_cache
        kv_disk_cache.note_slot_outcome(cached_tokens, prompt_tokens)
    except Exception:
        return


def response_metrics(value: dict, record: dict) -> None:
    usage = value.get("usage")
    if isinstance(usage, dict):
        record["usage"] = usage
        details = usage.get("prompt_tokens_details")
        cached = details.get("cached_tokens") if isinstance(details, dict) else None
        if cached is None:
            cached = usage.get("cached_tokens")
        if usage.get("prompt_tokens") is not None:
            note_reuse(cached or 0, usage.get("prompt_tokens"))
    timings = value.get("timings")
    if isinstance(timings, dict):
        record["timings"] = timings
        if not isinstance(usage, dict) and value.get("tokens_evaluated") is not None:
            cached = timings.get("cache_n", 0)
            total = value["tokens_evaluated"]
            record["usage"] = {"prompt_tokens": total,
                               "completion_tokens": value.get("tokens_predicted", 0),
                               "prompt_tokens_details": {"cached_tokens": cached}}
            note_reuse(cached, total)


class UpstreamCall:
    def __init__(self):
        self.started = time.monotonic()
        self.last_progress = self.started
        self.conn = None
        self.stall = None
        self.headers_sent = False
        self.client_disconnected = False
        self.native_kv = False


INFLIGHT = []
INFLIGHT_LOCK = threading.Lock()


def read_slot_runtime(upstream=None):
    """Return whether any slot is computing and how many tasks are deferred."""
    base = upstream or UPSTREAM
    with urllib.request.urlopen(base + "/slots", timeout=2) as response:
        slots = json.load(response)
    with urllib.request.urlopen(base + "/metrics", timeout=2) as response:
        metrics = response.read().decode("utf-8", "replace")
    processing = any(isinstance(slot, dict) and slot.get("is_processing") for slot in slots)
    deferred = 0
    for line in metrics.splitlines():
        if line.startswith("#") or "requests_deferred" not in line.split(" ", 1)[0]:
            continue
        deferred = max(deferred, int(float(line.split()[-1])))
    return processing, deferred


def wake_deferred_task(upstream=None):
    """Run one tiny completion so a slot release resumes one stranded task.

    llama-server only pops a deferred task when a slot is released. A save or a
    cancelled request can leave later chats queued while every slot is idle.
    """
    base = upstream or UPSTREAM
    payload = json.dumps({
        "model": "liveness-wake",
        "messages": [{"role": "user", "content": WAKE_CONTENT}],
        "max_tokens": 1,
        "stream": False,
        "cache_prompt": True,
        "temperature": 0,
    }).encode()
    request = urllib.request.Request(
        base + "/v1/chat/completions",
        data=payload,
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=120) as response:
        response.read()


def liveness_action(processing, deferred, silent, stagnant_wakes):
    """Decide how a silent call is allowed to end.

    A slot that is actually computing stays open. An idle slot either resumes
    one stranded task or the call fails. It does not remain pending.
    """
    if not silent or processing is None:
        return "wait", 0 if not silent else stagnant_wakes
    if processing:
        return "wait", 0
    if deferred > 0 and stagnant_wakes < 4:
        return "wake", stagnant_wakes
    return "fail", stagnant_wakes


def liveness_loop(stop: threading.Event) -> None:
    stagnant_wakes = 0
    while not stop.wait(LIVENESS_POLL_SECONDS):
        with INFLIGHT_LOCK:
            silent = [call for call in INFLIGHT
                      if not call.native_kv and call.stall is None and time.monotonic() - call.last_progress >= IDLE_STALL_SECONDS]
        processing = None
        deferred = 0
        if silent:
            try:
                processing, deferred = read_slot_runtime()
            except Exception:
                processing = None  # A probe failure is not evidence that the request is idle.
        action, stagnant_wakes = liveness_action(processing, deferred, bool(silent), stagnant_wakes)
        if action == "wait":
            continue
        if action == "wake":
            before = deferred
            try:
                wake_deferred_task()
            except Exception as exc:
                print(f"liveness wake failed: {type(exc).__name__}", flush=True)
                stagnant_wakes += 1
                continue
            try:
                processing, deferred = read_slot_runtime()
            except Exception:
                stagnant_wakes += 1
                continue
            if processing or deferred < before:
                stagnant_wakes = 0
            else:
                stagnant_wakes += 1
            continue
        for call in silent:
            call.stall = IDLE_ERROR
            sock = getattr(call.conn, "sock", None)
            if sock is not None:
                try:
                    sock.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_GET(self):
        controller = getattr(self.server, 'model_controller', None)
        if controller and self.path in ('/v1/echo/models', '/v1/echo/model'):
            self.send_json(200, controller.inventory() if self.path.endswith('/models')
                           else {'managed':True, **controller.job()})
            return
        if self.path == "/v1/echo/capabilities":
            capabilities = echo_capabilities()
            try:
                native = upstream_json('/v1/kv/op', {'op':'capabilities'}) if (
                    not controller or controller.native_supported()) else {}
                if all(native.get(key) is True for key in ('supported','shared_prefix','fork_at','label_constraint','release')):
                    capabilities['kv'].update(fork_at=True, sequence_handles=True,
                        endpoint='/v1/kv/op', hybrid_checkpoints=True,
                        branch_batch_size=native['branch_batch_size'], max_tokens=native['max_tokens'],
                        provider_parallel_slots=native.get('provider_parallel_slots', 1))
            except (OSError, ValueError, KeyError):
                pass
            self.send_json(200, capabilities)
            return
        if self.path == "/cache/stats":
            stats = prefix_cache.stats_dict()
            stats["text_repeat_uses"] = max(0, stats["hits"] - stats["entries"])
            stats["text_repeat_rate"] = stats["text_repeat_uses"] / max(1, stats["hits"])
            stats["disk_cache_kind"] = "prefix_text_accounting_and_persistent_kv_snapshots"
            try:
                stats["kv_disk_cache"] = json.loads((ROOT / "status" / "kv-disk-cache.json").read_text())
            except (OSError, ValueError):
                stats["kv_disk_cache"] = {"state": "not_started"}
            with METRICS_LOCK:
                stats["recent_inference_requests"] = list(RECENT_REQUESTS[-20:])
            self.send_json(200, stats)
            return
        self.forward(None)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        controller = getattr(self.server, 'model_controller', None)
        if controller and self.path == '/v1/echo/model':
            origin = self.headers.get('Origin')
            if origin or length > 4096 or self.headers.get_content_type() != 'application/json':
                self.send_json(403, {'error':{'message':'模型切换仅接受本机应用的 JSON 请求'}})
                return
            try:
                from model_switch import ModelError
                body = json.loads(self.rfile.read(length))
                if not isinstance(body, dict) or not isinstance(body.get('name'), str):
                    raise ModelError('请选择模型名称',400)
                result = controller.start(body['name'])
                self.send_json(202 if result['state'] == 'switching' else 200, result)
            except ModelError as error:
                self.send_json(error.status, {'error':{'message':str(error)}})
            except (ValueError, OSError):
                self.send_json(503, {'error':{'message':'模型切换服务不可用'}})
            return
        data = self.rfile.read(length) if length else b""
        if not self.permit_inference(data):
            return
        if controller and self.path == '/v1/kv/op' and not controller.native_supported():
            try:
                operation = json.loads(data).get('op')
            except (ValueError, AttributeError):
                operation = None
            if operation in ('capabilities','runtime'):
                self.send_json(200, {'backend':'external-http','supported':False,'shared_prefix':False,
                    'fork_at':False,'label_constraint':False,'release':False,
                    'reason':'此模型使用兼容选义，滑动窗口原生检查点尚未适配'})
            else:
                self.send_json(501, {'error':{'message':'native_kv_not_supported_for_selected_model'}})
            return
        self.request_metrics = None
        if self.path.rstrip("/").endswith("/chat/completions"):
            try:
                body = json.loads(data)
                if isinstance(body, dict) and isinstance(body.get("messages"), list):
                    body, self.request_metrics = prepare_request(body)
                    align_fork(body)
                    data = json.dumps(body, ensure_ascii=False).encode()
            except (ValueError, TypeError, AttributeError):
                pass  # Let upstream return its ordinary validation error.
        if self.path.rstrip("/") == "/completion":
            try:
                body = json.loads(data)
                if isinstance(body, dict):
                    body, self.request_metrics = prepare_completion(body)
                    align_fork(body)
                    data = json.dumps(body, ensure_ascii=False).encode()
            except (ValueError, TypeError, AttributeError):
                pass
        if self.request_metrics is not None:
            self.request_metrics["client"] = "echo" if self.headers.get("X-Local-AI-Client") == "echo" else "other"
        if self.path.rstrip("/").endswith(("/responses", "/chat/completions")):
            self.record_prefix(data)
        self.forward(data)

    def record_prefix(self, data: bytes) -> None:
        try:
            body = json.loads(data)
            if not isinstance(body, dict):
                return
            # Accounting follows the cache-stable prefix too. The moved tail is
            # still sent to the model, but a new timestamp is not a new prefix.
            metrics = getattr(self, "request_metrics", None) or {}
            if metrics.get("moved_system_tail_chars") or metrics.get("tool_surface_suffix_chars"):
                body = dict(body)
                body["messages"] = body["messages"][:-1]
            prefix = stable_prefix(body)
            if len(prefix) < MIN_PREFIX_BYTES:
                return
            result = prefix_cache.record_bytes(
                model_name(body),
                "ollama-local-prefix-v1",
                prefix,
                kind="local-ai-codex-prefix",
            )
            self.log_message(
                "local prefix cache %s model=%s bytes=%d weight=%s",
                result.get("status"),
                model_name(body),
                len(prefix),
                result.get("weight", "-"),
            )
        except Exception as exc:
            # A cache failure must never block the local model request.
            self.log_message("prefix cache skipped: %s", exc)

    def fail_idle(self, record, started: float, message: str, headers_sent: bool) -> None:
        if record is not None:
            record.update(status="model_idle", error_type="model_idle",
                          total_seconds=round(time.monotonic() - started, 4))
        body = {"error": {"message": message, "type": "model_idle"}}
        if headers_sent:
            try:
                self.wfile.write(b"data: " + json.dumps(body, ensure_ascii=False).encode() + b"\n\n")
                self.wfile.flush()
            except OSError:
                pass
            return
        self.send_json(503, body)

    def permit_inference(self, data):
        controller = getattr(self.server, 'model_controller', None)
        if controller and controller.switching():
            cleanup = False
            if self.path == '/v1/kv/op' and data:
                try:
                    cleanup = json.loads(data).get('op') in ('release','release_branch')
                except (ValueError, AttributeError):
                    pass
            if not cleanup:
                self.send_json(503, {'error':{'message':'统一服务正在更换模型，请稍后重试',
                    'type':'model_switching'}})
                return False
        return True

    def forward(self, data: bytes | None) -> None:
        if not self.permit_inference(data):
            return
        started = time.monotonic()
        record = getattr(self, "request_metrics", None)
        if record is not None:
            record["started_unix"] = time.time()
        headers = {key: value for key, value in self.headers.items()
                   if key.lower() not in HOP_HEADERS}
        headers["Connection"] = "close"
        if data is not None:
            headers["Content-Length"] = str(len(data))
        parsed = urllib.parse.urlsplit(UPSTREAM)
        call = UpstreamCall()
        # KV operations own queue tasks rather than public generation slots.
        # /slots idle is therefore not evidence of a stalled native operation.
        # Socket cancellation and normal upstream/client timeouts still apply.
        call.native_kv = self.path.rstrip('/') == '/v1/kv/op'
        conn = http.client.HTTPConnection(parsed.hostname, parsed.port or 80, timeout=UPSTREAM_TIMEOUT_SECONDS)
        call.conn = conn
        with INFLIGHT_LOCK:
            INFLIGHT.append(call)
        stop_monitor = threading.Event()
        monitor = None
        upstream_socket = None
        try:
            conn.connect()
            conn.request(self.command, self.path, body=data, headers=headers)
            # Start BEFORE waiting for headers. Raw probabilities and JSON
            # selectors must cancel during queueing/prefill as well as SSE.
            upstream_socket = socket.socket(fileno=os.dup(conn.sock.fileno()))

            def monitor_disconnect():
                while not stop_monitor.wait(0.05):
                    try:
                        readable, _, _ = select.select([self.connection], [], [], 0)
                        if readable and not self.connection.recv(1, socket.MSG_PEEK):
                            call.client_disconnected = True
                            if record is not None:
                                record["status"] = "client_disconnected"
                            upstream_socket.shutdown(socket.SHUT_RDWR)
                            return
                    except OSError:
                        return

            monitor = threading.Thread(target=monitor_disconnect, daemon=True)
            monitor.start()
            response = conn.getresponse()
            call.last_progress = time.monotonic()
            content_type = response.getheader("Content-Type", "application/json")
            streaming = response.status < 400 and "text/event-stream" in content_type
            payload = None if streaming else response.read()
            if call.client_disconnected:
                return
            self.send_response(response.status)
            for key, value in response.getheaders():
                if key.lower() not in HOP_HEADERS and key.lower() != "content-type":
                    self.send_header(key, value)
            self.send_header("Content-Type", content_type)
            self.send_header("Connection", "close")
            if payload is not None:
                self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            call.headers_sent = True
            self.close_connection = True
            if streaming:
                self._relay_stream(response, record, started, call)
            else:
                self.wfile.write(payload)
                if record is not None:
                    try:
                        response_metrics(json.loads(payload), record)
                    except (ValueError, TypeError, AttributeError):
                        pass
                    record["status"] = "complete" if response.status < 400 else "upstream_error"
                    if response.status >= 400:
                        record["http_status"] = response.status
        except (OSError, TimeoutError, http.client.HTTPException) as exc:
            if call.client_disconnected:
                return
            if call.stall:
                self.fail_idle(record, started, call.stall, call.headers_sent)
                return
            if record is not None:
                record.update(status="upstream_error", error_type=type(exc).__name__)
            if not call.headers_sent:
                try:
                    self.send_json(502, {"error": {"message": str(exc), "type": "upstream_error"}})
                except OSError:
                    pass
        finally:
            stop_monitor.set()
            if monitor is not None:
                monitor.join(timeout=1)
            if upstream_socket is not None:
                upstream_socket.close()
            with INFLIGHT_LOCK:
                if call in INFLIGHT:
                    INFLIGHT.remove(call)
            conn.close()
            if record is not None:
                record.setdefault("status", "complete")
                record["total_seconds"] = round(time.monotonic() - started, 4)
                save_metrics(record)

    def _relay_stream(self, response, record, started: float, call: UpstreamCall) -> None:
        pending = b""
        while True:
            chunk = response.read1(8192)
            if not chunk:
                break
            call.last_progress = time.monotonic()
            self.wfile.write(chunk)
            self.wfile.flush()
            if record is not None:
                record.setdefault("first_event_seconds", round(time.monotonic() - started, 4))
                pending += chunk
                while b"\n" in pending:
                    line, pending = pending.split(b"\n", 1)
                    if line.startswith(b"data: ") and line.strip() != b"data: [DONE]":
                        try:
                            event = json.loads(line[6:])
                            response_metrics(event, record)
                            if any(c.get("delta", {}).get("content") for c in event.get("choices") or []):
                                record.setdefault("first_content_seconds", round(time.monotonic() - started, 4))
                        except (ValueError, TypeError, AttributeError):
                            pass

    def relay_error(self, exc: urllib.error.HTTPError) -> None:
        payload = exc.read()
        self.send_response(exc.code)
        self.send_header("Content-Type", exc.headers.get("Content-Type", "application/json"))
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def send_json(self, status: int, value: dict) -> None:
        payload = json.dumps(value, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(payload)
        self.close_connection = True

    def log_message(self, fmt, *args):
        print(fmt % args, flush=True)


class Server(http.server.ThreadingHTTPServer):
    allow_reuse_address = True
    daemon_threads = True

    def serve_forever(self, poll_interval=0.5):
        self.liveness_stop = threading.Event()
        thread = threading.Thread(target=liveness_loop, args=(self.liveness_stop,), daemon=True)
        thread.start()
        try:
            super().serve_forever(poll_interval)
        finally:
            self.liveness_stop.set()
            thread.join(timeout=2)


def main() -> None:
    server = Server((LISTEN_HOST, LISTEN_PORT), Handler)
    from model_switch import Controller
    server.model_controller = Controller()
    print(f"local-ai-cache-proxy listening on http://{LISTEN_HOST}:{LISTEN_PORT}", flush=True)
    try:
        server.serve_forever()
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
