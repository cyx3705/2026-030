using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace HistoryApollo;

/// <summary>Apollo 的全部指令：一次调用、一次配置，没有第三类。</summary>
internal static class ApolloCommandCatalog
{
    private const string Domain = "apollo";
    internal const string Source = "module:HistoryApollo";

    public static void Register(CommandRegistry registry, ProviderStore store, ChatClient client, IWebSearch search)
    {
        registry.Register(
            new CommandDescriptor
            {
                Name = "apollo.chat.ask",
                Domain = Domain,
                CommandClass = "chat",
                Summary = "向模型提一个问题并取回答复；web=true 时模型可按需联网搜索。",
                Example = "apollo.chat.ask prompt=CDQ2B20-10D是哪个品牌的气缸 web=true",
                Level = CommandLevel.Run,
                // 不写本机状态，但会发出一次计费的外网请求，所以不声明 Readonly。
                Readonly = false,
                Parameters =
                [
                    new ParameterSpec
                    {
                        Name = "prompt",
                        Description = "要问的内容。",
                        Required = true,
                        Position = 0,
                    },
                    ..SharedParameters(),
                ],
                Handler = Guard(async context =>
                {
                    var messages = ChatTranscript.FromPrompt(
                        context.GetString("system"),
                        context.GetString("prompt"));
                    return await CompleteAsync(store, client, search, context, messages).ConfigureAwait(false);
                }),
            },
            Source);

        registry.Register(
            new CommandDescriptor
            {
                Name = "apollo.chat.send",
                Domain = Domain,
                CommandClass = "chat",
                Summary = "按完整消息数组发起一次多轮对话调用；web=true 时模型可按需联网搜索。",
                Example = "apollo.chat.send messages=[{\"role\":\"user\",\"content\":\"继续\"}]",
                Level = CommandLevel.Run,
                Readonly = false,
                Parameters =
                [
                    new ParameterSpec
                    {
                        Name = "messages",
                        Description = "JSON 数组，每项含 role(system/user/assistant) 与 content。上下文由调用方持有。",
                        Required = true,
                        Position = 0,
                    },
                    ..SharedParameters(),
                ],
                Handler = Guard(async context =>
                {
                    var messages = ChatTranscript.Parse(context.GetString("messages"));
                    return await CompleteAsync(store, client, search, context, messages).ConfigureAwait(false);
                }),
            },
            Source);

        registry.Register(
            new CommandDescriptor
            {
                Name = "apollo.model.list",
                Domain = Domain,
                CommandClass = "model",
                Summary = "列出供应商当前可用的模型。",
                Example = "apollo.model.list provider=deepseek",
                Level = CommandLevel.Run,
                Readonly = true,
                Parameters =
                [
                    ProviderParameter(0),
                    TimeoutParameter(),
                ],
                Handler = Guard(async context =>
                {
                    var profile = store.ResolveChat(context.GetString("provider"));
                    var models = await client.ListModelsAsync(
                        profile,
                        context.GetInt("timeout", 30),
                        context.Cancellation).ConfigureAwait(false);

                    var text = models.Count == 0
                        ? $"{profile.Name} 没有返回任何模型"
                        : $"{profile.Name} 可用模型（默认 {profile.Model}）：{Environment.NewLine}"
                          + string.Join(Environment.NewLine, models.Select(model => "  " + model));
                    return CommandResult.Ok(text, JsonSerializer.Serialize(models));
                }),
            },
            Source);

        registry.Register(
            new CommandDescriptor
            {
                Name = "apollo.provider.list",
                Domain = Domain,
                CommandClass = "provider",
                Summary = "列出已配置的供应商、默认模型和密钥来源（密钥只显示掩码）。",
                Example = "apollo.provider.list",
                Level = CommandLevel.Run,
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ =>
                {
                    var current = store.DefaultProvider;
                    var lines = new StringBuilder();
                    foreach (var profile in store.All())
                    {
                        lines.AppendLine(
                            $"{(profile.Name.Equals(current, StringComparison.OrdinalIgnoreCase) ? "*" : " ")} "
                            + $"{profile.Name}  {(profile.Kind == ProviderKind.Search ? "类型=搜索" : "模型=" + profile.Model)}  接入点={profile.BaseUrl}  "
                            + $"密钥={ProviderStore.Mask(profile.ApiKey)}({profile.KeySource})");
                    }

                    return CommandResult.Ok(lines.ToString().TrimEnd(), DescribeProviders(store));
                }),
            },
            Source);

