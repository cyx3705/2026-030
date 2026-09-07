using System.Text.Json;

namespace HistoryApollo;

/// <summary>把命令参数变成消息序列，再把结果变成回执。</summary>
internal static class ChatTranscript
{
    private static readonly HashSet<string> KnownRoles =
        new(StringComparer.OrdinalIgnoreCase) { "system", "user", "assistant" };

    /// <summary>单轮提问：可选的系统提示 + 一条 user 消息。</summary>
    public static IReadOnlyList<ChatMessage> FromPrompt(string? system, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ApolloInputException("prompt 不能为空");

        var messages = new List<ChatMessage>(2);
        if (!string.IsNullOrWhiteSpace(system))
            messages.Add(new ChatMessage("system", system.Trim()));
        messages.Add(new ChatMessage("user", prompt));
        return messages;
    }

    /// <summary>
    /// 多轮对话：<c>[{"role":"user","content":"…"}, …]</c>。
    /// </summary>
    /// <remarks>
    /// 上下文由调用方持有并逐次回传，本模块不在本地保存任何对话历史——
    /// 一个会随热重载消失的会话缓存，只会让调用方以为自己不必管上下文。
    /// </remarks>
    public static IReadOnlyList<ChatMessage> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ApolloInputException("messages 不能为空");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ApolloInputException($"messages 不是合法 JSON：{ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ApolloInputException("messages 必须是 JSON 数组");

            var messages = new List<ChatMessage>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new ApolloInputException("messages 的每一项必须是对象");

                var role = item.TryGetProperty("role", out var roleNode) ? roleNode.GetString() : null;
                var content = item.TryGetProperty("content", out var contentNode) ? contentNode.GetString() : null;
                if (string.IsNullOrWhiteSpace(role) || !KnownRoles.Contains(role))
                    throw new ApolloInputException($"role 只能是 system / user / assistant，收到：{role ?? "(空)"}");
                if (content == null)
                    throw new ApolloInputException("每条消息都必须有 content");

                messages.Add(new ChatMessage(role.ToLowerInvariant(), content));
            }

            if (messages.Count == 0)
                throw new ApolloInputException("messages 至少要有一条");
            return messages;
        }
    }

    /// <summary>
    /// 命令回执正文。
    /// </summary>
    /// <remarks>
    /// 正文就是模型的答复本身，后面补一行用量脚注。
    /// <paramref name="jsonOutput"/> 为真时不加脚注：那次调用的整段回执要能直接被 JSON 解析。
    /// </remarks>
    public static string Describe(ChatOutcome outcome, bool jsonOutput)
    {
        if (jsonOutput)
            return outcome.Content;

        var footer =
            $"— {outcome.Provider}/{outcome.Model} · "
            + $"用量 {outcome.PromptTokens}+{outcome.CompletionTokens}={outcome.TotalTokens} tokens · "
            + $"{outcome.ElapsedMilliseconds / 1000.0:0.0}s";
        return string.IsNullOrEmpty(outcome.Content)
            ? footer
            : $"{outcome.Content}{Environment.NewLine}{Environment.NewLine}{footer}";
    }

    /// <summary>结构化载荷：正文、思考过程、用量和结束原因。</summary>
    public static string ToJson(ChatOutcome outcome)
        => JsonSerializer.Serialize(
            new
            {
                provider = outcome.Provider,
                model = outcome.Model,
                content = outcome.Content,
                reasoning = outcome.Reasoning,
                finishReason = outcome.FinishReason,
                usage = new
                {
                    prompt = outcome.PromptTokens,
                    completion = outcome.CompletionTokens,
                    total = outcome.TotalTokens,
                },
                elapsedMs = outcome.ElapsedMilliseconds,
            },
            new JsonSerializerOptions { WriteIndented = true });
}
