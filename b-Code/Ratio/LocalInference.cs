using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ratio;

/// <summary>下一 token 的一个候选。</summary>
internal sealed record TokenProbability(string Token, double Logprob);

/// <summary>共享 KV 的序列句柄：服务端的序列编号、缓存代次与前缀 token 数。</summary>
internal sealed record SequenceLease(string SequenceId, string CacheEpoch, int PrefixTokens);

/// <summary>本机服务拒绝了一次共享 KV 操作；消息是服务给的原因码（如 invalid_sequence_size）。</summary>
internal sealed class RatioKvException(string message) : Exception(message);

/// <summary>
/// 本机推理服务的扩展推理面（0.8.0）：下一 token 概率、共享 KV 序列（A/B 分叉选标签）与流式生成。
/// </summary>
/// <remarks>
/// 这些是 llama-server 与 echo-proxy 的扩展接口，只有供应商 <c>local</c> 有；消费方（Echo）不再直连
/// 11435，只调 <c>ratio.local.probs</c> / <c>ratio.seq.*</c> / <c>ratio.stream.*</c>。
/// 序列由本模块记账：模块卸载时把还开着的序列逐个释放，不把服务端槽位留给已经不存在的调用方。
/// </remarks>
internal sealed class LocalInference : IDisposable
{
    private readonly Func<string> _endpoint;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, SequenceLease> _open = new(StringComparer.Ordinal);

    public LocalInference(ProviderStore store)
        : this(() => store.Resolve(ProviderStore.LocalProvider).BaseUrl, new HttpClient())
    {
    }

    /// <summary>测试用：注入接入点与传输。</summary>
    internal LocalInference(Func<string> endpoint, HttpClient http)
    {
        _endpoint = endpoint;
        _http = http;
        _http.Timeout = Timeout.InfiniteTimeSpan;
        Streams = new StreamRegistry();
    }

    public StreamRegistry Streams { get; }

    /// <summary>还开着的序列数。</summary>
    public int OpenSequences => _open.Count;

    /// <summary>代理根地址：<c>local</c> 接入点去掉末尾的 <c>/v1</c>。</summary>
    public string ServiceUrl
    {
        get
        {
            var url = _endpoint().TrimEnd('/');
            return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;
        }
    }

    public void Dispose()
    {
        Streams.Dispose();
        foreach (var lease in _open.Values.ToList())
        {
            try
            {
                CloseAsync(lease.SequenceId, lease.CacheEpoch, CancellationToken.None).Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // 卸载途中服务可能已经停了；序列会随服务重启一起消失。
            }
        }

        _http.Dispose();
    }

