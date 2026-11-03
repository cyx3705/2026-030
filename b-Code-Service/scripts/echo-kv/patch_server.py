"""Apply reviewed integration points to a disposable matching upstream tree."""
from pathlib import Path
import sys

root = Path(sys.argv[1])

def replace(file, old, new):
    path = root / file
    text = path.read_text()
    if old not in text:
        raise RuntimeError(f"Pinned upstream integration changed: {file}: {old[:60]}")
    path.write_text(text.replace(old, new, 1))

base = 'tools/server/'
replace(base+'server-task.h', '    SERVER_TASK_TYPE_TTS,', '    SERVER_TASK_TYPE_TTS,\n    SERVER_TASK_TYPE_ECHO_KV,')
replace(base+'server-task.h', '    server_task_type type;', '''    json echo_body;
    std::shared_ptr<std::atomic<bool>> echo_cancelled;
    server_task_type type;''')
replace(base+'server-task.h', '#include <string>', '#include <string>\n#include <atomic>\n#include <memory>')
replace(base+'server-task.h', 'struct server_task_result_control :', '''struct server_task_result_echo : server_task_result {
    json data;
    json to_json() override { return data; }
};

struct server_task_result_control :''')
replace(base+'server-context.h', '    server_http_context::handler_t post_control;',
        '    server_http_context::handler_t post_control;\n    server_http_context::handler_t post_echo_kv;')
replace(base+'server-context.cpp', '#include "server-context.h"', '#include "server-context.h"\n#include "echo-kv.h"')
replace(base+'server-context.cpp', '    common_params params_base;', '    common_params params_base;\n    echo_kv_runtime echo_kv;')
replace(base+'server-context.cpp', '        llama_init = common_init_from_params(params_base);', '''        auto echo_params = params_base;
        // Two extra sequence IDs, zero extra model loads. The public slot count
        // stays unchanged; all operations run on this same inference queue.
        echo_params.n_parallel += 2;
        echo_params.n_outputs_max = std::max(echo_params.n_outputs_max, echo_params.n_parallel);
        echo_params.kv_unified = true;
        llama_init = common_init_from_params(echo_params);''')
replace(base+'server-context.cpp', '        switch (task.type) {', '''        echo_kv.bind(ctx_tgt, params_base.n_parallel);
        echo_kv.expire();
        switch (task.type) {
            case SERVER_TASK_TYPE_ECHO_KV: {
                if (task.echo_cancelled->load()) { echo_kv.cancel_select(task.id); break; }
                auto res = std::make_unique<server_task_result_echo>();
                res->id = task.id;
                try {
                    const auto model = task.echo_body.value("model", std::string());
                    if (!model.empty() && model != model_name && model_aliases.count(model) == 0) {
                        throw std::runtime_error("model_not_served");
                    }
                    res->data = echo_kv.run(task.echo_body, task.echo_cancelled);
                    // A long B select yields a resumable task. Put it back at
                    // the queue tail so A's next-token task can run between
                    // bounded decode chunks; no HTTP response is completed
                    // until the branch has selected and released B.
                    if (res->data.value("pending", false)) {
                        queue_tasks.post(std::move(task));
                        break;
                    }
                    // Cancellation may win after the last decode but before the
                    // HTTP caller receives a newly allocated handle.
                    if (task.echo_cancelled->load() && task.echo_body.value("op", "") == "create") echo_kv.clear();
                    queue_results.send(std::move(res));
                } catch (const std::exception & e) {
                    echo_kv.cancel_select(task.id);
                    send_error(task, e.what(), ERROR_TYPE_INVALID_REQUEST);
                }
            } break;''')
replace(base+'server-context.cpp', '    this->post_slots =', '''    this->post_echo_kv = [this](const server_http_req & req) {
        auto res = create_response();
        auto cancelled = std::make_shared<std::atomic<bool>>(false);
        auto body = json::parse(req.body);
        if (body.value("op", "") == "stream") {
            const int limit = body.value("max_tokens", 1024);
            if (limit < 1 || limit > 8192) throw std::runtime_error("invalid_stream_limit");
            auto * output = res.get();
            res->content_type = "text/event-stream";
            // A single persistent response. Each step is submitted locally,
            // ahead of resumable B slices, without a client HTTP round trip.
            res->next = [this, &req, output, cancelled, body, limit, count = 0](std::string & chunk) mutable {
                if (req.should_stop()) { cancelled->store(true); return false; }
                server_response_reader reader(output->rd.queue_tasks, output->rd.queue_results,
                    output->rd.polling_interval_seconds);
                server_task step(SERVER_TASK_TYPE_ECHO_KV);
                step.id = reader.get_new_id();
                step.echo_body = body;
                step.echo_body["op"] = count < limit ? "next" : "finish";
                step.echo_body["_task_id"] = step.id;
                step.echo_cancelled = cancelled;
                reader.post_task(std::move(step), true);
                auto result = reader.next([&]() {
                    if (req.should_stop()) { cancelled->store(true); return true; }
                    return false;
                });
                if (!result) { cancelled->store(true); return false; }
                if (result->is_error()) {
                    chunk = "data: " + json({{"error", result->to_json()}}).dump() + "\\n\\n";
                    return false;
                }
                auto data = result->to_json();
                const bool done = count >= limit || data.value("done", false);
                ++count;
                if (done) data["done"] = true;
                chunk = "data: " + data.dump() + "\\n\\n";
                if (done) chunk += "data: [DONE]\\n\\n";
                return !done;
            };
            return res;
        }
        server_task task(SERVER_TASK_TYPE_ECHO_KV);
        task.id = res->rd.get_new_id();
        task.echo_body = json::parse(req.body);
        task.echo_body["_task_id"] = task.id;
        task.echo_cancelled = cancelled;
        res->rd.post_task(std::move(task));
        auto result = res->rd.next([&]() {
            if (req.should_stop()) { cancelled->store(true); return true; }
            return false;
        });
        if (!result) { cancelled->store(true); return res; }
        if (result->is_error()) res->error(result->to_json());
        else res->ok(result->to_json());
        return res;
    };

    this->post_slots =''')
replace(base+'server.cpp', '    ctx_http.post("/completion",',
        '    ctx_http.post("/v1/kv/op", ex_wrapper(routes.post_echo_kv));\n    ctx_http.post("/completion",')

# Snapshots run on the same inference queue and can block it for seconds.
# Refuse maintenance atomically if any public task or Echo A/B lease owns it;
# a Python-side idle probe alone has a race with a newly created A.
for action in ('SAVE', 'RESTORE'):
    marker = '            case SERVER_TASK_TYPE_SLOT_' + action + ':\n                {'
    replace(base+'server-context.cpp', marker, marker + '''
                    if (!echo_kv.sequence.empty() || std::any_of(slots.begin(), slots.end(),
                            [](const server_slot & s) { return s.is_processing(); })) {
                        send_error(task, "model_busy_for_snapshot", ERROR_TYPE_INVALID_REQUEST);
                        break;
                    }
''')
