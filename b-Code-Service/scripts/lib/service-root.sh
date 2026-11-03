# Resolve the Ratio model service (<drive>/ModuleServicesData/Ratio) whether it is mounted in WSL (/mnt/f)
# or on the native Ubuntu system partition.
_SERVICE_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVICE_ROOT="$(cd "${_SERVICE_LIB_DIR}/../.." && pwd)"
DRIVE_ROOT="$(cd "${SERVICE_ROOT}/../.." && pwd)"
MODELS_ROOT="${DRIVE_ROOT}/Models"
OLLAMA_MODELS="${MODELS_ROOT}/Ollama"