        registry.Register(
            new CommandDescriptor
            {
                Name = "apollo.provider.use",
                Domain = Domain,
                CommandClass = "provider",
                Summary = "切换默认供应商。",
                Example = "apollo.provider.use deepseek",
                Level = CommandLevel.Run,
                Parameters =
                [
                    new ParameterSpec
                    {
                        Name = "name",
                        Description = "供应商名称。",
                        Required = true,
                        Position = 0,
                    },
                ],
                Handler = Guard(context =>
                {
                    var profile = store.SetDefaultProvider(context.RequireString("name"));
                    return Task.FromResult(CommandResult.Ok(
                        $"默认供应商已设为 {profile.Name}（模型 {profile.Model}）",
                        DescribeProviders(store)));
                }),
            },
            Source);

        registry.Register(
            new CommandDescriptor
            {
                Name = "apollo.provider.config",
                Domain = Domain,
                CommandClass = "provider",
                Summary = "设置供应商的默认模型或接入点；给出 baseurl 时可登记一家新供应商。",
                Example = "apollo.provider.config provider=deepseek model=deepseek-reasoner",
                Level = CommandLevel.Run,
                Parameters =
                [
                    ProviderParameter(0),
                    new ParameterSpec
                    {
                        Name = "model",
                        Description = "该供应商的默认模型。",
                        Position = 1,
                    },
                    new ParameterSpec
                    {
                        Name = "baseurl",
                        Description = "OpenAI 兼容接入点根地址，不含 /chat/completions。",
                    },
                ],
                Handler = Guard(context =>
                {
                    var provider = context.GetString("provider");
                    var model = context.GetString("model");
                    var baseUrl = context.GetString("baseurl");
                    if (string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(baseUrl))
                        throw new ApolloInputException("至少要给出 model= 或 baseurl= 之一");

                    ProviderProfile profile;
                    if (!store.Knows(provider))
                    {
                        // 名字还不认识：只有同时给出 baseurl 才是「登记新供应商」，
                        // 否则它多半是个拼错的 provider=，应当照旧报未知供应商。
                        if (string.IsNullOrWhiteSpace(baseUrl))
                            _ = store.Resolve(provider);
                        profile = store.Define(provider!, baseUrl!, model);
                        return Task.FromResult(CommandResult.Ok(
                            $"已登记供应商 {profile.Name}：模型={profile.Model} 接入点={profile.BaseUrl}",
                            DescribeProviders(store)));
                    }

                    profile = store.Resolve(provider);
                    if (!string.IsNullOrWhiteSpace(model))
                        profile = store.SetModel(profile.Name, model);
                    if (!string.IsNullOrWhiteSpace(baseUrl))
                        profile = store.SetBaseUrl(profile.Name, baseUrl);

                    return Task.FromResult(CommandResult.Ok(
                        $"{profile.Name}：模型={profile.Model} 接入点={profile.BaseUrl}",
                        DescribeProviders(store)));
                }),
            },
            Source);

        registry.Register(
            new CommandDescriptor
            {
                Name = "apollo.key.set",
                Domain = Domain,
                CommandClass = "key",
                Summary = "写入供应商密钥，只存在本机模块数据目录，回执与日志一律掩码。",
                Example = "apollo.key.set token=<密钥> provider=deepseek",
                Level = CommandLevel.Run,
                Parameters =
                [
                    // 参数名叫 token：总线按名字判定敏感值，回显、历史和结果都会自动遮蔽。
                    // 位置 0 让位置传参同样被遮。改名成 apikey 会让这层保护整体失效。
                    new ParameterSpec
                    {
                        Name = "token",
                        Description = "供应商密钥。",
                        Required = true,
                        Position = 0,
                    },
                    ProviderParameter(1),
                ],
                Handler = Guard(context =>
                {
                    var profile = store.SetKey(context.GetString("provider"), context.RequireString("token"));
                    ApolloRuntime.Log("key", $"{profile.Name} 密钥已更新：{ProviderStore.Mask(profile.ApiKey)}");
                    var note = profile.KeySource.StartsWith("env:", StringComparison.Ordinal)
                        ? $"；注意 {profile.KeySource} 仍然优先生效"
                        : string.Empty;
                    return Task.FromResult(CommandResult.Ok(
                        $"{profile.Name} 密钥已保存：{ProviderStore.Mask(profile.ApiKey)}{note}"));
                }),
            },
            Source);

