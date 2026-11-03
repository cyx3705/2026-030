#!/usr/bin/env bash
# Unified local model service for WSL.
#   start              selected llama-server on 127.0.0.1:18769 and the C++ Echo proxy on 11435
#   start-experiment   llama-server on 127.0.0.1:18765 for the b-code /completion API
#   stop               stop the processes this script started
#   stop-experiment    stop only the experiment llama-server
#   status             print which listeners answer
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Runtime root is <drive>/ModuleServicesData/Ratio; models stay in <drive>/Models.
DRIVE="$(cd "${ROOT}/../.." && pwd)"
LIB="${ROOT}/linux-ollama/lib/ollama"
BIN="${ROOT}/linux-ollama/bin/ollama"
LOG_DIR="${ROOT}/linux-logs"
RUN_DIR="${ROOT}/status"
MODELS="${DRIVE}/Models/Ollama"
QWEN8_BLOB="${MODELS}/blobs/sha256-a3de86cd1c132c822487ededd47a324c50491393e6565cd14bafa40d0b8e686f"
export LD_LIBRARY_PATH="${LIB}${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}"
export OLLAMA_HOST="${OLLAMA_HOST:-127.0.0.1:11434}"
export OLLAMA_MODELS="${MODELS}"
export OLLAMA_MAX_LOADED_MODELS="${OLLAMA_MAX_LOADED_MODELS:-1}"
export NO_PROXY="${NO_PROXY:-localhost,127.0.0.1,::1}"

mkdir -p "${LOG_DIR}" "${RUN_DIR}" "${ROOT}/linux-tmp" "${ROOT}/linux-cache"

http_up() {
  python3 - "$1" <<'PY'
import sys, urllib.request
try:
    urllib.request.urlopen(sys.argv[1], timeout=2)
except Exception:
    sys.exit(1)
PY
}

pid_alive() {
  local pidfile="$1"
  [[ -f "${pidfile}" ]] || return 1
  local pid
  pid="$(tr -d '[:space:]' < "${pidfile}")"
  [[ -n "${pid}" ]] && kill -0 "${pid}" 2>/dev/null
}

stop_pid() {
  local pidfile="$1"
  if pid_alive "${pidfile}"; then
    kill "$(tr -d '[:space:]' < "${pidfile}")" 2>/dev/null || true
    sleep 1
    if pid_alive "${pidfile}"; then
      kill -9 "$(tr -d '[:space:]' < "${pidfile}")" 2>/dev/null || true
    fi
  fi
  rm -f "${pidfile}"
}

stop_stray_ollama() {
  if ! http_up "http://127.0.0.1:11434/api/version"; then
    return 0
  fi
  echo "stopping extra ollama on 11434"
  pkill -f "${BIN} serve" || true
  stop_pid "${RUN_DIR}/ollama.pid"
}

start_35b() {
  bash "${ROOT}/scripts/start-fast-path.sh"
  for _ in $(seq 1 180); do
    if http_up "http://127.0.0.1:18769/health"; then
      echo "selected model ready on 18769"
      return 0
    fi
    if ! pid_alive "${RUN_DIR}/fast-path-18769.pid"; then
      echo "Configured model runtime exited; see fast-path-18769.log" >&2
      return 1
    fi
    sleep 1
  done
  echo "selected model did not become ready; see ${LOG_DIR}/fast-path-18769.log" >&2
  return 1
}

