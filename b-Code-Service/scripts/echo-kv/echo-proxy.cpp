#include <cpp-httplib/httplib.h>
#include <nlohmann/json.hpp>

#include <array>
#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <deque>
#include <iostream>
#include <mutex>
#include <thread>
#include <sstream>
#include <string>
#include <vector>

using json = nlohmann::json;

namespace {

const char * env_or(const char * name, const char * fallback) {
    const char * value = std::getenv(name);
    return value && *value ? value : fallback;
}

std::string shell_quote(const std::string & value) {
    std::string result = "'";
    for (char c : value) {
        if (c == '\'') result += "'\\''";
        else result += c;
    }
    return result + "'";
}

std::string run_command(const std::string & command) {
    std::array<char, 4096> buffer{};
    std::string output;
    FILE * pipe = popen(command.c_str(), "r");
    if (!pipe) return {};
    while (fgets(buffer.data(), static_cast<int>(buffer.size()), pipe)) output += buffer.data();
    pclose(pipe);
    return output;
}

json read_json_file(const std::string & path, const json & fallback) {
    try {
        std::ifstream stream(path);
        if (!stream) return fallback;
        json value;
        stream >> value;
        return value;
    } catch (...) {
        return fallback;
    }
}

httplib::Headers forward_headers(const httplib::Request & request) {
    httplib::Headers headers;
    for (const auto & item : request.headers) {
        std::string name = item.first;
        for (auto & ch : name) ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
        if (name == "host" || name == "content-length" || name == "connection" ||
            name == "transfer-encoding" || name == "keep-alive" || name == "accept-encoding" ||
            name == "upgrade" || name == "te" || name == "trailer" ||
            name == "proxy-authorization" || name == "proxy-authenticate") continue;
        headers.emplace(name, item.second);
    }
    return headers;
}

// Owns exactly one upstream request, with bounded buffering and cancellation
// from before response headers through the final downstream write.
struct Exchange {
    explicit Exchange(const std::string & upstream) : client(upstream) {
        client.set_connection_timeout(2, 0);
        client.set_read_timeout(1800, 0);
        client.set_write_timeout(30, 0);
        client.set_tcp_nodelay(true);
    }
    ~Exchange() { cancel(); if (worker.joinable()) worker.join(); if (monitor.joinable()) monitor.join(); }
    void cancel() { cancelled.store(true); cv.notify_all(); client.stop(); }
    httplib::Client client;
    std::thread worker, monitor;
    std::atomic<bool> cancelled{false}, finished{false};
    std::mutex mutex;
    std::condition_variable cv;
    bool headers_ready = false;
    int status = 502;
    httplib::Headers headers;
    std::deque<std::string> chunks;
    size_t buffered = 0;
    std::string error;
};

class EchoProxy {
public:
    EchoProxy() : upstream_(env_or("LOCAL_AI_UPSTREAM", "http://127.0.0.1:18769")),
                  root_(env_or("LOCAL_AI_ROOT", ".")) {}

