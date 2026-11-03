#!/usr/bin/env bash
# Single CUDA runtime; profiles and the last successful selection are provider-owned.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LIB="${ROOT}/linux-ollama/lib/ollama"
LOG="${ROOT}/linux-logs/fast-path-18769.log"
export LD_LIBRARY_PATH="${LIB}:${LIB}/cuda_v13:${LIB}/cuda_v12"
export GGML_BACKEND_PATH="${LIB}/cuda_v13/libggml-cuda.so"
unset GGML_CUDA_ENABLE_UNIFIED_MEMORY
export TMPDIR="${ROOT}/linux-tmp"
export XDG_CACHE_HOME="${ROOT}/linux-cache"
export NO_PROXY="${NO_PROXY:-localhost,127.0.0.1,::1}"
mkdir -p "${ROOT}/linux-logs" "${ROOT}/linux-tmp" "${ROOT}/linux-cache" "${ROOT}/status" "${ROOT}/prefix-cache/runtime-slots"
# ECHO-KV-001: same libllama/CUDA weights, server with queue-owned KV extension.
SERVER="${ROOT}/linux-cache/echo-kv-build/build/llama-server"
[[ -x "${SERVER}" ]] || { echo "Build scripts/echo-kv/build.sh first" >&2; exit 1; }
exec python3 "${ROOT}/scripts/model_switch.py" launch "$@" >>"${LOG}" 2>&1