    /// <summary>原样续写 <paramref name="prompt"/> 一个 token，取概率最高的 <paramref name="top"/> 个候选。</summary>
    public async Task<IReadOnlyList<TokenProbability>> ProbsAsync(
        string prompt, int top, string? model, int timeoutSeconds, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(prompt))
            throw new RatioInputException("prompt 不能为空");
        if (top is < 1 or > 100)
            throw new RatioInputException("top 须在 1-100 之间");
        var body = new JsonObject
        {
            ["prompt"] = prompt,
            ["n_predict"] = 1,
            ["n_probs"] = top,
            ["temperature"] = 0,
            ["cache_prompt"] = true,
            ["stream"] = false,
        };
        if (!string.IsNullOrWhiteSpace(model))
            body["model"] = model.Trim();
        var text = await RatioHttp.SendAsync(_http, "本机推理服务", HttpMethod.Post, ServiceUrl + "/completion", null,
            body.ToJsonString(), timeoutSeconds, cancellation).ConfigureAwait(false);
        var root = JsonNode.Parse(text) as JsonObject;
        var rows = root?["completion_probabilities"] as JsonArray;
        if (rows is not { Count: > 0 } || rows[0]?["top_logprobs"] is not JsonArray tops)
            throw new RatioRemoteException("本机推理服务没有返回下一 token 概率");
        return tops.OfType<JsonObject>()
            .Select(item => new TokenProbability(
                item["token"]?.GetValue<string>() ?? string.Empty,
                item["logprob"]?.GetValue<double>() ?? double.NegativeInfinity))
            .ToList();
    }

    /// <summary>服务对某模型的共享 KV 能力；探测失败时返回 supported=false 与原因，不抛。</summary>
    public async Task<JsonObject> CapabilitiesAsync(string? model, CancellationToken cancellation)
    {
        try
        {
            var body = new JsonObject();
            if (!string.IsNullOrWhiteSpace(model))
                body["model"] = model.Trim();
            var data = await OpAsync("capabilities", body, 10, cancellation).ConfigureAwait(false);
            foreach (var key in new[] { "supported", "shared_prefix", "fork_at", "label_constraint", "release" })
            {
                if (data[key]?.GetValueKind() != JsonValueKind.True)
                {
                    data["supported"] = false;
                    data["reason"] ??= "external_service_no_sequence_fork";
                    break;
                }
            }

            return data;
        }
        catch (Exception ex) when (ex is RatioKvException or RatioRemoteException)
        {
            return new JsonObject { ["supported"] = false, ["backend"] = "external-http", ["reason"] = ex.Message };
        }
    }

    /// <summary>以 <paramref name="text"/> 为前缀开一条序列（预填 A）。</summary>
    public async Task<SequenceLease> OpenAsync(string? model, string text, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(text))
            throw new RatioInputException("text 不能为空");
        var body = new JsonObject { ["text"] = text };
        if (!string.IsNullOrWhiteSpace(model))
            body["model"] = model.Trim();
        // 预填可能比调用方等得久：调用方取消了也要拿到句柄并释放，否则服务端槽位泄漏。
        var pending = OpAsync("create", body, 600, CancellationToken.None);
        JsonObject data;
        try
        {
            data = await pending.WaitAsync(cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = pending.ContinueWith(async task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                    await CloseQuietAsync(Lease(task.Result)).ConfigureAwait(false);
            }, TaskScheduler.Default);
            throw;
        }

        var lease = Lease(data);
        _open[lease.SequenceId] = lease;
        return lease;
    }

    /// <summary>
    /// 在序列上分叉一个 B：把 <paramref name="prefixText"/> 对齐到 A 的 token 前缀，接上 <paramref name="suffixText"/>，
    /// 约束只能输出 <paramref name="labels"/> 之一，取一步，B 随即释放。
    /// </summary>
    public async Task<JsonObject> ChooseAsync(
        string sequence, string epoch, string prefixText, string suffixText, IReadOnlyList<string> labels,
        CancellationToken cancellation)
    {
        if (labels.Count == 0)
            throw new RatioInputException("labels 至少要有一个");
        var allowed = new JsonArray(labels.Select(label => (JsonNode)JsonValue.Create(label)!).ToArray());
        JsonObject data;
        try
        {
            data = await OpAsync("select_text", new JsonObject
            {
                ["sequence_id"] = sequence,
                ["cache_epoch"] = epoch,
                ["prefix_text"] = prefixText,
                ["suffix_text"] = suffixText,
                ["allowed_labels"] = allowed,
            }, 120, cancellation).ConfigureAwait(false);
        }
        catch (RatioKvException ex) when (ex.Message.Contains("unknown_echo_operation", StringComparison.Ordinal))
        {
            // 旧服务没有 select_text：拆成 prefix + tokenize + select 三步。
            var prefix = await OpAsync("prefix", new JsonObject
            {
                ["sequence_id"] = sequence, ["cache_epoch"] = epoch, ["text"] = prefixText,
            }, 60, cancellation).ConfigureAwait(false);
            var remainder = prefix["remainder"]?.GetValue<string>() ?? string.Empty;
            var tokens = await OpAsync("tokenize", new JsonObject
            {
                ["text"] = remainder + suffixText, ["add_special"] = false,
            }, 60, cancellation).ConfigureAwait(false);
            data = await OpAsync("select", new JsonObject
            {
                ["sequence_id"] = sequence,
                ["cache_epoch"] = epoch,
                ["jobs"] = new JsonArray(new JsonObject
                {
                    ["prefix_tokens"] = prefix["prefix_tokens"]?.DeepClone(),
                    ["suffix_tokens"] = tokens["tokens"]?.DeepClone(),
                    ["allowed_labels"] = allowed.DeepClone(),
                    ["budget"] = 1,
                }),
            }, 120, cancellation).ConfigureAwait(false);
        }

        if (data["results"] is not JsonArray { Count: 1 } results || results[0] is not JsonObject row)
            throw new RatioKvException("invalid_shared_results");
        var label = row["label"]?.GetValue<string>();
        if (label is null || !labels.Contains(label, StringComparer.Ordinal)
            || row["decision_steps"]?.GetValue<int>() != 1)
            throw new RatioKvException("invalid_shared_label");
        return (JsonObject)row.DeepClone();
    }

    /// <summary>
    /// 在序列末尾续接一段文本（0.10.0）：只算新增的 token，此前的 KV 原样保留。返回续接后的前缀 token 数。
    /// </summary>
    public async Task<SequenceLease> AppendAsync(string sequence, string epoch, string text, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(text))
            throw new RatioInputException("text 不能为空");
        var data = await OpAsync("append", new JsonObject
        {
            ["sequence_id"] = sequence, ["cache_epoch"] = epoch, ["text"] = text,
        }, 600, cancellation).ConfigureAwait(false);
        var lease = Lease(data);
        if (_open.ContainsKey(sequence))
            _open[sequence] = lease;
        return lease;
    }

    /// <summary>
    /// 在序列上一次分叉很多个 B（0.10.0）：每个任务一段后缀与一组标签，服务把它们并进同一次前向（多序列一批），
    /// 各取一步。<paramref name="prefixText"/> 为空表示在序列末尾分叉。结果与任务同序。
    /// </summary>
    /// <remarks>旧服务没有 <c>select_batch</c> 时退回逐个 <see cref="ChooseAsync"/>，结果相同、只是慢。</remarks>
    public async Task<(JsonArray Rows, double ServiceMs, bool Batched)> ChooseBatchAsync(
        string sequence, string epoch, string? prefixText, IReadOnlyList<(string Suffix, IReadOnlyList<string> Labels)> jobs,
        CancellationToken cancellation)
    {
        if (jobs.Count is < 1 or > 512)
            throw new RatioInputException("jobs 须为 1-512 个");
        if (jobs.Any(job => job.Labels.Count == 0 || string.IsNullOrEmpty(job.Suffix)))
            throw new RatioInputException("每个任务都要有非空的 suffix 与至少一个标签");
        var body = new JsonObject
        {
            ["sequence_id"] = sequence,
            ["cache_epoch"] = epoch,
            ["jobs"] = new JsonArray(jobs.Select(job => (JsonNode)new JsonObject
            {
                ["suffix_text"] = job.Suffix,
                ["allowed_labels"] = new JsonArray(job.Labels.Select(label => (JsonNode)JsonValue.Create(label)!).ToArray()),
            }).ToArray()),
        };
        if (!string.IsNullOrEmpty(prefixText))
            body["prefix_text"] = prefixText;
        JsonObject data;
        try
        {
            data = await OpAsync("select_batch", body, 300, cancellation).ConfigureAwait(false);
        }
        catch (RatioKvException ex) when (ex.Message.Contains("unknown_echo_operation", StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(prefixText))
                throw new RatioKvException("select_batch_requires_prefix_on_old_service");
            var rows = new JsonArray();
            foreach (var job in jobs)
                rows.Add(await ChooseAsync(sequence, epoch, prefixText, job.Suffix, job.Labels, cancellation).ConfigureAwait(false));
            return (rows, 0, false);
        }

        if (data["results"] is not JsonArray results || results.Count != jobs.Count)
            throw new RatioKvException("invalid_shared_results");
        for (var i = 0; i < results.Count; i++)
        {
            var label = results[i]?["label"]?.GetValue<string>();
            if (label is null || !jobs[i].Labels.Contains(label, StringComparer.Ordinal))
                throw new RatioKvException("invalid_shared_label");
        }

        data.Remove("results");
        return (results, data["elapsed_ms"]?.GetValue<double>() ?? 0, true);
    }

    /// <summary>释放序列。</summary>
    public async Task CloseAsync(string sequence, string epoch, CancellationToken cancellation)
    {
        _open.TryRemove(sequence, out _);
        await OpAsync("release", new JsonObject { ["sequence_id"] = sequence, ["cache_epoch"] = epoch }, 30, cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>在序列上继续生成 A，增量写进一路流。</summary>
    public StreamSession StreamSequence(string sequence, string epoch, int maxTokens)
        => Streams.Open((session, token) => GenerateSequenceAsync(session, sequence, epoch, maxTokens, token));

    /// <summary>OpenAI 兼容的流式对话，增量写进一路流。</summary>
    public StreamSession StreamChat(ProviderProfile provider, string model, JsonObject body)
        => Streams.Open((session, token) => GenerateChatAsync(session, provider, model, body, token));

    private async Task GenerateSequenceAsync(StreamSession session, string sequence, string epoch, int maxTokens, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ServiceUrl + "/v1/kv/op")
        {
            Content = Json(new JsonObject
            {
                ["op"] = "stream", ["sequence_id"] = sequence, ["cache_epoch"] = epoch, ["max_tokens"] = maxTokens,
            }),
        };
        using var response = await SendStreamingAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            // 服务没有 generation_streaming：逐步 next，比流式慢但结果相同。
            await GenerateByStepsAsync(session, sequence, epoch, maxTokens, token).ConfigureAwait(false);
            return;
        }

        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        var finished = false;
        await foreach (var payload in ReadEventsAsync(response, token).ConfigureAwait(false))
        {
            if (payload == "[DONE]")
            {
                if (!finished)
                    throw new RatioKvException("shared_stream_missing_finish");
                return;
            }

            var data = JsonNode.Parse(payload) as JsonObject;
            if (data?["error"] is { } error && error.GetValueKind() is not (JsonValueKind.Null or JsonValueKind.False))
                throw new RatioKvException(error.GetValueKind() == JsonValueKind.String ? error.GetValue<string>() : error.ToJsonString());
            if (data?["delta"]?.GetValue<string>() is { Length: > 0 } delta)
                session.Append(delta);
            finished |= data?["done"]?.GetValueKind() == JsonValueKind.True;
        }

        throw new RatioKvException("incomplete_shared_kv_stream");
    }

    private async Task GenerateByStepsAsync(StreamSession session, string sequence, string epoch, int maxTokens, CancellationToken token)
    {
        var handle = new JsonObject { ["sequence_id"] = sequence, ["cache_epoch"] = epoch };
        for (var step = 0; step < maxTokens; step++)
        {
            var data = await OpAsync("next", (JsonObject)handle.DeepClone(), 120, token).ConfigureAwait(false);
            if (data["done"]?.GetValueKind() == JsonValueKind.True)
                return;
            if (data["delta"]?.GetValue<string>() is { Length: > 0 } delta)
                session.Append(delta);
        }

        await OpAsync("finish", (JsonObject)handle.DeepClone(), 30, token).ConfigureAwait(false);
    }

    private async Task GenerateChatAsync(StreamSession session, ProviderProfile provider, string model, JsonObject body, CancellationToken token)
    {
        body["model"] = model;
        body["stream"] = true;
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = Json(body),
        };
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
        using var response = await SendStreamingAsync(request, token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        await foreach (var payload in ReadEventsAsync(response, token).ConfigureAwait(false))
        {
            if (payload == "[DONE]")
                return;
            var data = JsonNode.Parse(payload) as JsonObject;
            if (data?["error"] is { } error)
                throw new RatioRemoteException($"{provider.Name} 流式生成出错：{RatioHttp.DescribeError(new JsonObject { ["error"] = error.DeepClone() }.ToJsonString())}");
            foreach (var choice in (data?["choices"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (choice["delta"]?["content"]?.GetValue<string>() is { Length: > 0 } content)
                    session.Append(content);
            }
        }

        throw new RatioRemoteException($"{provider.Name} 的流意外结束，未收到完成标记");
    }

    private async Task<HttpResponseMessage> SendStreamingAsync(HttpRequestMessage request, CancellationToken token)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RatioRemoteException($"推理服务不可达：{ex.Message}");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode)
            return;
        var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        throw new RatioRemoteException($"推理服务返回 {(int)response.StatusCode}：{RatioHttp.DescribeError(text)}");
    }

    private static async IAsyncEnumerable<string> ReadEventsAsync(
        HttpResponseMessage response, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            var payload = line[5..].Trim();
            if (payload.Length > 0)
                yield return payload;
        }
    }

    /// <summary>一次 <c>/v1/kv/op</c>。400/404/409/501 是「服务不支持或拒绝」，翻译成带原因码的 <see cref="RatioKvException"/>。</summary>
    private async Task<JsonObject> OpAsync(string op, JsonObject body, int timeoutSeconds, CancellationToken cancellation)
    {
        body["op"] = op;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, ServiceUrl + "/v1/kv/op") { Content = Json(body) };
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new RatioRemoteException($"本机推理服务 {op} 超时（{timeoutSeconds}s）");
        }
        catch (HttpRequestException ex)
        {
            throw new RatioRemoteException($"本机推理服务不可达：{ex.Message}");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict
                or HttpStatusCode.NotImplemented)
                throw new RatioKvException(KvReason(text));
            if (!response.IsSuccessStatusCode)
                throw new RatioRemoteException($"本机推理服务返回 {(int)response.StatusCode}：{RatioHttp.DescribeError(text)}");
            return JsonNode.Parse(text) as JsonObject ?? throw new RatioKvException("invalid_shared_kv_response");
        }
    }

    private static string KvReason(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject root && root["error"] is JsonObject error
                && error["message"]?.GetValue<string>() is { Length: > 0 } message)
                return message;
        }
        catch (JsonException)
        {
        }

        return "shared_kv_unavailable";
    }

    private static SequenceLease Lease(JsonObject data)
    {
        try
        {
            return new SequenceLease(
                data["sequence_id"]!.ToString(),
                data["cache_epoch"]!.ToString(),
                int.Parse(data["prefix_tokens"]!.ToString(), CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is NullReferenceException or FormatException or InvalidOperationException)
        {
            throw new RatioKvException("invalid_sequence_response");
        }
    }

    private async Task CloseQuietAsync(SequenceLease lease)
    {
        try
        {
            await CloseAsync(lease.SequenceId, lease.CacheEpoch, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}