    void install(httplib::Server & server) {
        server.Get("/v1/echo/capabilities", [this](const httplib::Request &, httplib::Response & response) {
            response.set_content(capabilities().dump(), "application/json; charset=utf-8");
        });
        server.Get("/v1/echo/models", [this](const httplib::Request &, httplib::Response & response) {
            response.set_content(inventory(), "application/json; charset=utf-8");
        });
        server.Get("/v1/echo/model", [this](const httplib::Request &, httplib::Response & response) {
            response.set_content(model_job(), "application/json; charset=utf-8");
        });
        server.Post("/v1/echo/model", [this](const httplib::Request & request, httplib::Response & response) {
            switch_model(request, response);
        });
        server.Get("/cache/stats", [this](const httplib::Request &, httplib::Response & response) {
            json stats = { {"backend", "cpp-echo-proxy"}, {"upstream", upstream_},
                           {"disk_cache_kind", "provider-owned"},
                           {"request_disk_alignment", false}, {"legacy_python_metrics_active", false},
                           {"kv_disk_cache", read_json_file(root_ + "/status/kv-disk-cache.json", json({{"state", "not_started"}}))} };
            response.set_content(stats.dump(), "application/json; charset=utf-8");
        });

        // The high-frequency endpoints are deliberately forwarded without
        // prompt rewriting, JSON parsing, or Python-side stream inspection.
        auto post = [this](const httplib::Request & request, httplib::Response & response) {
            forward(request, response);
        };
        server.Post("/v1/chat/completions", post);
        server.Post("/v1/responses", post);
        server.Post("/v1/kv/op", post);
        server.Post("/completion", post);
        server.Post(R"(/v1/.*)", post);
        server.Post(R"(/.*)", post);
        server.Get(R"(/.*)", [this](const httplib::Request & request, httplib::Response & response) {
            forward(request, response);
        });
    }

private:
    json capabilities() const {
        json result = {
            {"protocol", "echo-external-http"}, {"version", 1},
            {"model_lifecycle", "provider_owned"}, {"shared_model", true},
            {"chat", {{"endpoint", "/v1/chat/completions"}, {"stream", true}, {"json_schema", true}}},
            {"prediction", {{"endpoint", "/completion"}, {"raw_prompt", true},
                             {"probabilities", "completion_probabilities[].top_logprobs"}}},
            {"cancellation", {{"streaming", true}, {"non_streaming", true}}},
            {"kv", {{"fork_at", false}, {"sequence_handles", false}, {"cache_owner", "llama-server"},
                    {"disk_fork_matching", false}}}, {"transport", "cpp-echo-proxy"}
        };
        if (native_supported()) {
            httplib::Client client(upstream_);
            client.set_connection_timeout(2, 0);
            client.set_read_timeout(5, 0);
            auto response = client.Post("/v1/kv/op", "{\"op\":\"capabilities\"}", "application/json");
            try {
                auto native = response && response->status == 200 ? json::parse(response->body) : json::object();
                if (native.value("supported", false) && native.value("shared_prefix", false) &&
                    native.value("fork_at", false) && native.value("label_constraint", false) && native.value("release", false)) {
                    result["kv"].update({{"fork_at", true}, {"sequence_handles", true}, {"endpoint", "/v1/kv/op"},
                        {"hybrid_checkpoints", true}, {"branch_batch_size", native.at("branch_batch_size")},
                        {"max_tokens", native.at("max_tokens")}, {"provider_parallel_slots", native.value("provider_parallel_slots", 1)}});
                }
            } catch (...) {}
        }
        return result;
    }

    bool native_supported() const {
        const auto selected = read_json_file(root_ + "/status/active-model.json", json::object());
        const auto config = read_json_file(root_ + "/config/echo-models.json", json::object());
        const auto name = selected.value("name", config.value("default", std::string()));
        for (const auto & model : config.value("models", json::array())) {
            if (model.value("name", std::string()) == name) return model.value("shared_kv", true);
        }
        return false;
    }

    std::string inventory() const {
        const std::string script = root_ + "/scripts/model_switch.py";
        std::string output = run_command("python3 " + shell_quote(script) + " inventory 2>/dev/null");
        if (output.empty()) return json({{"managed", false}, {"models", json::array()}}).dump();
        return output;
    }

