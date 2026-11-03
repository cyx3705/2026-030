// ECHO-KV-001: executed exclusively on llama-server's inference queue.
// Attention cells are shared. Hybrid recurrent state is restored at a checkpoint
// before replaying ONLY the missing left prefix; seq_cp alone ignores its range.
#pragma once
#include "common.h"
#include "llama.h"
#include "json.h"
#include <atomic>
#include <chrono>
#include <cmath>
#include <map>
#include <memory>
#include <random>

struct echo_kv_runtime {
    using clock = std::chrono::steady_clock;
    llama_context * ctx = nullptr;
    const llama_vocab * vocab = nullptr;
    llama_memory_t mem = nullptr;
    llama_seq_id a = 1, b = 2;
    int provider_parallel_slots = 1;
    std::string epoch = std::to_string(std::random_device{}()) + "-" + std::to_string(std::random_device{}());
    std::string sequence, branch;
    std::vector<llama_token> tokens;
    // Exact raw prompt/generated-byte ends, not re-tokenized approximations.
    std::vector<size_t> ends;
    std::map<int, std::vector<uint8_t>> checkpoints;
    std::vector<float> scores;
    int branch_prefix = 0, branch_reused = 0, branch_replayed = 0;
    bool checkpoint_verified = false;
    uint64_t serial = 0, forks = 0, releases = 0;
    bool generating = false;
    bool implicit_bos = false;
    // A select_text request is resumable.  Keeping the branch and its small
    // suffix here lets the server return to the task queue between chunks so
    // A's next-token requests can run while B is alive.
    bool select_pending = false;
    int select_owner = -1;
    int select_offset = 0;
    std::vector<llama_token> select_suffix;
    std::vector<llama_token> select_ids;
    std::vector<std::string> select_labels;
    std::string generated, pending;
    clock::time_point touched = clock::now();
    static constexpr int max_tokens = 8192, max_suffix = 2048, max_checkpoints = 8;

    static void require(bool ok, const char * message) { if (!ok) throw std::runtime_error(message); }
    void bind(llama_context * c, int first) {
        if (ctx == c) return;
        ctx = c; vocab = llama_model_get_vocab(llama_get_model(c)); mem = llama_get_memory(c);
        provider_parallel_slots = first;
        a = first; b = first + 1;
        require(llama_n_seq_max(c) >= (uint32_t)first + 2, "echo_sequences_not_reserved");
    }
    void release_branch() {
        if (branch.empty()) return;
        require(llama_memory_seq_rm(mem, b, -1, -1), "branch_release_failed");
        branch.clear(); ++releases;
        select_pending = false; select_owner = -1; select_offset = 0;
        select_suffix.clear(); select_ids.clear(); select_labels.clear();
    }
    void clear() {
        release_branch();
        if (ctx) require(llama_memory_seq_rm(mem, a, -1, -1), "sequence_release_failed");
        sequence.clear(); tokens.clear(); ends.clear(); checkpoints.clear(); scores.clear();
        generated.clear(); pending.clear(); generating = false; implicit_bos = false;
    }
    void expire() { if (!sequence.empty() && clock::now() - touched > std::chrono::seconds(90)) clear(); }
    void cancel_select(int task_id) {
        if (select_pending && select_owner == task_id) release_branch();
    }
    void validate(const json & body) {
        require(body.value("cache_epoch", "") == epoch, "stale_cache_epoch");
        require(!sequence.empty() && body.value("sequence_id", "") == sequence, "stale_sequence");
        touched = clock::now();
    }
    json handle() const { return {{"sequence_id", sequence}, {"cache_epoch", epoch}, {"prefix_tokens", tokens.size()}}; }
    void checkpoint(int n) {
        const auto flags = LLAMA_STATE_SEQ_FLAGS_PARTIAL_ONLY;
        auto size = llama_state_seq_get_size_ext(ctx, a, flags);
        require(size <= 80ull*1024*1024, "checkpoint_budget_exceeded");
        auto & data = checkpoints[n]; data.resize(size);
        require(llama_state_seq_get_data_ext(ctx, data.data(), size, a, flags) == size, "checkpoint_save_failed");
        // Keep the first checkpoint plus a bounded recent window.
        while (checkpoints.size() > max_checkpoints) checkpoints.erase(std::next(checkpoints.begin()));
    }
    void evaluate(llama_seq_id seq, const std::vector<llama_token> & input, int from, int to,
                  bool logits, const std::shared_ptr<std::atomic<bool>> & cancelled) {
        std::unique_ptr<llama_batch_ext, decltype(&llama_batch_ext_free)> batch(llama_batch_ext_init(ctx), llama_batch_ext_free);
        for (int off = from; off < to;) {
            require(!cancelled->load(), "request_cancelled");
            int stop = std::min(to, off + 128);
            llama_batch_ext_clear(batch.get());
            for (int i = off; i < stop; ++i) {
                auto idx = llama_batch_ext_add_token(batch.get(), seq, input[i]);
                llama_pos pos = i;
                require(idx >= 0 && llama_batch_ext_set_pos(batch.get(), idx, &pos), "batch_add_failed");
                require(llama_batch_ext_set_output_logits(batch.get(), idx, logits && i + 1 == to), "batch_output_failed");
            }
            require(llama_process(ctx, LLAMA_PROCESS_TYPE_DECODE, batch.get()) == 0, "decode_failed");
            off = stop;
        }
    }
    void save_scores() {
        auto p = llama_get_logits_ith(ctx, -1);
        require(p != nullptr, "logits_missing");
        scores.assign(p, p + llama_vocab_n_tokens(vocab));
    }

