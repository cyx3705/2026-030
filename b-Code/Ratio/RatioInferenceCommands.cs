using System.Text.Json;
using System.Text.Json.Nodes;
using HistoryVulcan.Core.Commands;

namespace Ratio;

/// <summary>
/// 推理原语指令（0.8.0）：下一 token 概率（<c>ratio.local.probs</c>）、共享 KV 序列（<c>ratio.seq.*</c>）、
/// 流式生成（<c>ratio.chat.stream</c> + <c>ratio.stream.*</c>）。
/// </summary>
/// <remarks>
/// 给需要逐 token 交互的消费方（Echo）用：它们不再直连推理服务的私有 HTTP，只调这里。
/// 回执的 Data 一律是 JSON 文本，字段写在本仓 <c>b-Office/现行约定.md</c>「推理原语」节。
/// </remarks>
internal static class RatioInferenceCommands
{
    private const string Domain = "ratio";

    public static void Register(ICommandRegistrar registry, ProviderStore store, ChatClient client, LocalInference local)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "ratio.local.probs",
            Domain = Domain,
            CommandClass = "local",
            Summary = "把一段原始提示词交给本机推理服务续写一个 token，返回概率最高的若干候选（token 与对数概率），供输入预测用。",
            Example = "ratio.local.probs prompt=\"<|im_start|>user\\nI like\" top=20",
            Level = CommandLevel.Run,
            Readonly = true,
            Parameters =
            [
                new ParameterSpec { Name = "prompt", Description = "已按模型聊天模板拼好的原始提示词，原样送进模型，不再套模板。", Required = true, Position = 0 },
                new ParameterSpec { Name = "top", Description = "返回几个候选（1-100）。", Type = ParamType.Int, Default = "20" },
                new ParameterSpec { Name = "model", Description = "模型名，省略时用服务当前装着的模型，例如 qwen3:8b。" },
                Timeout(60),
            ],
            Handler = Guard(async context =>
            {
                var tokens = await local.ProbsAsync(context.RequireString("prompt"), context.GetInt("top", 20),
                    context.GetString("model"), context.GetInt("timeout", 60), context.Cancellation).ConfigureAwait(false);
                var data = new JsonArray(tokens.Select(t => (JsonNode)new JsonObject { ["token"] = t.Token, ["logprob"] = t.Logprob }).ToArray());
                return CommandResult.Ok($"{tokens.Count} 个候选：{string.Join(" ", tokens.Take(5).Select(t => $"[{t.Token}]"))}",
                    new JsonObject { ["tokens"] = data }.ToJsonString());
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ratio.seq.caps",
            Domain = Domain,
            CommandClass = "seq",
            Summary = "查询本机推理服务对某模型是否支持共享 KV 序列（同一前缀分叉选标签、流式续写）；不支持时给原因，不报错。",
            Example = "ratio.seq.caps model=qwen3:8b",
            Level = CommandLevel.Run,
            Readonly = true,
            Parameters = [new ParameterSpec { Name = "model", Description = "模型名，省略时问服务当前模型，例如 qwen3:8b。", Position = 0 }],
            Handler = Guard(async context =>
            {
                var caps = await local.CapabilitiesAsync(context.GetString("model"), context.Cancellation).ConfigureAwait(false);
                var supported = caps["supported"]?.GetValueKind() == JsonValueKind.True;
                return CommandResult.Ok(supported ? "支持共享 KV 序列" : $"不支持共享 KV 序列：{caps["reason"]}", caps.ToJsonString());
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ratio.seq.open",
            Domain = Domain,
            CommandClass = "seq",
            Summary = "在本机推理服务上以一段原始提示词预填并开一条共享 KV 序列，返回序列句柄；用完必须 ratio.seq.close。",
            Example = "ratio.seq.open text=\"<|im_start|>system\\nHi<|im_end|>\\n\"",
            Level = CommandLevel.Run,
            Parameters =
            [
                new ParameterSpec { Name = "text", Description = "序列前缀：按模型模板拼好的原始提示词。", Required = true, Position = 0 },
                new ParameterSpec { Name = "model", Description = "模型名，省略时用服务当前模型，例如 qwen3:8b。" },
            ],
            Handler = Guard(async context =>
            {
                var lease = await local.OpenAsync(context.GetString("model"), context.RequireString("text"), context.Cancellation).ConfigureAwait(false);
                return CommandResult.Ok($"已开序列 {lease.SequenceId}（前缀 {lease.PrefixTokens} token）", JsonSerializer.Serialize(new
                {
                    sequence = lease.SequenceId, epoch = lease.CacheEpoch, prefixTokens = lease.PrefixTokens,
                }));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ratio.seq.choose",
            Domain = Domain,
            CommandClass = "seq",
            Summary = "在共享 KV 序列上分叉一次：对齐前缀文本、接上后缀，约束模型只能输出给定标签之一，返回选中的标签；分叉随即释放。",
            Example = "ratio.seq.choose sequence=3 epoch=e1 prefix=\"<|im_start|>user\\n\" suffix=\"Label:\" labels=[\"0\",\" A\",\" B\"]",
            Level = CommandLevel.Run,
            Parameters =
            [
                SequenceParameter(),
                EpochParameter(),
                new ParameterSpec { Name = "prefix", Description = "与序列共享的前缀文本（序列原文或它的前一段）；服务把它对齐到 token 边界，多出的半截并进后缀。", Required = true },
                new ParameterSpec { Name = "suffix", Description = "分叉里接在前缀后面的文本，通常是任务说明加「Label:」。", Required = true },
                new ParameterSpec { Name = "labels", Description = "JSON 字符串数组，允许输出的标签，例如 [\"0\",\" A\",\" B\"]（注意前导空格是 token 的一部分）。", Required = true },
            ],
            Handler = Guard(async context =>
            {
                var labels = ParseLabels(context.RequireString("labels"));
                var row = await local.ChooseAsync(context.RequireString("sequence"), context.RequireString("epoch"),
                    context.GetString("prefix") ?? string.Empty, context.GetString("suffix") ?? string.Empty, labels,
                    context.Cancellation).ConfigureAwait(false);
                return CommandResult.Ok($"选中 [{row["label"]}]", row.ToJsonString());
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ratio.seq.stream",
            Domain = Domain,
            CommandClass = "seq",
            Summary = "在共享 KV 序列上继续生成（A），立即返回流编号；正文用 ratio.stream.read 按位置读，期间可并发 ratio.seq.choose。",
            Example = "ratio.seq.stream sequence=3 epoch=e1 maxtokens=1024",
            Level = CommandLevel.Run,
            Parameters =
            [
                SequenceParameter(),
                EpochParameter(),
                new ParameterSpec { Name = "maxtokens", Description = "最多生成多少 token（1-8192）。", Type = ParamType.Int, Default = "1024" },
            ],
            Handler = Guard(context =>
            {
                var max = context.GetInt("maxtokens", 1024);
                if (max is < 1 or > 8192)
                    throw new RatioInputException("maxtokens 须在 1-8192 之间");
                var session = local.StreamSequence(context.RequireString("sequence"), context.RequireString("epoch"), max);
                return Task.FromResult(StreamOpened(session));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ratio.seq.close",
            Domain = Domain,
            CommandClass = "seq",
            Summary = "释放一条共享 KV 序列，归还服务端槽位。",
            Example = "ratio.seq.close sequence=3 epoch=e1",
            Level = CommandLevel.Run,
            Parameters = [SequenceParameter(), EpochParameter()],
            Handler = Guard(async context =>
            {
                await local.CloseAsync(context.RequireString("sequence"), context.RequireString("epoch"), context.Cancellation).ConfigureAwait(false);
                return CommandResult.Ok("序列已释放");
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ratio.chat.stream",
            Domain = Domain,
            CommandClass = "chat",
            Summary = "按完整消息数组发起一次流式对话，立即返回流编号；正文用 ratio.stream.read 按位置读，不联网、不附图。",
            Example = "ratio.chat.stream messages=[{\"role\":\"user\",\"content\":\"Hi\"}] provider=local think=false",
            Level = CommandLevel.Run,
            Parameters =
            [
                new ParameterSpec { Name = "messages", Description = "JSON 数组，每项含 role(system/user/assistant) 与 content。", Required = true, Position = 0 },
                new ParameterSpec { Name = "system", Description = "系统提示；写了就插在 messages 最前面。" },
                new ParameterSpec { Name = "model", Description = "本次使用的模型；省略时用供应商默认模型，本机服务取它当前装着的模型。" },
                new ParameterSpec { Name = "provider", Description = "供应商名称，例如 local 或 deepseek；省略时用默认供应商。" },
                new ParameterSpec { Name = "maxtokens", Description = "本次回复的最大 token 数。", Type = ParamType.Int },
                new ParameterSpec { Name = "temperature", Description = "采样温度；省略时用服务端默认值。", Type = ParamType.Double },
                new ParameterSpec { Name = "think", Description = "写 false 时关掉本机模型的思考块（chat_template_kwargs.enable_thinking=false）；省略时按服务端默认。", Type = ParamType.Bool },
            ],
            Handler = Guard(async context =>
            {
                var profile = store.ResolveChat(context.GetString("provider"));
                var options = new ChatOptions
                {
                    Model = context.GetString("model"),
                    Temperature = context.Has("temperature") ? context.GetDouble("temperature", 1.0) : null,
                    MaxTokens = context.Has("maxtokens") ? context.GetInt("maxtokens", 0) : null,
                    Think = context.Has("think") ? context.GetBool("think", true) : null,
                    TimeoutSeconds = 30,
                };
                if (options.MaxTokens is <= 0)
                    throw new RatioInputException("maxtokens 必须为正整数");
                if (profile.KeyRequired && !profile.HasKey)
                    throw new RatioInputException($"供应商 {profile.Name} 未配置密钥：先执行 ratio.key.set provider={profile.Name} token=<密钥>");
                var messages = ChatTranscript.Parse(context.GetString("messages")).ToList();
                if (!string.IsNullOrWhiteSpace(context.GetString("system")))
                    messages.Insert(0, new ChatMessage("system", context.GetString("system")!));
                var conversation = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray());
                var model = await client.ResolveModelAsync(profile, options, context.Cancellation).ConfigureAwait(false);
                var body = ChatClient.BuildBody(model, conversation, options);
                return StreamOpened(local.StreamChat(profile, model, body));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ratio.stream.read",
            Domain = Domain,
            CommandClass = "stream",
            Summary = "读一路流式生成从某位置起新到的正文；没有新内容时最多等 wait 毫秒。读到结束的那次之后流自动关闭。",
            Example = "ratio.stream.read stream=0f3a after=0 wait=2000",
            Level = CommandLevel.Run,
            Readonly = true,
            Parameters =
            [
                StreamParameter(),
                new ParameterSpec { Name = "after", Description = "从第几个字符起读，即上次回执的 next；第一次传 0。", Type = ParamType.Int, Default = "0" },
                new ParameterSpec { Name = "wait", Description = "没有新内容时最多等多少毫秒（0-30000）。", Type = ParamType.Int, Default = "2000" },
            ],
            Handler = Guard(async context =>
            {
                var chunk = await local.Streams.ReadAsync(context.RequireString("stream"), context.GetInt("after", 0),
                    context.GetInt("wait", 2000), context.Cancellation).ConfigureAwait(false);
                var data = JsonSerializer.Serialize(new { text = chunk.Text, next = chunk.Next, done = chunk.Done, error = chunk.Error });
                return chunk.Error is null
                    ? CommandResult.Ok(chunk.Done ? $"已结束（共 {chunk.Next} 字）" : $"到第 {chunk.Next} 字", data)
                    : CommandResult.Ok($"已失败：{chunk.Error}", data);
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "ratio.stream.cancel",
            Domain = Domain,
            CommandClass = "stream",
            Summary = "取消一路流式生成并关闭它，上游请求随即断开。",
            Example = "ratio.stream.cancel stream=0f3a",
            Level = CommandLevel.Run,
            Parameters = [StreamParameter()],
            Handler = Guard(context => Task.FromResult(local.Streams.Cancel(context.RequireString("stream"))
                ? CommandResult.Ok("已取消")
                : CommandResult.Ok("这路流已经不在了"))),
        });
    }

    internal static IReadOnlyList<string> ParseLabels(string text)
    {
        try
        {
            var labels = JsonSerializer.Deserialize<List<string>>(text);
            if (labels is not { Count: > 0 } || labels.Any(string.IsNullOrEmpty))
                throw new RatioInputException("labels 必须是非空字符串组成的 JSON 数组");
            return labels;
        }
        catch (JsonException ex)
        {
            throw new RatioInputException($"labels 不是合法 JSON 数组：{ex.Message}");
        }
    }

    private static CommandResult StreamOpened(StreamSession session)
        => CommandResult.Ok($"已开流 {session.Id}", JsonSerializer.Serialize(new { stream = session.Id }));

    private static ParameterSpec SequenceParameter() => new()
    {
        Name = "sequence", Description = "ratio.seq.open 回执里的 sequence。", Required = true,
    };

    private static ParameterSpec EpochParameter() => new()
    {
        Name = "epoch", Description = "ratio.seq.open 回执里的 epoch（缓存代次，服务换模后旧句柄作废）。", Required = true,
    };

    private static ParameterSpec StreamParameter() => new()
    {
        Name = "stream", Description = "ratio.seq.stream 或 ratio.chat.stream 回执里的 stream 编号。", Required = true, Position = 0,
    };

    private static ParameterSpec Timeout(int seconds) => new()
    {
        Name = "timeout", Description = "最多等多少秒（1-600）。", Type = ParamType.Int, Default = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>可预期的失败翻成失败回执；共享 KV 的拒绝原因码原样放在消息里（如 invalid_sequence_size）。</summary>
    private static Func<CommandContext, Task<CommandResult>> Guard(Func<CommandContext, Task<CommandResult>> handler)
        => async context =>
        {
            try
            {
                return await handler(context).ConfigureAwait(false);
            }
            catch (RatioInputException ex)
            {
                return CommandResult.Fail(ex.Message);
            }
            catch (RatioKvException ex)
            {
                return CommandResult.Fail("shared_kv:" + ex.Message);
            }
            catch (RatioRemoteException ex)
            {
                return CommandResult.Fail(ex.Message);
            }
        };
}