    std::string prepare_chat(const httplib::Request & request) const {
        // Preserve the existing OpenClaw cache-stable system/tool surface.
        // Echo's requests bypass this control-client-only transformation.
        if (request.path != "/v1/chat/completions" || request.get_header_value("X-Local-AI-Client") == "echo") return request.body;
        try {
            auto body = json::parse(request.body);
            auto & messages = body.at("messages");
            if (!messages.is_array() || messages.empty() || messages[0].value("role", "") != "system" ||
                !messages[0].at("content").is_string()) return request.body;
            auto content = messages[0]["content"].get<std::string>();
            if (content.find("## OpenClaw Control") == std::string::npos ||
                content.find("## Workspace Files (injected)") == std::string::npos) return request.body;
            bool tail_moved = false;
            auto boundary = content.find("\n## Temporal Context\n");
            if (boundary != std::string::npos) {
                messages.push_back({{"role", "system"}, {"content", content.substr(boundary)}});
                content.resize(boundary);
                messages[0]["content"] = content;
                tail_moved = true;
            }
            const std::string footer = "The AGENTS.md Tools section guides usage; it never grants availability.";
            const auto start = content.find("## Tooling\n");
            const auto end = start == std::string::npos ? std::string::npos : content.find(footer, start);
            if (end != std::string::npos) {
                const std::string before = content.substr(0, start), tooling = content.substr(start, end + footer.size() - start),
                                  after = content.substr(end + footer.size());
                const auto tools = body.value("tools", json::array());
                auto names = [](const json & list, const std::string & text) {
                    std::vector<std::string> result;
                    std::istringstream lines(text);
                    std::string line;
                    auto add = [&](const std::string & name) { if (!name.empty() && std::find(result.begin(), result.end(), name) == result.end()) result.push_back(name); };
                    while (std::getline(lines, line)) if (line.rfind("- ", 0) == 0) {
                        const auto split = line.find_first_of(" (:", 2);
                        if (split != std::string::npos) add(line.substr(2, split - 2));
                    }
                    for (const auto & tool : list) {
                        const auto function = tool.value("function", json::object());
                        add(function.value("name", tool.value("name", std::string())));
                    }
                    return result;
                };
                const auto current = names(tools, tooling);
                std::lock_guard<std::mutex> lock(surface_mutex_);
                if (surface_.empty() || surface_.at("before") != before || surface_.at("after") != after) {
                    surface_ = {{"before", before}, {"after", after}, {"tooling", tooling}, {"tools", tools}, {"names", current}};
                } else {
                    const auto previous = surface_.at("names").get<std::vector<std::string>>();
                    std::vector<std::string> added, removed;
                    for (const auto & name : current) if (std::find(previous.begin(), previous.end(), name) == previous.end()) added.push_back(name);
                    for (const auto & name : previous) if (std::find(current.begin(), current.end(), name) == current.end()) removed.push_back(name);
                    if (!added.empty() || !removed.empty()) {
                        std::string suffix = "## Tool Surface Update\nThe tool list above is the cached list. Apply only this change for the current turn.\n";
                        for (const auto & name : removed) suffix += "Unavailable now: " + name + ". Do not call an unavailable tool.\n";
                        if (!added.empty()) suffix += "Additional tools:\n";
                        std::istringstream lines(tooling);
                        std::string line;
                        while (std::getline(lines, line)) for (const auto & name : added) if (line.rfind("- " + name, 0) == 0) { suffix += line + "\n"; break; }
                        for (const auto & tool : tools) {
                            auto name = tool.value("function", json::object()).value("name", tool.value("name", std::string()));
                            if (std::find(added.begin(), added.end(), name) != added.end()) suffix += tool.dump() + "\n";
                        }
                        suffix += "Call an additional tool in the normal tool-call format.";
                        if (suffix.size() <= 8000) {
                            messages[0]["content"] = before + surface_.at("tooling").get<std::string>() + after;
                            body["tools"] = surface_.at("tools");
                            messages.insert(tail_moved ? messages.end()-1 : messages.end(), json({{"role", "system"}, {"content", suffix}}));
                        } else surface_ = {{"before", before}, {"after", after}, {"tooling", tooling}, {"tools", tools}, {"names", current}};
                    }
                }
            }
            if (!body.contains("cache_prompt")) body["cache_prompt"] = true;
            if (body.value("stream", false)) body["stream_options"]["include_usage"] = true;
            return body.dump();
        } catch (...) { return request.body; }
    }

    std::string model_job() const {
        return control("http-job").at("body").dump();
    }

    json control(const std::string & action, const std::string & name = "") const {
        auto output = run_command("python3 " + shell_quote(root_ + "/scripts/model_switch.py") +
            " " + action + " " + shell_quote(name) + " 2>/dev/null");
        try { return json::parse(output); }
        catch (...) { return {{"status", 503}, {"body", {{"error", {{"message", "model control unavailable"}}}}}}; }
    }

    void switch_model(const httplib::Request & request, httplib::Response & response) const {
        if (request.has_header("Origin") || request.body.size() > 4096 ||
            request.get_header_value("Content-Type").find("application/json") != 0) {
            response.status = 403;
            response.set_content("{\"error\":{\"message\":\"模型切换仅接受本机应用的 JSON 请求\"}}", "application/json");
            return;
        }
        try {
            const auto body = json::parse(request.body);
            const std::string name = body.at("name").get<std::string>();
            const auto result = control("http-start", name);
            response.status = result.at("status");
            response.set_content(result.at("body").dump(), "application/json; charset=utf-8");
        } catch (const std::exception & error) {
            response.status = 400;
            response.set_content(json({{"error", {{"message", error.what()}}}}).dump(), "application/json; charset=utf-8");
        }
    }

