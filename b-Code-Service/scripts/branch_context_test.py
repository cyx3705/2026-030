#!/usr/bin/env python3
"""Two divergent prompts must each reuse their own saved context.

The report contains token counts only. Prompt text and model output stay out.
"""
import json
import sys
import time
import urllib.request

import kv_disk_cache as cache


PROXY = "http://127.0.0.1:11435/v1/chat/completions"
UPSTREAM = "http://127.0.0.1:18769"


def get_json(url, body=None, timeout=30):
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.load(response)


def slot_busy():
    return any(slot.get("is_processing") for slot in get_json(UPSTREAM + "/slots", timeout=5))


def messages(branch, assistant=None):
    shared = "\n".join(
        f"Shared rule {index:03d}: this line is identical for every branch." for index in range(40))
    own = "\n".join(
        f"Branch {branch} fact {index:03d}: this line exists only on branch {branch}."
        for index in range(120))
    built = [
        {"role": "system", "content": shared + "\n" + own},
        {"role": "user", "content": f"Branch {branch}. Reply with the single word OK."},
    ]
    if assistant is not None:
        built.append({"role": "assistant", "content": assistant})
        built.append({"role": "user", "content": "Again, reply with the single word OK."})
    return built


def request_tokens(body):
    rendered = get_json(UPSTREAM + "/apply-template", body, timeout=60)
    prompt = rendered.get("prompt")
    if not isinstance(prompt, str) or not prompt:
        raise RuntimeError("apply-template returned no prompt")
    tokenized = get_json(UPSTREAM + "/tokenize", {
        "content": prompt, "add_special": False, "parse_special": True,
    }, timeout=60)
    tokens = tokenized.get("tokens")
    if not isinstance(tokens, list):
        raise RuntimeError("tokenize returned no tokens")
    return tokens


def run(label, branch, assistant=None):
    if slot_busy():
        raise SystemExit(f"refusing {label}: the model slot is busy")
    body = {"model": "qwen3.5:35b-a3b-q4_K_M", "messages": messages(branch, assistant),
            "stream": True, "temperature": 0, "max_tokens": 4}
    started = time.monotonic()
    text = []
    result = {"label": label, "branch": branch}
    request = urllib.request.Request(
        PROXY, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=600) as response:
        for line in response:
            if not line.startswith(b"data: ") or line.strip() == b"data: [DONE]":
                continue
            event = json.loads(line[6:])
            for choice in event.get("choices") or []:
                delta = (choice.get("delta") or {}).get("content")
                if isinstance(delta, str):
                    text.append(delta)
            if "usage" in event:
                result["usage"] = event["usage"]
            if "timings" in event:
                result["timings"] = event["timings"]
    usage = result.get("usage") or {}
    details = usage.get("prompt_tokens_details") or {}
    timings = result.get("timings") or {}
    summary = {
        "label": label,
        "branch": branch,
        "prompt_tokens": usage.get("prompt_tokens"),
        "cached_tokens": details.get("cached_tokens", 0),
        "processed_tokens": timings.get("prompt_n"),
        "prompt_seconds": round((timings.get("prompt_ms") or 0) / 1000, 3),
        "total_seconds": round(time.monotonic() - started, 3),
    }
    print(json.dumps(summary), flush=True)
    summary["assistant"] = "".join(text)
    return summary


def save_branch():
    if not cache.save(wait=True):
        raise RuntimeError("branch snapshot was not saved")
    current = json.loads(cache.STATUS.read_text())
    print(json.dumps({
        "saved": current.get("snapshot"),
        "tokens": current.get("tokens"),
    }), flush=True)
    return current.get("snapshot")


def overlap(label, body, snapshot):
    tokens = request_tokens(body)
    saved = cache.read_token_prefix(cache.SLOTS / snapshot)
    print(json.dumps({
        "label": label,
        "request_tokens": len(tokens),
        "saved_tokens": len(saved),
        "common_prefix": cache.common_prefix_len(tokens, saved),
    }), flush=True)


def main():
    if slot_busy():
        raise SystemExit("Model is busy; the branch test does not interrupt it.")
    first = run("A_cold", "A")
    snapshot_a = save_branch()
    second = run("A_again", "A", assistant=first["assistant"])
    save_branch()
    third = run("B_other", "B")
    snapshot_b = save_branch()
    overlap("A_against_B_save", {
        "model": "qwen3.5:35b-a3b-q4_K_M",
        "messages": messages("A", first["assistant"]),
    }, snapshot_b)
    overlap("A_against_A_save", {
        "model": "qwen3.5:35b-a3b-q4_K_M",
        "messages": messages("A", first["assistant"]),
    }, snapshot_a)
    fourth = run("A_return", "A", assistant=first["assistant"])
    fifth = run("B_return", "B", assistant=third["assistant"])
    failures = []
    if second["cached_tokens"] < 1024:
        failures.append("same branch was not reused")
    if fourth["cached_tokens"] < 1024:
        failures.append("returning to branch A recomputed instead of switching cache")
    if fifth["cached_tokens"] < 1024:
        failures.append("returning to branch B recomputed instead of switching cache")
    if failures:
        raise SystemExit("branch context test failed: " + "; ".join(failures))
    print(json.dumps({"result": "pass", "A_again": second["cached_tokens"],
                      "A_return": fourth["cached_tokens"], "B_return": fifth["cached_tokens"]}),
          flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"branch context test error: {type(exc).__name__}: {exc}", file=sys.stderr)
        raise