start_proxy() {
  if http_up "http://127.0.0.1:11435/cache/stats"; then
    echo "cache proxy already listening on 11435"
    return 0
  fi
  local PROXY="${ROOT}/linux-cache/echo-kv-build/build/echo-proxy"
  if [[ ! -x "${PROXY}" ]]; then
    echo "C++ Echo proxy is missing; run scripts/echo-kv/build.sh first" >&2
    return 1
  fi
  LOCAL_AI_UPSTREAM="http://127.0.0.1:18769" LOCAL_AI_ROOT="${ROOT}" \
    setsid nohup "${PROXY}" 11435 >>"${LOG_DIR}/echo-proxy.log" 2>&1 </dev/null &
  echo $! >"${RUN_DIR}/cache-proxy.pid"
  for _ in $(seq 1 20); do
    if http_up "http://127.0.0.1:11435/cache/stats"; then
      echo "cache proxy started"
      return 0
    fi
    sleep 1
  done
  echo "C++ proxy did not become ready; see ${LOG_DIR}/echo-proxy.log" >&2
  return 1
}

start_kv_disk_cache() {
  # Explicit server-only upgrades preserve the old slot before proxy startup.
  python3 "${ROOT}/scripts/echo-kv/migrate_cache.py" apply
  # Restore only into an empty model slot, before starting the client proxy.
  # A running/populated slot is left intact; no process is restarted here.
  python3 "${ROOT}/scripts/kv_disk_cache.py" restore
  if pid_alive "${RUN_DIR}/kv-disk-cache.pid"; then
    echo "KV disk cache watcher already running"
    return 0
  fi
  setsid nohup python3 "${ROOT}/scripts/kv_disk_cache.py" watch \
    >>"${LOG_DIR}/kv-disk-cache.log" 2>&1 </dev/null &
  echo $! >"${RUN_DIR}/kv-disk-cache.pid"
  echo "KV disk cache enabled (shared 40 GB budget)"
}

unload_ollama_models() {
  if ! http_up "http://127.0.0.1:11434/api/version"; then
    return 0
  fi
  python3 - <<'PY' | while read -r name; do
import json, urllib.request
try:
    with urllib.request.urlopen("http://127.0.0.1:11434/api/ps", timeout=5) as response:
        data = json.load(response)
except Exception:
    data = {}
for model in data.get("models") or []:
    name = model.get("name") or model.get("model")
    if name:
        print(name)
PY
    [[ -n "${name}" ]] || continue
    "${BIN}" stop "${name}" || true
  done
}

start_experiment() {
  echo "Legacy GPU experiment route retired; Vulkan-only main model uses 18769" >&2
  return 1
}

status() {
  if http_up "http://127.0.0.1:18769/health"; then echo "model 18769 up"; else echo "model 18769 down"; fi
  if http_up "http://127.0.0.1:11435/cache/stats"; then echo "cache-proxy 11435 up"; else echo "cache-proxy 11435 down"; fi
  if http_up "http://127.0.0.1:11434/api/version"; then echo "ollama 11434 up"; else echo "ollama 11434 down"; fi
  if http_up "http://127.0.0.1:18765/health"; then echo "experiment 18765 up"; else echo "experiment 18765 down"; fi
}

cmd="${1:-status}"
case "${cmd}" in
  start)
    stop_stray_ollama
    start_35b
    start_kv_disk_cache
    start_proxy
    status
    ;;
  start-experiment)
    start_experiment
    ;;
  restart-proxy)
    stop_pid "${RUN_DIR}/cache-proxy.pid"
    start_proxy
    ;;
  stop-experiment)
    stop_pid "${RUN_DIR}/experiment-llama.pid"
    ;;
  stop)
    # Save first. A failed save aborts stop, preserving the live model/cache.
    if http_up "http://127.0.0.1:18769/health"; then
      python3 "${ROOT}/scripts/kv_disk_cache.py" save --wait
    fi
    stop_pid "${RUN_DIR}/kv-disk-cache.pid"
    stop_pid "${RUN_DIR}/experiment-llama.pid"
    stop_pid "${RUN_DIR}/cache-proxy.pid"
    stop_pid "${RUN_DIR}/fast-path-18769.pid"
    stop_stray_ollama
    ;;
  status)
    status
    ;;
  *)
    echo "usage: $0 {start|start-experiment|stop|stop-experiment|restart-proxy|status}" >&2
    exit 2
    ;;
esac
