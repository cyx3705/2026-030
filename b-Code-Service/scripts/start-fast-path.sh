#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mkdir -p "${ROOT}/status" "${ROOT}/linux-logs"
if ss -lnt | grep -q ':18769 '; then
  echo "already listening on 18769"
  exit 0
fi
setsid "${ROOT}/scripts/fast-path-35b.sh" </dev/null >/dev/null 2>&1 &
echo $! > "${ROOT}/status/fast-path-18769.pid"
echo "started $(cat "${ROOT}/status/fast-path-18769.pid")"
