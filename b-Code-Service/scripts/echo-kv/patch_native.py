"""RATIO-NATIVE-001: add an in-process ABI to the already Echo-patched server.

Does not change echo-kv.h or its queue integration. Never edits upstream in place
in the repository; the builder supplies a disposable pinned source tree.
"""
from pathlib import Path
import shutil
import sys

root = Path(sys.argv[1])
here = Path(__file__).resolve().parent
path = root / 'tools/server/server.cpp'
text = path.read_text(encoding='utf-8')

def replace(old, new):
    global text
    if old not in text:
        raise RuntimeError('Pinned native integration changed: ' + old[:80])
    text = text.replace(old, new, 1)

replace('#include "server-http.h"', '#include "server-http.h"\n#ifdef RATIO_NATIVE_EMBEDDED\n#include "ratio-native.h"\n#endif')
if text.count('if (!ctx_http.start()) {') != 2:
    raise RuntimeError('Pinned HTTP startup branches changed')
text = text.replace('if (!ctx_http.start()) {', 'if (\n#ifndef RATIO_NATIVE_EMBEDDED\n            !ctx_http.start()\n#else\n            false\n#endif\n        ) {')
replace('        routes.update_meta(ctx_server);\n        ctx_http.is_ready.store(true);', '''        routes.update_meta(ctx_server);
        ctx_http.is_ready.store(true);
#ifdef RATIO_NATIVE_EMBEDDED
        ratio_native_publish(ctx_http, [&ctx_server]() { ctx_server.terminate(); });
#endif''')
replace('    if (!is_run_by_cli) {\n#if defined (__unix__)', '    if (!is_run_by_cli\n#ifdef RATIO_NATIVE_EMBEDDED\n        && false\n#endif\n    ) {\n#if defined (__unix__)')
replace('        clean_up = [&ctx_http, &ctx_server, &mcp_mgr]() {', '''        clean_up = [&ctx_http, &ctx_server, &mcp_mgr]() {
#ifdef RATIO_NATIVE_EMBEDDED
            ratio_native_unpublish();
#endif''')
path.write_text(text, encoding='utf-8')
context = root / 'tools/server/server-context.cpp'
source = context.read_text(encoding='utf-8')
source = source.replace('#include "echo-kv.h"', '#include "echo-kv.h"\n#ifdef RATIO_NATIVE_EMBEDDED\n#include "ratio-native.h"\n#endif', 1)
old = '        if (const char * value = std::getenv("ECHO_KV_BRANCHES")) echo_branches = std::atoi(value);'
if old not in source:
    raise RuntimeError('Pinned native branch integration changed')
source = source.replace(old, '#ifdef RATIO_NATIVE_EMBEDDED\n        echo_branches = ratio_native_branches();\n#else\n' + old + '\n#endif', 1)
context.write_text(source, encoding='utf-8')
queue = root / 'tools/server/server-queue.cpp'
source = queue.read_text(encoding='utf-8')
old = 'void server_queue::start_loop(int64_t idle_sleep_ms) {\n    running = true;'
if old not in source:
    raise RuntimeError('Pinned queue startup changed')
source = '#ifdef RATIO_NATIVE_EMBEDDED\n#include "ratio-native.h"\n#endif\n' + source.replace(old, old + '\n#ifdef RATIO_NATIVE_EMBEDDED\n    ratio_native_loop_ready();\n#endif', 1)
queue.write_text(source, encoding='utf-8')
for name in ('ratio-native.cpp', 'ratio-native.h', 'echo-kv.h'):
    shutil.copyfile(here / name, root / 'tools/server' / name)
cmake = root / 'tools/server/CMakeLists.txt'
with cmake.open('a', encoding='utf-8') as out:
    out.write('''
# Ratio owns this DLL; upstream model/queue code is shared with the WSL build.
target_sources(llama-server-impl PRIVATE ratio-native.cpp)
target_include_directories(llama-server-impl PRIVATE "${PROJECT_SOURCE_DIR}/vendor")
target_compile_definitions(llama-server-impl PRIVATE RATIO_NATIVE_EMBEDDED)
target_compile_definitions(server-context PRIVATE RATIO_NATIVE_EMBEDDED)
set_target_properties(llama-server-impl PROPERTIES OUTPUT_NAME ratio-inference)
''')
