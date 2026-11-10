#pragma once
#include "server-http.h"
#include <functional>

// RATIO-NATIVE-001. Only the transport changes: handlers still submit to the
// original inference queue, including Echo's batch/append/cancellation logic.
void ratio_native_publish(server_http_context & http, std::function<void()> stop);
void ratio_native_unpublish();
void ratio_native_loop_ready();
int ratio_native_branches();
