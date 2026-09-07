using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HistoryApollo;

/// <summary>一条对话消息。</summary>
/// <param name="Role">system / user / assistant。</param>
/// <param name="Content">消息正文。</param>
internal sealed record ChatMessage(string Role, string Content);

/// <summary>一次调用的可选项；未指定的项不进请求体，由服务端取自己的缺省值。</summary>
internal sealed record ChatOptions
{
    public string? Model { get; init; }

    public double? Temperature { get; init; }

    public int? MaxTokens { get; init; }

    /// <summary>要求服务端返回严格 JSON 对象（DeepSeek 的 <c>response_format=json_object</c>）。</summary>
    public bool JsonOutput { get; init; }

    /// <summary>整次请求的超时秒数。</summary>
    public int TimeoutSeconds { get; init; } = 120;
}

/// <summary>一次调用的结果。</summary>
internal sealed record ChatOutcome(
    string Provider,
    string Model,
    string Content,
    string? Reasoning,
    string? FinishReason,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    long ElapsedMilliseconds);

/// <summary>远端拒绝或不可达。消息已包含服务端给出的原因，可直接作为失败回执。</summary>
internal sealed class ApolloRemoteException(string message) : Exception(message);

/// <summary>
/// OpenAI 兼容的 chat/completions 客户端。
/// </summary>
/// <remarks>
/// 只说 HTTP 和 JSON，不认识任何一家供应商的私有协议——DeepSeek 与后来者的差异
/// 全部收在 <see cref="ProviderStore"/> 的接入点和模型名里。
///
/// <see cref="HttpClient"/> 由本类持有并随模块一起释放：宿主热重载会在装新快照前
/// Dispose 模块实例，连接池跟着走，不会每重载一次多留一份。
/// </remarks>
internal sealed class ChatClient : IDisposable
{
    private readonly HttpClient _http;

    public ChatClient()
        : this(new HttpClient())
    {
    }

    /// <summary>测试用：注入自定义传输。</summary>
    internal ChatClient(HttpClient http)
    {
        _http = http;
        // 超时逐次由 CancellationToken 控制，HttpClient 自身不再另设一层。
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>发起一次对话补全。</summary>
    public async Task<ChatOutcome> CompleteAsync(
        ProviderProfile provider,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        CancellationToken cancellation)
    {
        if (messages.Count == 0)
            throw new ApolloInputException("至少需要一条消息");
        RequireKey(provider);

        var model = string.IsNullOrWhiteSpace(options.Model) ? provider.Model : options.Model.Trim();
        if (string.IsNullOrWhiteSpace(model))
            throw new ApolloInputException($"供应商 {provider.Name} 没有默认模型，请用 model= 指定");
        if (options.MaxTokens is <= 0)
            throw new ApolloInputException("maxtokens 必须为正整数");

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages
                .Select(message => new Dictionary<string, string>
                {
                    ["role"] = message.Role,
                    ["content"] = message.Content,
                })
                .ToArray(),
            ["stream"] = false,
        };
        if (options.Temperature is { } temperature)
            body["temperature"] = temperature;
        if (options.MaxTokens is { } maxTokens)
            body["max_tokens"] = maxTokens;
        if (options.JsonOutput)
            body["response_format"] = new Dictionary<string, string> { ["type"] = "json_object" };

        var stopwatch = Stopwatch.StartNew();
        var payload = await SendAsync(
            provider,
            HttpMethod.Post,
            "/chat/completions",
            JsonSerializer.Serialize(body),
            options.TimeoutSeconds,
            cancellation).ConfigureAwait(false);
        stopwatch.Stop();

        return ReadOutcome(provider, model, payload, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>列出供应商当前可用的模型。</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(
        ProviderProfile provider,
        int timeoutSeconds,
        CancellationToken cancellation)
    {
        RequireKey(provider);
        var payload = await SendAsync(provider, HttpMethod.Get, "/models", null, timeoutSeconds, cancellation)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void RequireKey(ProviderProfile provider)
    {
        if (!provider.HasKey)
        {
            throw new ApolloInputException(
                $"供应商 {provider.Name} 未配置密钥：先执行 apollo.key.set provider={provider.Name} token=<密钥>，"
                + $"或设置环境变量 APOLLO_{provider.Name.ToUpperInvariant()}_KEY");
        }
    }

    private async Task<string> SendAsync(
        ProviderProfile provider,
        HttpMethod method,
        string route,
        string? json,
        int timeoutSeconds,
        CancellationToken cancellation)
    {
        var seconds = timeoutSeconds is > 0 and <= 600 ? timeoutSeconds : 120;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));

        using var request = new HttpRequestMessage(method, provider.BaseUrl + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new ApolloRemoteException($"{provider.Name} 请求超时（{seconds}s）");
        }
        catch (HttpRequestException ex)
        {
            throw new ApolloRemoteException($"{provider.Name} 不可达：{ex.Message}");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ApolloRemoteException($"{provider.Name} 返回 {(int)response.StatusCode}：{DescribeError(text)}");
            return text;
        }
    }

    /// <summary>从错误体里挑出人能读的那一句；挑不出就回截断的原文。</summary>
    private static string DescribeError(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? text;
                if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    return message.GetString() ?? text;
            }
        }
        catch (JsonException)
        {
        }

        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= 300 ? single : single[..300] + "…";
    }

    private static ChatOutcome ReadOutcome(
        ProviderProfile provider,
        string model,
        string payload,
        long elapsed)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new ApolloRemoteException($"{provider.Name} 返回的不是 JSON：{ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                throw new ApolloRemoteException($"{provider.Name} 返回里没有 choices");
            }

            var choice = choices[0];
            var message = choice.TryGetProperty("message", out var node) ? node : default;
            var content = message.ValueKind == JsonValueKind.Object
                          && message.TryGetProperty("content", out var contentNode)
                ? contentNode.GetString() ?? string.Empty
                : string.Empty;

            // deepseek-reasoner 把思考过程放在 reasoning_content，与正文分开返回。
            var reasoning = message.ValueKind == JsonValueKind.Object
                            && message.TryGetProperty("reasoning_content", out var reasoningNode)
                ? reasoningNode.GetString()
                : null;

            var finish = choice.TryGetProperty("finish_reason", out var finishNode)
                ? finishNode.GetString()
                : null;

            var usage = root.TryGetProperty("usage", out var usageNode) ? usageNode : default;
            var reportedModel = root.TryGetProperty("model", out var modelNode)
                ? modelNode.GetString() ?? model
                : model;

            return new ChatOutcome(
                provider.Name,
                reportedModel,
                content,
                string.IsNullOrWhiteSpace(reasoning) ? null : reasoning,
                finish,
                ReadInt(usage, "prompt_tokens"),
                ReadInt(usage, "completion_tokens"),
                ReadInt(usage, "total_tokens"),
                elapsed);
        }
    }

    private static int ReadInt(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.TryGetInt32(out var number)
            ? number
            : 0;
}
