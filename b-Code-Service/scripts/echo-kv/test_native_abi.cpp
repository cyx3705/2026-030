// RATIO-NATIVE-001: actual C ABI/thread/callback contract with a fake model.
// Compiles against the pinned upstream HTTP types, without loading a GPU.
#include "ratio-native.h"
#include <cassert>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <mutex>
#include <condition_variable>
#include <thread>

extern "C" int ratio_abi_version();
extern "C" int ratio_start(const char *);
extern "C" int ratio_state();
extern "C" int ratio_stop(int);
extern "C" void ratio_cancel(uint64_t);
using callback_t = int (*)(void *, int, int, const char *, size_t);
extern "C" int ratio_request(uint64_t, const char *, const char *, const char *, callback_t, void *);

class server_http_context::Impl {};
server_http_context::server_http_context() = default;
server_http_context::~server_http_context() = default;
std::mutex model_gate;
std::condition_variable model_changed;
bool model_stopped;
std::atomic<bool> slow_load{false};
int llama_server(int, char **) {
    server_http_context http;
    model_stopped = false;
    if (slow_load.load()) std::this_thread::sleep_for(std::chrono::milliseconds(50));
    http.handlers["/v1/models"] = [](const server_http_req &) {
        auto res = std::make_unique<server_http_res>();
        res->data = "{\"model\":\"test\"}";
        return res;
    };
    http.handlers["/v1/kv/op"] = [](const server_http_req & req) {
        auto res = std::make_unique<server_http_res>();
        res->content_type = "text/event-stream";
        res->next = [&req, count = 0](std::string & chunk) mutable {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
            if (req.should_stop()) return false;
            chunk = "data: token\n\n";
            return ++count < 1000;
        };
        return res;
    };
    ratio_native_publish(http, [] {
        std::lock_guard<std::mutex> lock(model_gate);
        model_stopped = true;
        model_changed.notify_all();
    });
    ratio_native_loop_ready();
    std::unique_lock<std::mutex> lock(model_gate);
    model_changed.wait(lock, [] { return model_stopped; });
    lock.unlock();
    ratio_native_unpublish();
    return 0;
}
struct received { int headers = 0, chunks = 0; bool cancel = false; std::string text; };
int callback(void * ptr, int kind, int status, const char * data, size_t size) {
    auto & result = *static_cast<received *>(ptr);
    if (result.cancel) return 0;
    if (kind == 0) { assert(status == 200); ++result.headers; }
    if (kind == 1) { ++result.chunks; result.text.append(data, size); }
    return kind != 1 || result.chunks < 3;
}
int main() {
    assert(ratio_abi_version() == 1);
    for (int cycle = 0; cycle < 3; ++cycle) {
        assert(ratio_start("[\"--ratio-kv-branches\",\"32\"]") == 0);
        for (int i = 0; ratio_state() == 1 && i < 1000; ++i) std::this_thread::sleep_for(std::chrono::milliseconds(1));
        assert(ratio_state() == 2 && ratio_native_branches() == 32);
        assert(ratio_start("[]") == -1); // No second model instance.
        received plain;
        assert(ratio_request(1, "GET", "/v1/models", "", callback, &plain) == 0);
        assert(plain.headers == 1 && plain.text == "{\"model\":\"test\"}");
        received stream;
        assert(ratio_request(2, "POST", "/v1/kv/op", "{}", callback, &stream) == 0);
        assert(stream.headers == 1 && stream.chunks == 3); // Callback cancellation.
        received cancelled; cancelled.cancel = true;
        assert(ratio_request(3, "POST", "/v1/kv/op", "{}", callback, &cancelled) == 0);
        assert(cancelled.chunks == 0);
        received concurrent;
        std::thread reader([&] { ratio_request(4, "POST", "/v1/kv/op", "{}", callback, &concurrent); });
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
        ratio_cancel(4);
        assert(ratio_stop(2000) == 0);
        reader.join();
        assert(ratio_state() == 0);
    }
    slow_load.store(true);
    assert(ratio_start("[]") == 0);
    const int pending = ratio_stop(2000);
    assert(pending == 0 || pending == 1);
    for (int i = 0; ratio_state() == 1 && i < 2000; ++i) std::this_thread::sleep_for(std::chrono::milliseconds(1));
    assert(ratio_state() == 0); // Deferred stop survives model/queue startup.
    assert(ratio_stop(2000) == 0);
    return 0;
}
