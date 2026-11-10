"""Offline native ABI and pinned patch tests; no model, GPU or network required."""
from pathlib import Path
import argparse
import subprocess
import tarfile
import tempfile
import sys

p = argparse.ArgumentParser()
p.add_argument('--archive', required=True)
p.add_argument('--compiler', default='g++')
p.add_argument('--runtime-libs', help='Also link the full patched server against matching deployed Linux libraries')
args = p.parse_args()
here = Path(__file__).resolve().parent
with tempfile.TemporaryDirectory(prefix='ratio-native-test-') as folder:
    work = Path(folder)
    with tarfile.open(args.archive) as archive:
        archive.extractall(work, filter='data')
    source = next(work.glob('llama.cpp-*'))
    subprocess.run([sys.executable, str(here/'patch_server.py'), str(source)], check=True)
    subprocess.run([sys.executable, str(here/'patch_native.py'), str(source)], check=True)
    assert 'echo_kv.run(task.echo_body' in (source/'tools/server/server-context.cpp').read_text()
    assert (source/'tools/server/echo-kv.h').read_bytes() == (here/'echo-kv.h').read_bytes()
    output = work/'native-test'
    subprocess.run([args.compiler, '-std=c++17', '-pthread', '-O2',
        '-I'+str(source/'tools/server'), '-I'+str(source/'vendor'),
        str(here/'ratio-native.cpp'), str(here/'test_native_abi.cpp'), '-o', str(output)], check=True)
    subprocess.run([str(output)], check=True, timeout=15)
    if args.runtime_libs:
        cmake = work/'CMakeLists.txt'
        cmake.write_text('''cmake_minimum_required(VERSION 3.20)
project(ratio-native-integration LANGUAGES C CXX)
set(CMAKE_CXX_STANDARD 17)
file(GLOB SOURCES "${UPSTREAM}/tools/server/*.cpp")
add_library(ratio-inference SHARED ${SOURCES})
target_compile_definitions(ratio-inference PRIVATE RATIO_NATIVE_EMBEDDED)
target_include_directories(ratio-inference PRIVATE "${UPSTREAM}" "${UPSTREAM}/include"
 "${UPSTREAM}/ggml/include" "${UPSTREAM}/common" "${UPSTREAM}/vendor" "${UPSTREAM}/vendor/nlohmann"
 "${UPSTREAM}/tools/mtmd" "${UPSTREAM}/tools/server" "${CMAKE_CURRENT_BINARY_DIR}")
execute_process(COMMAND ${CMAKE_COMMAND} -DUI_SOURCE_DIR=${UPSTREAM}/tools/ui
 -DUI_BINARY_DIR=${CMAKE_CURRENT_BINARY_DIR} -DLLAMA_SOURCE_DIR=${UPSTREAM}
 -DBUILD_UI=OFF -DHF_ENABLED=OFF -P ${UPSTREAM}/scripts/ui-assets.cmake COMMAND_ERROR_IS_FATAL ANY)
target_sources(ratio-inference PRIVATE "${CMAKE_CURRENT_BINARY_DIR}/ui.cpp")
target_link_directories(ratio-inference PRIVATE "${RUNTIME_LIB}")
target_link_libraries(ratio-inference PRIVATE llama-common llama mtmd ggml ggml-base pthread dl)
''')
        subprocess.run(['cmake', '-S', str(work), '-B', str(work/'build'),
                        '-DUPSTREAM='+str(source), '-DRUNTIME_LIB='+args.runtime_libs], check=True, stdout=subprocess.DEVNULL)
        subprocess.run(['cmake', '--build', str(work/'build'), '-j', '4'], check=True)
        print('PASS full patched server and native ABI compile and link (Linux compatibility check)')
    print('PASS pinned patches preserve Echo queue and KV source')
    print('PASS native ABI startup, restart, single model, JSON, SSE, cancellation and shutdown')