        registry.Register(
            new CommandDescriptor
            {
                Name = "apollo.key.clear",
                Domain = Domain,
                CommandClass = "key",
                Summary = "删除本机保存的供应商密钥。",
                Example = "apollo.key.clear provider=deepseek",
                Level = CommandLevel.Ask,
                ConfirmPrompt = context =>
                    $"确认删除 {context.GetString("provider") ?? "默认"} 供应商保存的密钥？删除后需要重新提供密钥。",
                Parameters = [ProviderParameter(0)],
                Handler = Guard(context =>
                {
                    var profile = store.ClearKey(context.GetString("provider"));
                    var remaining = profile.HasKey
                        ? $"；{profile.KeySource} 仍提供着一把密钥"
                        : string.Empty;
                    return Task.FromResult(CommandResult.Ok($"{profile.Name} 已清除本机密钥{remaining}"));
                }),
            },
            Source);
    }

    private static async Task<CommandResult> CompleteAsync(
        ProviderStore store,
        ChatClient client,
        IWebSearch search,
        CommandContext context,
        IReadOnlyList<ChatMessage> messages)
    {
        var profile = store.ResolveChat(context.GetString("provider"));
        var jsonOutput = context.GetBool("json", false);
        var options = new ChatOptions
        {
            Model = context.GetString("model"),
            Temperature = context.Has("temperature") ? context.GetDouble("temperature", 1.0) : null,
            MaxTokens = context.Has("maxtokens") ? context.GetInt("maxtokens", 0) : null,
            JsonOutput = jsonOutput,
            TimeoutSeconds = context.GetInt("timeout", 120),
            WebSearch = context.GetBool("web", false),
        };

        var outcome = await client.CompleteAsync(profile, messages, options, context.Cancellation, search)
            .ConfigureAwait(false);

        // 搜过什么进本地日志：模型凭哪几次搜索下的结论，事后要查得到。
        var searched = outcome.SearchCount == 0
            ? string.Empty
            : $" 搜索 {outcome.SearchCount} 次：{string.Join(" | ", outcome.Searches!.Select(trace => trace.Query))}";
        ApolloRuntime.Log(
            "chat",
            $"{outcome.Provider}/{outcome.Model} {outcome.TotalTokens} tokens {outcome.ElapsedMilliseconds}ms{searched}");

        return CommandResult.Ok(
            ChatTranscript.Describe(outcome, jsonOutput),
            ChatTranscript.ToJson(outcome));
    }

    /// <summary>两条 chat 指令共用的可选项。</summary>
    private static IReadOnlyList<ParameterSpec> SharedParameters() =>
    [
        new ParameterSpec
        {
            Name = "system",
            Description = "系统提示；apollo.chat.send 忽略本项，请直接放进 messages。",
        },
        new ParameterSpec
        {
            Name = "model",
            Description = "本次使用的模型；省略时用供应商默认模型。",
        },
        ProviderParameter(null),
        new ParameterSpec
        {
            Name = "temperature",
            Description = "采样温度；省略时用服务端默认值。",
            Type = ParamType.Double,
        },
        new ParameterSpec
        {
            Name = "maxtokens",
            Description = "本次回复的最大 token 数。",
            Type = ParamType.Int,
        },
        new ParameterSpec
        {
            Name = "json",
            Description = "要求模型返回严格 JSON 对象；为真时回执不附加用量脚注。",
            Type = ParamType.Bool,
            Default = "false",
        },
        new ParameterSpec
        {
            Name = "web",
            Description = $"允许模型按需联网搜索（博查）：搜不搜、搜什么由模型决定，每次调用最多 {ChatClient.MaxSearches} 次，搜索另行计费。",
            Type = ParamType.Bool,
            Default = "false",
        },
        TimeoutParameter(),
    ];

    private static ParameterSpec ProviderParameter(int? position) => new()
    {
        Name = "provider",
        Description = "供应商名称；省略时用默认供应商。",
        Position = position,
    };

    private static ParameterSpec TimeoutParameter() => new()
    {
        Name = "timeout",
        Description = "本次请求超时秒数（1-600）。",
        Type = ParamType.Int,
        Default = "120",
    };

    private static string DescribeProviders(ProviderStore store)
        => JsonSerializer.Serialize(
            new
            {
                @default = store.DefaultProvider,
                providers = store.All().Select(profile => new
                {
                    name = profile.Name,
                    kind = profile.Kind == ProviderKind.Search ? "search" : "chat",
                    model = profile.Model,
                    baseUrl = profile.BaseUrl,
                    key = ProviderStore.Mask(profile.ApiKey),
                    keySource = profile.KeySource,
                }),
            },
            new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// 把两类可预期的失败翻译成失败回执。
    /// </summary>
    /// <remarks>
    /// 输入错和远端拒绝都是**结果**，不是故障：让它们以异常穿到总线，
    /// 调用方看到的是一段与自己无关的堆栈，而真正该看的那句话被埋在里面。
    /// 其余异常照旧上抛，由总线记为内部故障。
    /// </remarks>
    private static Func<CommandContext, Task<CommandResult>> Guard(
        Func<CommandContext, Task<CommandResult>> handler)
        => async context =>
        {
            try
            {
                return await handler(context).ConfigureAwait(false);
            }
            catch (ApolloInputException ex)
            {
                return CommandResult.Fail(ex.Message);
            }
            catch (ApolloRemoteException ex)
            {
                ApolloRuntime.Log("chat", $"调用失败：{ex.Message}");
                return CommandResult.Fail(ex.Message);
            }
        };
}