    int prefix_tokens_for(const std::string & text) const {
        std::string full;
        int n = 0;
        for (auto t : tokens) {
            if (implicit_bos && n == 0) { ++n; continue; }
            auto piece = common_token_to_piece(vocab, t, true);
            if (full.size() + piece.size() > text.size() || text.compare(full.size(), piece.size(), piece)) break;
            full += piece; ++n;
        }
        require(text.compare(0, full.size(), full) == 0, "prefix_mismatch");
        return n;
    }
    void fork(int prefix, const std::shared_ptr<std::atomic<bool>> & cancelled) {
        require(branch.empty(), "branch_busy");
        require(prefix >= 0 && prefix <= (int)tokens.size(), "invalid_prefix");
        require(llama_memory_seq_rm(mem, b, -1, -1), "branch_clear_failed");
        int reused = 0;
        checkpoint_verified = false;
        try {
        auto it = checkpoints.upper_bound(prefix);
        if (it != checkpoints.begin()) {
            --it; reused = it->first;
            // seq_cp shares attention through the requested position, but its
            // recurrent half ignores that range. Replace B's recurrent state
            // immediately, before any decode, without touching shared attention.
            const auto & data = it->second;
            llama_memory_seq_cp(mem, a, b, 0, reused);
            require(llama_state_seq_set_data_ext(ctx, data.data(), data.size(), b,
                    LLAMA_STATE_SEQ_FLAGS_PARTIAL_ONLY) == data.size(), "checkpoint_restore_failed");
            // Verify the actual restored recurrent bytes, independent of
            // numerical drift from MoE/Q8 kernels with different batch shapes.
            std::vector<uint8_t> restored(llama_state_seq_get_size_ext(ctx, b, LLAMA_STATE_SEQ_FLAGS_PARTIAL_ONLY));
            require(restored.size() == data.size(), "restored_checkpoint_size_mismatch");
            require(llama_state_seq_get_data_ext(ctx, restored.data(), restored.size(), b,
                    LLAMA_STATE_SEQ_FLAGS_PARTIAL_ONLY) == restored.size(), "restored_checkpoint_read_failed");
            // The official host-state envelope contains magic + source seq_id.
            // Normalize that identity field; compare every state payload byte.
            require(restored.size() >= 8, "checkpoint_header_missing");
            std::copy(data.begin()+4, data.begin()+8, restored.begin()+4);
            require(restored == data, "restored_checkpoint_bytes_mismatch");
            require(llama_memory_seq_pos_max(mem, b) == reused - 1, "restored_prefix_position_mismatch");
            checkpoint_verified = true;
        }
            evaluate(b, tokens, reused, prefix, false, cancelled);
            require(llama_memory_seq_pos_max(mem, b) == prefix - 1, "fork_position_mismatch");
            branch = "b" + std::to_string(++serial); ++forks;
            branch_prefix = prefix; branch_reused = reused; branch_replayed = prefix - reused;
        } catch (...) { llama_memory_seq_rm(mem, b, -1, -1); throw; }
    }
    json decode(const json & job, const std::shared_ptr<std::atomic<bool>> & cancelled) {
        require(job.value("budget", 1) == 1, "single_decision_required");
        auto suffix = job.at("suffix_tokens").get<std::vector<llama_token>>();
        auto labels = job.at("allowed_labels").get<std::vector<std::string>>();
        require(!suffix.empty() && suffix.size() <= max_suffix, "invalid_suffix_size");
        require(!labels.empty() && labels.size() <= 53, "invalid_labels");
        std::vector<llama_token> ids;
        for (auto & label : labels) {
            require(!label.empty() && label.size() <= 4, "invalid_label");
            auto t = common_tokenize(vocab, label, false, false);
            require(t.size() == 1 && std::find(ids.begin(), ids.end(), t[0]) == ids.end(), "label_not_single_distinct_token");
            ids.push_back(t[0]);
        }
        // Preserve absolute positions in B while evaluating only its suffix.
        std::vector<llama_token> input(branch_prefix, 0); input.insert(input.end(), suffix.begin(), suffix.end());
        evaluate(b, input, branch_prefix, input.size(), true, cancelled);
        auto p = llama_get_logits_ith(ctx, -1); require(p != nullptr, "logits_missing");
        size_t best = 0; json logits = json::array();
        for (size_t i = 0; i < ids.size(); ++i) {
            require(std::isfinite(p[ids[i]]), "nonfinite_label_logit");
            logits.push_back(p[ids[i]]);
            if (p[ids[i]] > p[ids[best]]) best = i;
        }
        return {{"label", labels[best]}, {"decision_steps", 1}, {"label_logits", logits},
                {"shared_prefix_tokens", branch_reused}, {"replayed_tokens", branch_replayed},
                {"recurrent_checkpoint_verified", checkpoint_verified},
                {"suffix_tokens", suffix.size()}};
    }
    void prepare_select(const json & job) {
        require(job.value("budget", 1) == 1, "single_decision_required");
        select_suffix = job.at("suffix_tokens").get<std::vector<llama_token>>();
        select_labels = job.at("allowed_labels").get<std::vector<std::string>>();
        require(!select_suffix.empty() && select_suffix.size() <= max_suffix, "invalid_suffix_size");
        require(!select_labels.empty() && select_labels.size() <= 53, "invalid_labels");
        select_ids.clear();
        for (auto & label : select_labels) {
            require(!label.empty() && label.size() <= 4, "invalid_label");
            auto t = common_tokenize(vocab, label, false, false);
            require(t.size() == 1 && std::find(select_ids.begin(), select_ids.end(), t[0]) == select_ids.end(),
                    "label_not_single_distinct_token");
            select_ids.push_back(t[0]);
        }
        select_offset = 0;
        select_pending = true;
    }
    json select_step(const std::shared_ptr<std::atomic<bool>> & cancelled) {
        require(select_pending && !branch.empty(), "select_not_pending");
        require(!cancelled->load(), "request_cancelled");
        std::vector<llama_token> input(branch_prefix, 0);
        input.insert(input.end(), select_suffix.begin(), select_suffix.end());
        const int from = branch_prefix + select_offset;
        // Keep each branch slice short enough to yield A promptly, while
        // avoiding a queue round trip for every token in the label prompt.
        const int to = std::min((int) input.size(), from + 64);
        evaluate(b, input, from, to, to == (int) input.size(), cancelled);
        select_offset = to - branch_prefix;
        if (select_offset < (int) select_suffix.size()) return {{"pending", true}};
        auto p = llama_get_logits_ith(ctx, -1); require(p != nullptr, "logits_missing");
        size_t best = 0; json logits = json::array();
        for (size_t i = 0; i < select_ids.size(); ++i) {
            require(std::isfinite(p[select_ids[i]]), "nonfinite_label_logit");
            logits.push_back(p[select_ids[i]]);
            if (p[select_ids[i]] > p[select_ids[best]]) best = i;
        }
        json result = {{"label", select_labels[best]}, {"decision_steps", 1}, {"label_logits", logits},
            {"shared_prefix_tokens", branch_reused}, {"replayed_tokens", branch_replayed},
            {"recurrent_checkpoint_verified", checkpoint_verified}, {"suffix_tokens", select_suffix.size()}};
        release_branch();
        return {{"results", json::array({result})}};
    }
    json run(const json & body, const std::shared_ptr<std::atomic<bool>> & cancelled) {
        expire(); const auto op = body.at("op").get<std::string>();
        if (op == "capabilities" || op == "runtime") {
            size_t bytes = 0; for (auto & c : checkpoints) bytes += c.second.size();
            return {{"backend", "llama-server-shared-kv"}, {"supported", true}, {"shared_prefix", true},
                {"fork_at", true}, {"label_constraint", true}, {"release", true}, {"cache_epoch", epoch},
                {"active_sequences", sequence.empty()?0:1}, {"active_branches", branch.empty()?0:1},
                {"provider_parallel_slots", provider_parallel_slots},
                {"generation_streaming", true},
                {"forks", forks}, {"releases", releases}, {"checkpoint_bytes", bytes},
                {"max_tokens", max_tokens}, {"max_suffix_tokens", max_suffix},
                {"checkpoint_limit", max_checkpoints}, {"branch_batch_size", 1}, {"model_instances", 1}};
        }
        if (op == "tokenize") return {{"tokens", common_tokenize(vocab, body.at("text").get<std::string>(), body.value("add_special", true), true)}};
        if (op == "create") {
            require(sequence.empty(), "sequence_busy");
            auto text = body.at("text").get<std::string>();
            auto input = common_tokenize(vocab, text, true, true);
            // Gemma's tokenizer always prepends BOS, even when the source
            // explicitly contains it. Preserve exactly one in that case.
            const auto bos = llama_vocab_bos(vocab);
            const auto bos_piece = bos == LLAMA_TOKEN_NULL ? std::string() : common_token_to_piece(vocab, bos, true);
            if (llama_vocab_get_add_bos(vocab) && !bos_piece.empty() && text.rfind(bos_piece, 0) == 0) {
                input = common_tokenize(vocab, text, false, true);
            }
            require(!input.empty() && input.size() <= max_tokens, "invalid_sequence_size");
            try {
                tokens = input;
                std::string pieces;
                for (auto t : tokens) { pieces += common_token_to_piece(vocab, t, true); ends.push_back(pieces.size()); }
                // An automatically inserted BOS owns a real token position,
                // but consumes zero bytes of the caller's original source.
                implicit_bos = pieces != text && !tokens.empty() && tokens.front() == bos &&
                    !bos_piece.empty() && pieces == bos_piece + text;
                require(pieces == text || implicit_bos, "prompt_token_bytes_mismatch");
                if (implicit_bos) for (auto & end : ends) end -= bos_piece.size();
                for (int off = 0; off < (int)tokens.size();) {
                    auto stop = std::min((int)tokens.size(), off + 256);
                    evaluate(a, tokens, off, stop, stop == (int)tokens.size(), cancelled);
                    checkpoint(stop); off = stop;
                }
                save_scores(); sequence = "a" + std::to_string(++serial); touched = clock::now();
                return handle();
            } catch (...) { clear(); throw; }
        }
        if (op == "release" && sequence.empty()) {
            require(body.value("cache_epoch", "") == epoch, "stale_cache_epoch");
            return {{"released", false}};
        }
        validate(body);
        if (op == "release") { clear(); return {{"released", true}}; }
        if (op == "scores") {
            auto labels = body.at("allowed_labels").get<std::vector<std::string>>();
            require(!labels.empty() && labels.size() <= 53, "invalid_labels");
            json values = json::array();
            for (auto & label : labels) {
                auto t = common_tokenize(vocab, label, false, false);
                require(t.size() == 1, "label_not_single_token");
                values.push_back(scores.at(t[0]));
            }
            return {{"label_logits", values}};
        }
        if (op == "prefix") {
            auto text = body.at("text").get<std::string>();
            auto n = prefix_tokens_for(text);
            std::string full;
            for (int i = 0; i < n; ++i) {
                if (implicit_bos && i == 0) continue;
                full += common_token_to_piece(vocab, tokens[i], true);
            }
            return {{"prefix_tokens", n}, {"remainder", text.substr(full.size())}};
        }
        if (op == "fork") {
            fork(body.at("prefix_tokens").get<int>(), cancelled);
            return {{"branch_id", branch}, {"cache_epoch", epoch}, {"shared_prefix_tokens", branch_reused}, {"replayed_tokens", branch_replayed}};
        }
        if (op == "release_branch" || op == "decode") {
            require(!branch.empty() && body.value("branch_id", "") == branch, "stale_branch");
            if (op == "release_branch") { release_branch(); return {{"released", true}}; }
            try { auto result = decode(body, cancelled); release_branch(); return result; }
            catch (...) { release_branch(); throw; }
        }
        if (op == "select_text" && select_pending) {
            if (body.value("_task_id", -1) != select_owner) return {{"pending", true}, {"busy", true}};
            return select_step(cancelled);
        }
        if (op == "select_text") {
            require(branch.empty(), "branch_busy");
            auto prefix_text = body.at("prefix_text").get<std::string>();
            auto suffix_text = body.at("suffix_text").get<std::string>();
            auto labels = body.at("allowed_labels").get<std::vector<std::string>>();
            auto prefix_tokens = prefix_tokens_for(prefix_text);
            std::string full;
            for (int i = 0; i < prefix_tokens; ++i) {
                if (implicit_bos && i == 0) continue;
                full += common_token_to_piece(vocab, tokens[i], true);
            }
            auto suffix = common_tokenize(vocab, prefix_text.substr(full.size()) + suffix_text, false, true);
            fork(prefix_tokens, cancelled);
            prepare_select({{"suffix_tokens", suffix}, {"allowed_labels", labels}, {"budget", 1}});
            select_owner = body.value("_task_id", -1);
            return select_step(cancelled);
        }
        if (op == "select") {
            auto jobs = body.at("jobs"); require(jobs.is_array() && !jobs.empty() && jobs.size() <= 12, "invalid_jobs");
            json results = json::array();
            for (auto & job : jobs) {
                try {
                    // Live acceptance can verify all of A, including attention
                    // tensors. Keep this expensive diagnostic off normal work.
                    std::vector<uint8_t> before;
                    if (job.value("verify_source", false)) {
                        before.resize(llama_state_seq_get_size(ctx, a));
                        require(llama_state_seq_get_data(ctx, before.data(), before.size(), a) == before.size(), "source_snapshot_failed");
                    }
                    fork(job.at("prefix_tokens").get<int>(), cancelled);
                    auto row = decode(job, cancelled); release_branch();
                    if (!before.empty()) {
                        std::vector<uint8_t> after(llama_state_seq_get_size(ctx, a));
                        require(llama_state_seq_get_data(ctx, after.data(), after.size(), a) == after.size(), "source_snapshot_failed");
                        require(before == after, "source_sequence_polluted");
                        row["source_state_unchanged"] = true;
                    }
                    results.push_back(std::move(row));
                }
                catch (...) { release_branch(); throw; }
            }
            return {{"results", results}};
        }
        if (op == "next") {
            require(tokens.size() < max_tokens, "sequence_context_limit");
            auto id = (llama_token)std::distance(scores.begin(), std::max_element(scores.begin(), scores.end()));
            if (llama_vocab_is_eog(vocab, id)) { checkpoint(tokens.size()); return {{"done", true}, {"delta", ""}, {"sequence", handle()}}; }
            tokens.push_back(id);
            try { evaluate(a, tokens, tokens.size()-1, tokens.size(), true, cancelled); save_scores(); }
            catch (...) { clear(); throw; }
            pending += common_token_to_piece(vocab, id, true);
            // Don't emit a split UTF-8 codepoint across HTTP JSON strings.
            size_t complete = 0;
            for (size_t i = 0; i < pending.size();) {
                unsigned char c = pending[i]; size_t n = c < 0x80 ? 1 : c < 0xe0 ? 2 : c < 0xf0 ? 3 : 4;
                if (i+n > pending.size()) break; i += n; complete = i;
            }
            auto delta = pending.substr(0, complete); pending.erase(0, complete); generated += delta;
            ends.push_back(ends.back() + common_token_to_piece(vocab, id, true).size());
            if (tokens.size()%32 == 0) checkpoint(tokens.size());
            return {{"done", false}, {"delta", delta}, {"sequence", handle()}};
        }
        if (op == "finish") { require(pending.empty(), "incomplete_utf8"); checkpoint(tokens.size()); return handle(); }
        throw std::runtime_error("unknown_echo_operation");
    }
};
