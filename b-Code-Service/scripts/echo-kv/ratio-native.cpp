#include "ratio-native.h"
#include "nlohmann/json.hpp"
#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <map>
#include <mutex>
#include <thread>

#ifdef _WIN32
#define RATIO_API extern "C" __declspec(dllexport)
#else
#define RATIO_API extern "C" __attribute__((visibility("default")))
#endif
using json = nlohmann::json;
int llama_server(int argc, char ** argv);
namespace {
std::mutex gate;
std::condition_variable changed;
std::thread engine;
server_http_context * current = nullptr;
std::function<void()> stop_engine;
std::map<uint64_t, std::shared_ptr<std::atomic<bool>>> requests;
bool exited = true;
bool stopping = false;
bool loop_ready = false;
int exit_code = 0;
int branches = 1;
// Callback: kind 0 = status + content type, kind 1 = body bytes. Return 0 to
// cancel. Buffers are borrowed only for the duration of the callback.
using callback_t = int (*)(void *, int, int, const char *, size_t);
}

int ratio_native_branches() { return branches; }

void ratio_native_publish(server_http_context & http, std::function<void()> stop) {
    std::lock_guard<std::mutex> lock(gate);
    current = &http;
    stop_engine = std::move(stop);
    changed.notify_all();
}
void ratio_native_unpublish() {
    std::unique_lock<std::mutex> lock(gate);
    current = nullptr;
    loop_ready = false;
    stop_engine = nullptr;
    for (auto & item : requests) item.second->store(true);
    changed.wait(lock, [] { return requests.empty(); });
}

void ratio_native_loop_ready() {
    std::lock_guard<std::mutex> lock(gate);
    loop_ready = true;
    // A stop requested during model loading must survive queue initialization.
    if (stopping && stop_engine) stop_engine();
    changed.notify_all();
}

RATIO_API int ratio_abi_version() { return 1; }
RATIO_API int ratio_start(const char * arguments) {
    std::lock_guard<std::mutex> lock(gate);
    if (engine.joinable() || !exited) return -1;
    try {
        auto args = json::parse(arguments).get<std::vector<std::string>>();
        branches = 1;
        for (auto it = args.begin(); it != args.end(); ++it) {
            if (*it == "--ratio-kv-branches") {
                auto next = std::next(it);
                if (next == args.end()) return -2;
                branches = std::stoi(*next);
                if (branches < 1 || branches > 64) return -2;
                args.erase(it, std::next(next));
                break;
            }
        }
        args.insert(args.begin(), "ratio-inference");
        stopping = false; loop_ready = false; exited = false; exit_code = 0;
        engine = std::thread([args = std::move(args)]() mutable {
            int code = -1;
            try {
                std::vector<char *> argv;
                for (auto & arg : args) argv.push_back(arg.data());
                code = llama_server((int)argv.size(), argv.data());
            } catch (...) { code = -2; }
            std::lock_guard<std::mutex> lock(gate);
            current = nullptr; stop_engine = nullptr;
            exit_code = code; exited = true;
            changed.notify_all();
        });
        return 0;
    } catch (...) { return -2; }
}
RATIO_API int ratio_state() {
    std::lock_guard<std::mutex> lock(gate);
    return exited ? (exit_code == 0 ? 0 : -1) : current && loop_ready ? 2 : 1;
}
RATIO_API void ratio_cancel(uint64_t id) {
    std::lock_guard<std::mutex> lock(gate);
    auto found = requests.find(id);
    if (found != requests.end()) found->second->store(true);
}
RATIO_API int ratio_request(uint64_t id, const char * method, const char * path,
                           const char * body, callback_t callback, void * context) {
    auto cancelled = std::make_shared<std::atomic<bool>>(false);
    server_http_context::handler_t handler;
    std::string route(path);
    std::map<std::string, std::string> params;
    {
        std::lock_guard<std::mutex> lock(gate);
        if (!current || !loop_ready || stopping || !requests.emplace(id, cancelled).second) return -1;
        if (route.rfind("/slots/", 0) == 0) {
            auto end = route.find('?');
            params["id_slot"] = route.substr(7, end == std::string::npos ? end : end - 7);
            if (end != std::string::npos) params["action"] = route.substr(end + 8);
            route = "/slots/:id_slot";
        }
        // Fixed protocol surface. No upload, router, MCP or remote fetch API.
        const bool get = std::string(method) == "GET";
        const bool allowed = get ? (route == "/health" || route == "/v1/models" || route == "/slots" || route == "/props")
            : std::string(method) == "POST" && (route == "/v1/kv/op" || route == "/completion" ||
              route == "/v1/chat/completions" || route == "/v1/completions" || route == "/v1/embeddings" ||
              route == "/tokenize" || route == "/detokenize" || route == "/apply-template" || route == "/slots/:id_slot");
        auto found = current->handlers.find(route);
        if (allowed && found != current->handlers.end()) handler = found->second;
    }
    int code = 0;
    try {
        if (!handler) throw std::runtime_error("unsupported_native_route");
        const std::function<bool()> stop = [cancelled, callback, context] {
            if (!cancelled->load() && !callback(context, 3, 0, nullptr, 0)) cancelled->store(true);
            return cancelled->load();
        };
        server_http_req req{params, {}, path, "", body, {}, stop};
        auto res = handler(req);
        if (!callback(context, 0, res->status, res->content_type.data(), res->content_type.size())) cancelled->store(true);
        if (res->is_stream()) {
            while (!cancelled->load()) {
                std::string chunk;
                const bool more = res->next(chunk);
                if (!chunk.empty() && !callback(context, 1, res->status, chunk.data(), chunk.size())) cancelled->store(true);
                if (!more) break;
            }
        } else if (!cancelled->load()) {
            callback(context, 1, res->status, res->data.data(), res->data.size());
        }
        res->on_complete();
    } catch (const std::exception & error) {
        auto message = json({{"error", {{"message", error.what()}}}}).dump();
        const std::string type = "application/json";
        callback(context, 0, 500, type.data(), type.size());
        callback(context, 1, 500, message.data(), message.size());
        code = -2;
    } catch (...) { code = -3; }
    {
        std::lock_guard<std::mutex> lock(gate);
        requests.erase(id);
        changed.notify_all();
    }
    return code;
}
// Never unload a DLL while this returns 1. Loading may be in an uninterruptible
// CUDA call; stop remains pending and the managed owner retains the library.
RATIO_API int ratio_stop(int timeout_ms) {
    std::unique_lock<std::mutex> lock(gate);
    stopping = true;
    for (auto & item : requests) item.second->store(true);
    if (!changed.wait_for(lock, std::chrono::milliseconds(timeout_ms), [] { return requests.empty(); })) return 1;
    if (!exited && !stop_engine) return 1;
    if (stop_engine && loop_ready) stop_engine();
    if (!changed.wait_for(lock, std::chrono::milliseconds(timeout_ms), [] { return exited; })) return 1;
    if (engine.joinable()) engine.join();
    return 0;
}