    void forward(const httplib::Request & request, httplib::Response & response) const {
        json operation;
        if (request.path == "/v1/kv/op") {
            try { operation = json::parse(request.body); } catch (...) {}
        }
        const auto op = operation.is_object() ? operation.value("op", std::string()) : std::string();
        const auto job = read_json_file(root_ + "/status/model-switch.json", json::object());
        bool switching = job.value("state", std::string()) == "switching";
        if (switching && job.contains("pid") && job.contains("updated_at")) {
            const double now = std::chrono::duration<double>(std::chrono::system_clock::now().time_since_epoch()).count();
            if (now - job.at("updated_at").get<double>() > 10) {
                std::ifstream command("/proc/" + std::to_string(job.at("pid").get<int>()) + "/cmdline");
                const std::string text((std::istreambuf_iterator<char>(command)), std::istreambuf_iterator<char>());
                if (text.find("model_switch.py") == std::string::npos || text.find(job.value("job_id", std::string())) == std::string::npos) switching = false;
            }
        }
        if (switching && op != "release" && op != "release_branch") {
            response.status = 503;
            response.set_content("{\"error\":{\"message\":\"统一服务正在更换模型，请稍后重试\",\"type\":\"model_switching\"}}", "application/json");
            return;
        }
        if (request.path == "/v1/kv/op" && !native_supported()) {
            response.status = op == "capabilities" || op == "runtime" ? 200 : 501;
            response.set_content(json({{"backend", "external-http"}, {"supported", false},
                {"shared_prefix", false}, {"fork_at", false}, {"label_constraint", false}, {"release", false},
                {"reason", "此模型使用兼容选义，滑动窗口原生检查点尚未适配"}}).dump(), "application/json");
            return;
        }
        auto exchange = std::make_shared<Exchange>(upstream_);
        auto * state = exchange.get();
        auto closed = request.is_connection_closed;
        exchange->worker = std::thread([state, method = request.method,
                path = request.target.empty() ? request.path : request.target,
                headers = forward_headers(request), body = prepare_chat(request), closed]() {
            if (!closed() && !state->cancelled.load()) {
                httplib::Request upstream;
                upstream.method = method;
                upstream.path = path;
                upstream.headers = headers;
                upstream.body = body;
                upstream.response_handler = [state](const httplib::Response & received) {
                    std::lock_guard<std::mutex> lock(state->mutex);
                    state->status = received.status;
                    state->headers = received.headers;
                    state->headers_ready = true;
                    state->cv.notify_all();
                    return !state->cancelled.load();
                };
                upstream.content_receiver = [state](const char * bytes, size_t size, size_t, size_t) {
                    std::unique_lock<std::mutex> lock(state->mutex);
                    state->cv.wait(lock, [state] { return state->buffered < 65536 || state->cancelled.load(); });
                    if (state->cancelled.load()) return false;
                    state->chunks.emplace_back(bytes, size);
                    state->buffered += size;
                    state->cv.notify_all();
                    return true;
                };
                auto result = state->client.send(upstream);
                if (!result) {
                    std::lock_guard<std::mutex> lock(state->mutex);
                    state->error = httplib::to_string(result.error());
                }
            }
            state->finished.store(true);
            state->cv.notify_all();
        });
        exchange->monitor = std::thread([state, closed]() {
            std::unique_lock<std::mutex> lock(state->mutex);
            while (!state->cancelled.load()) {
                if (closed()) { lock.unlock(); state->cancel(); return; }
                state->cv.wait_for(lock, std::chrono::milliseconds(5));
            }
        });
        std::unique_lock<std::mutex> lock(exchange->mutex);
        exchange->cv.wait(lock, [&] { return exchange->headers_ready || exchange->finished.load() || exchange->cancelled.load(); });
        if (!exchange->headers_ready) {
            response.status = 502;
            response.set_content(json({{"error", {{"message", exchange->error.empty() ? "upstream_cancelled" : exchange->error}}}}).dump(), "application/json");
            return;
        }
        response.status = exchange->status;
        httplib::Request header_request;
        header_request.headers = exchange->headers;
        for (const auto & item : forward_headers(header_request)) response.set_header(item.first, item.second);
        const std::string content_type = header_request.get_header_value("Content-Type", "application/json");
        lock.unlock();
        response.set_chunked_content_provider(content_type,
            [exchange](size_t, httplib::DataSink & sink) {
                std::unique_lock<std::mutex> guard(exchange->mutex);
                exchange->cv.wait(guard, [&] { return !exchange->chunks.empty() || exchange->finished.load() || exchange->cancelled.load(); });
                if (!exchange->chunks.empty()) {
                    auto chunk = std::move(exchange->chunks.front());
                    exchange->chunks.pop_front();
                    exchange->buffered -= chunk.size();
                    exchange->cv.notify_all();
                    guard.unlock();
                    if (!sink.write(chunk.data(), chunk.size())) { exchange->cancel(); return false; }
                    return true;
                }
                if (!exchange->error.empty() || exchange->cancelled.load()) return false;
                sink.done();
                return true;
            });
    }

    std::string upstream_;
    std::string root_;
    mutable std::mutex surface_mutex_;
    mutable json surface_;
};

} // namespace

int main(int argc, char ** argv) {
    const int port = argc > 1 ? std::atoi(argv[1]) : 11435;
    httplib::Server server;
    EchoProxy proxy;
    proxy.install(server);
    server.set_tcp_nodelay(true);
    std::cout << "C++ Echo proxy listening on 127.0.0.1:" << port << std::endl;
    return server.listen("127.0.0.1", port) ? 0 : 1;
}
