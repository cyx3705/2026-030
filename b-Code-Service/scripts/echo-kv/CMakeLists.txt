cmake_minimum_required(VERSION 3.16)
project(echo-shared-kv LANGUAGES C CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_BUILD_TYPE Release)
file(GLOB SERVER_SOURCES "${UPSTREAM}/tools/server/*.cpp")
add_executable(llama-server ${SERVER_SOURCES})
target_include_directories(llama-server PRIVATE "${UPSTREAM}" "${UPSTREAM}/include"
  "${UPSTREAM}/ggml/include" "${UPSTREAM}/common" "${UPSTREAM}/vendor"
  "${UPSTREAM}/tools/mtmd" "${UPSTREAM}/tools/server" "${CMAKE_CURRENT_SOURCE_DIR}"
  "${CMAKE_CURRENT_BINARY_DIR}")
# Web UI is disabled in production; generate its official empty asset header.
execute_process(COMMAND ${CMAKE_COMMAND} -DUI_SOURCE_DIR=${UPSTREAM}/tools/ui
  -DUI_BINARY_DIR=${CMAKE_CURRENT_BINARY_DIR} -DLLAMA_SOURCE_DIR=${UPSTREAM}
  -DBUILD_UI=OFF -DHF_ENABLED=OFF -P ${UPSTREAM}/scripts/ui-assets.cmake
  COMMAND_ERROR_IS_FATAL ANY)
target_sources(llama-server PRIVATE "${CMAKE_CURRENT_BINARY_DIR}/ui.cpp")
target_link_directories(llama-server PRIVATE "${RUNTIME_LIB}")
target_link_libraries(llama-server PRIVATE llama-common llama mtmd ggml ggml-base pthread dl)
set_target_properties(llama-server PROPERTIES BUILD_RPATH "${RUNTIME_LIB}")

# C++ edge proxy for Echo.  It keeps the model in llama-server while removing
# the Python HTTP hop from the hot inference/KV/SSE path.
add_executable(echo-proxy echo-proxy.cpp
  "${UPSTREAM}/vendor/cpp-httplib/httplib.cpp")
target_include_directories(echo-proxy PRIVATE "${UPSTREAM}/vendor"
  "${UPSTREAM}/vendor/nlohmann" "${UPSTREAM}/common")
target_link_libraries(echo-proxy PRIVATE pthread dl)
set_target_properties(echo-proxy PROPERTIES BUILD_RPATH "${RUNTIME_LIB}")
