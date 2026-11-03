#!/usr/bin/env python3
"""Measure actual llama KV reuse through the production proxy, without delivery."""
import json
import pathlib
import sys
import time
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent


def get_json(url):
    return json.load(urllib.request.urlopen(url, timeout=5))


def run(label, group, clock, followup=False):
    stable = (
        "## OpenClaw Control\nCache verification; do not use any tools.\n"
        "## Workspace Files (injected)\nReply with OK and nothing else.\n"
        + "\n".join(f"Reference {group} item {n:03d}: keep this stable context unchanged."
                    for n in range(96))
    )
    tail = f"\n## Temporal Context\nDiagnostic clock: {clock}\n## Runtime\nlocal verification"
    messages = [{"role": "system", "content": stable + tail},
                {"role": "user", "content": "Reply OK."}]
    if followup:
        messages += [{"role": "assistant", "content": "OK"},
                     {"role": "user", "content": "Again, reply OK."}]
    body = {"model": "qwen3.5:35b-a3b-q4_K_M", "messages": messages,
            "stream": True, "temperature": 0, "max_tokens": 4}
    request = urllib.request.Request(
        "http://127.0.0.1:11435/v1/chat/completions",
        data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    started = time.monotonic()
    result = {"label": label}
    with urllib.request.urlopen(request, timeout=180) as response:
        for line in response:
            if not line.startswith(b"data: ") or line.strip() == b"data: [DONE]":
                continue
            event = json.loads(line[6:])
            if any(c.get("delta", {}).get("content") for c in event.get("choices", [])):
                result.setdefault("first_content_seconds", round(time.monotonic() - started, 3))
            for key in ("usage", "timings"):
                if key in event:
                    result[key] = event[key]
    result["total_seconds"] = round(time.monotonic() - started, 3)
    # Idle slots may already be moved into the RAM cache and report zero.
    # Per-response usage/timings describe the request that just completed.
    result["cached_tokens"] = result.get("usage", {}).get("prompt_tokens_details", {}).get("cached_tokens", 0)
    result["processed_tokens"] = result.get("timings", {}).get("prompt_n", 0)
    print(json.dumps(result), flush=True)
    return result


def validate(results):
    for result in results:
        result["cached_tokens"] = result.get("usage", {}).get("prompt_tokens_details", {}).get("cached_tokens", 0)
        result["processed_tokens"] = result.get("timings", {}).get("prompt_n", 0)
    assert results[1]["cached_tokens"] > 0, "same prefix was recomputed"
    assert results[2]["cached_tokens"] > 0, "conversation growth invalidated all KV"
    assert results[4]["cached_tokens"] > 0, "RAM cache did not retain prefix A"


def main():
    destination = ROOT / "linux-cache" / "prefix-reuse-verification.json"
    if sys.argv[1:] == ["--validate-saved"]:
        results = json.loads(destination.read_text())
        validate(results)
        destination.write_text(json.dumps(results, ensure_ascii=False, indent=2) + "\n")
        print("Saved per-response token usage verifies prefix reuse, history growth, and A/B/A cache retention.")
        return
    if any(s.get("is_processing") for s in get_json("http://127.0.0.1:18769/slots")):
        raise SystemExit("Model is busy; verification does not interrupt active requests.")
    results = [run("A_cold", "A", "one"),
               run("A_changed_clock", "A", "two"),
               run("A_followup", "A", "three", followup=True),
               run("B_other_prefix", "B", "four"),
               run("A_return_after_B", "A", "five", followup=True)]
    validate(results)
    destination.write_text(json.dumps(results, ensure_ascii=False, indent=2) + "\n")


if __name__ == "__main__":
    main()
