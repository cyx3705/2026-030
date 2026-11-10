#!/usr/bin/env bash
# Builds server code ONLY; reuses the pinned deployed CUDA/libllama libraries.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
REV=9ff0d7e90f0faaa7c16017e6dcf9cf91c50a100e
WORK="${ROOT}/linux-cache/echo-kv-build"
mkdir -p "${WORK}"
if [[ ! -f "${WORK}/llama-source.tar.gz" ]]; then
  curl -fL "https://codeload.github.com/ggml-org/llama.cpp/tar.gz/${REV}" -o "${WORK}/llama-source.tar.gz"
fi
echo 'd8552e1b309c1571e49baf7a53d429b9709fa0c1d6907c446f6cecb87087d07b  '"${WORK}/llama-source.tar.gz" | sha256sum -c -
tar -xzf "${WORK}/llama-source.tar.gz" -C "${WORK}"
python3 "${ROOT}/scripts/echo-kv/patch_server.py" "${WORK}/llama.cpp-${REV}"
# A cache written under another runtime root (the root was moved) makes cmake
# refuse to configure; drop it and let this run regenerate it.
if [[ -f "${WORK}/build/CMakeCache.txt" ]] &&
   ! grep -qxF "CMAKE_HOME_DIRECTORY:INTERNAL=${ROOT}/scripts/echo-kv" "${WORK}/build/CMakeCache.txt"; then
  rm -rf "${WORK}/build/CMakeCache.txt" "${WORK}/build/CMakeFiles"
fi
cmake -S "${ROOT}/scripts/echo-kv" -B "${WORK}/build" \
  -DUPSTREAM="${WORK}/llama.cpp-${REV}" -DRUNTIME_LIB="${ROOT}/linux-ollama/lib/ollama"
cmake --build "${WORK}/build" -j "$(nproc)"
# ggml discovers CPU plugins beside the executable. Keep one shared library
# installation and link to it instead of copying or rebuilding CUDA backends.
for library in "${ROOT}/linux-ollama/lib/ollama/"*.so*; do
  # Relative links: the runtime root can move without breaking the build directory.
  ln -sfnr "${library}" "${WORK}/build/$(basename "${library}")"
done
