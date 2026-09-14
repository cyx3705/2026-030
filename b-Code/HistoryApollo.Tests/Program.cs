using System.Net;
using System.Text.Json;
using HistoryApollo;
using HistoryVulcan.Core.Commands;

var tests = new (string Name, Func<Task> Run)[]
{
    ("provider defaults", TestProviderDefaultsAsync),
    ("key roundtrip", TestKeyRoundtripAsync),
    ("define provider", TestDefineProviderAsync),
    ("environment key wins", TestEnvironmentKeyAsync),
    ("mask", TestMaskAsync),
    ("transcript from prompt", TestTranscriptFromPromptAsync),
    ("transcript parse", TestTranscriptParseAsync),
    ("chat outcome", TestChatOutcomeAsync),
    ("chat error mapping", TestChatErrorAsync),
    ("missing key", TestMissingKeyAsync),
    ("model list", TestModelListAsync),
    ("result footer", TestResultFooterAsync),
    ("command registration", TestCommandRegistrationAsync),
    ("secret parameter contract", TestSecretParameterContractAsync),
    ("search provider kind", TestSearchProviderKindAsync),
    ("web search request", TestWebSearchRequestAsync),
    ("web search tool loop", TestWebSearchToolLoopAsync),
    ("web search budget", TestWebSearchBudgetAsync),
    ("chat trace", TestChatTraceAsync),
};

// 联网实调默认不跑：它要花钱、要外网，且依赖本机已配置的真实密钥。
// 交付验收时显式打开 APOLLO_LIVE=1，让「能不能真的调通」这件事也有可重复的证据。
if (string.Equals(Environment.GetEnvironmentVariable("APOLLO_LIVE"), "1", StringComparison.Ordinal))
    tests = [.. tests, ("live deepseek call", TestLiveCallAsync)];

// 联网搜索实调另开一个开关：它还要博查密钥，不该让只配了 DeepSeek 的机器 VERIFY-LIVE 变红。
if (string.Equals(Environment.GetEnvironmentVariable("APOLLO_LIVE_WEB"), "1", StringComparison.Ordinal))
    tests = [.. tests, ("live web search call", TestLiveWebCallAsync)];

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failed == 0 ? 0 : 1;

static Task TestProviderDefaultsAsync()
{
    using var scope = new StoreScope();
    var profile = scope.Store.Resolve(null);
    Equal("deepseek", profile.Name);
    Equal("https://api.deepseek.com", profile.BaseUrl);
    Equal("deepseek-chat", profile.Model);
    Equal("none", profile.KeySource);
    True(!profile.HasKey, "全新库不应带出密钥");
    Throws<ApolloInputException>(() => scope.Store.Resolve("nowhere"));
    return Task.CompletedTask;
}

static Task TestKeyRoundtripAsync()
{
    using var scope = new StoreScope();
    scope.Store.SetKey(null, "sk-0123456789abcdef");
    scope.Store.SetModel(null, "deepseek-reasoner");

    // 重新打开同一个文件：配置必须落盘，而不是只活在内存里。
    var reopened = new ProviderStore(scope.Store.Path);
    var profile = reopened.Resolve("deepseek");
    Equal("sk-0123456789abcdef", profile.ApiKey!);
    Equal("deepseek-reasoner", profile.Model);
    Equal("store", profile.KeySource);

    var text = File.ReadAllText(scope.Store.Path);
    True(text.Contains("sk-0123456789abcdef", StringComparison.Ordinal), "密钥应保存在模块数据目录");

    var cleared = reopened.ClearKey("deepseek");
    True(!cleared.HasKey, "清除后不应再有密钥");
    Throws<ApolloInputException>(() => reopened.SetKey(null, "  "));
    Throws<ApolloInputException>(() => reopened.SetKey(null, "sk-with space"));
    Throws<ApolloInputException>(() => reopened.SetBaseUrl(null, "ftp://example.com"));
    return Task.CompletedTask;
}

static Task TestDefineProviderAsync()
{
    using var scope = new StoreScope();
    True(!scope.Store.Knows("moonshot"), "库里本不该认识 moonshot");
    Throws<ApolloInputException>(() => scope.Store.SetModel("moonshot", "kimi"));

    var defined = scope.Store.Define("MoonShot", "https://api.moonshot.cn/v1/", "kimi");
    Equal("moonshot", defined.Name);
    Equal("https://api.moonshot.cn/v1", defined.BaseUrl);
    Equal("kimi", defined.Model);
    True(scope.Store.Knows("moonshot"), "登记后应认识 moonshot");

    // 内置的两家（deepseek 与搜索服务 bocha）仍在，登记新供应商不会顶掉它们。
    Equal("https://api.deepseek.com", scope.Store.Resolve("deepseek").BaseUrl);
    Equal(3, scope.Store.All().Count);

    Throws<ApolloInputException>(() => scope.Store.Define("bad name", "https://x.test", null));
    Throws<ApolloInputException>(() => scope.Store.Define("ok", "not-a-url", null));
    return Task.CompletedTask;
}

static Task TestEnvironmentKeyAsync()
{
    using var scope = new StoreScope();
    scope.Store.SetKey(null, "sk-from-store-000");
    Environment.SetEnvironmentVariable("APOLLO_DEEPSEEK_KEY", "sk-from-env-1111");
    try
    {
        var profile = new ProviderStore(scope.Store.Path).Resolve(null);
        Equal("sk-from-env-1111", profile.ApiKey!);
        Equal("env:APOLLO_DEEPSEEK_KEY", profile.KeySource);
    }
    finally
    {
        Environment.SetEnvironmentVariable("APOLLO_DEEPSEEK_KEY", null);
    }

    return Task.CompletedTask;
}

static Task TestMaskAsync()
{
    Equal("(未配置)", ProviderStore.Mask(null));
    Equal("********", ProviderStore.Mask("12345678"));
    // 形状与真实 DeepSeek 密钥一致（sk- + 32 位），但刻意不是十六进制：
    // 项目合同检查按「sk- 后接 32 位以上十六进制」判定真实密钥入库，这条不该被它命中。
    var masked = ProviderStore.Mask("sk-fakekeyzzzzzzzzzzzzzzzzzzzzzzzz");
    True(!masked.Contains("keyzzz", StringComparison.Ordinal), "掩码不得带出密钥中段");
    Equal("sk-f******zzzz", masked);
    return Task.CompletedTask;
}

static Task TestTranscriptFromPromptAsync()
{
    var plain = ChatTranscript.FromPrompt(null, "你好");
    Equal(1, plain.Count);
    Equal("user", plain[0].Role);

    var guided = ChatTranscript.FromPrompt("你是史官", "你好");
    Equal(2, guided.Count);
    Equal("system", guided[0].Role);
    Equal("你是史官", guided[0].Content);

    Throws<ApolloInputException>(() => ChatTranscript.FromPrompt(null, "  "));
    return Task.CompletedTask;
}

static Task TestTranscriptParseAsync()
{
    var messages = ChatTranscript.Parse(
        """[{"role":"system","content":"a"},{"role":"user","content":"b"}]""");
    Equal(2, messages.Count);
    Equal("system", messages[0].Role);
    Equal("b", messages[1].Content);

    Throws<ApolloInputException>(() => ChatTranscript.Parse("{}"));
    Throws<ApolloInputException>(() => ChatTranscript.Parse("[]"));
    Throws<ApolloInputException>(() => ChatTranscript.Parse("""[{"role":"root","content":"x"}]"""));
    Throws<ApolloInputException>(() => ChatTranscript.Parse("""[{"role":"user"}]"""));
    Throws<ApolloInputException>(() => ChatTranscript.Parse("not json"));
    return Task.CompletedTask;
}

static async Task TestChatOutcomeAsync()
{
    string? captured = null;
    using var client = FakeTransport.Client(
        (request, body) =>
        {
            captured = body;
            Equal("https://api.deepseek.com/chat/completions", request.RequestUri!.ToString());
            Equal("Bearer", request.Headers.Authorization!.Scheme);
            Equal("sk-test", request.Headers.Authorization.Parameter!);
            return (HttpStatusCode.OK, """
                {
                  "model": "deepseek-reasoner",
                  "choices": [{
                    "message": {"content": "42", "reasoning_content": "先想一想"},
                    "finish_reason": "stop"
                  }],
                  "usage": {"prompt_tokens": 11, "completion_tokens": 3, "total_tokens": 14}
                }
                """);
        });

    var profile = new ProviderProfile("deepseek", "https://api.deepseek.com", "deepseek-chat", "sk-test", "store");
    var outcome = await client.CompleteAsync(
        profile,
        [new ChatMessage("user", "答案是多少")],
        new ChatOptions { Model = "deepseek-reasoner", Temperature = 0.2, MaxTokens = 64, JsonOutput = true },
        CancellationToken.None);

    Equal("42", outcome.Content);
    Equal("先想一想", outcome.Reasoning!);
    Equal("deepseek-reasoner", outcome.Model);
    Equal("stop", outcome.FinishReason!);
    Equal(14, outcome.TotalTokens);

    using var sent = JsonDocument.Parse(captured!);
    var root = sent.RootElement;
    Equal("deepseek-reasoner", root.GetProperty("model").GetString()!);
    Equal(false, root.GetProperty("stream").GetBoolean());
    Equal(0.2, root.GetProperty("temperature").GetDouble());
    Equal(64, root.GetProperty("max_tokens").GetInt32());
    Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString()!);
    Equal("答案是多少", root.GetProperty("messages")[0].GetProperty("content").GetString()!);

    var payload = ChatTranscript.ToJson(outcome);
    True(payload.Contains("\"total\": 14", StringComparison.Ordinal), "结构化载荷应带用量");
    True(!payload.Contains("sk-test", StringComparison.Ordinal), "结构化载荷不得带出密钥");
}

static async Task TestChatErrorAsync()
{
    using var client = FakeTransport.Client((_, _) => (
        HttpStatusCode.Unauthorized,
        """{"error": {"message": "Authentication Fails"}}"""));

    var profile = new ProviderProfile("deepseek", "https://api.deepseek.com", "deepseek-chat", "sk-bad", "store");
    var failure = await ThrowsAsync<ApolloRemoteException>(() => client.CompleteAsync(
        profile,
        [new ChatMessage("user", "hi")],
        new ChatOptions(),
        CancellationToken.None));

    True(failure.Message.Contains("401", StringComparison.Ordinal), "失败回执应带状态码");
    True(failure.Message.Contains("Authentication Fails", StringComparison.Ordinal), "失败回执应带服务端原因");

    using var garbage = FakeTransport.Client((_, _) => (HttpStatusCode.OK, "<html>gateway</html>"));
    await ThrowsAsync<ApolloRemoteException>(() => garbage.CompleteAsync(
        profile,
        [new ChatMessage("user", "hi")],
        new ChatOptions(),
        CancellationToken.None));
}

static async Task TestMissingKeyAsync()
{
    using var client = FakeTransport.Client((_, _) => throw new InvalidOperationException("不该发出请求"));
    var profile = new ProviderProfile("deepseek", "https://api.deepseek.com", "deepseek-chat", null, "none");

    var failure = await ThrowsAsync<ApolloInputException>(() => client.CompleteAsync(
        profile,
        [new ChatMessage("user", "hi")],
        new ChatOptions(),
        CancellationToken.None));
    True(failure.Message.Contains("apollo.key.set", StringComparison.Ordinal), "应指出配置密钥的办法");

    await ThrowsAsync<ApolloInputException>(() => client.CompleteAsync(
        profile with { ApiKey = "sk-test" },
        [],
        new ChatOptions(),
        CancellationToken.None));

    await ThrowsAsync<ApolloInputException>(() => client.CompleteAsync(
        profile with { ApiKey = "sk-test" },
        [new ChatMessage("user", "hi")],
        new ChatOptions { MaxTokens = 0 },
        CancellationToken.None));

    await ThrowsAsync<ApolloInputException>(() => client.CompleteAsync(
        profile with { ApiKey = "sk-test", Model = string.Empty },
        [new ChatMessage("user", "hi")],
        new ChatOptions(),
        CancellationToken.None));
}

static async Task TestModelListAsync()
{
    using var client = FakeTransport.Client((request, _) =>
    {
        Equal("https://api.deepseek.com/models", request.RequestUri!.ToString());
        Equal(HttpMethod.Get, request.Method);
        return (HttpStatusCode.OK, """{"data":[{"id":"deepseek-reasoner"},{"id":"deepseek-chat"}]}""");
    });

    var profile = new ProviderProfile("deepseek", "https://api.deepseek.com", "deepseek-chat", "sk-test", "store");
    var models = await client.ListModelsAsync(profile, 30, CancellationToken.None);
    Equal(2, models.Count);
    Equal("deepseek-chat", models[0]);
}

static Task TestResultFooterAsync()
{
    var outcome = new ChatOutcome("deepseek", "deepseek-chat", "{\"a\":1}", null, "stop", 5, 7, 12, 1500);
    Equal("{\"a\":1}", ChatTranscript.Describe(outcome, jsonOutput: true));

    var plain = ChatTranscript.Describe(outcome, jsonOutput: false);
    True(plain.StartsWith("{\"a\":1}", StringComparison.Ordinal), "正文必须在最前");
    True(plain.Contains("5+7=12 tokens", StringComparison.Ordinal), "脚注应报告用量");
    return Task.CompletedTask;
}

static Task TestCommandRegistrationAsync()
{
    using var scope = new StoreScope();
    using var client = FakeTransport.Client((_, _) => (HttpStatusCode.OK, "{}"));
    var registry = new CommandRegistry();

    // 注册表在这里会同时校验 Ask 与 ConfirmPrompt 是否配套、命令类是否合法。
    ApolloCommandCatalog.Register(registry, scope.Store, client, new FakeSearch(_ => []));

    string[] expected =
    [
        "apollo.chat.ask",
        "apollo.chat.send",
        "apollo.model.list",
        "apollo.provider.list",
        "apollo.provider.use",
        "apollo.provider.config",
        "apollo.key.set",
        "apollo.key.clear",
    ];
    foreach (var name in expected)
    {
        True(registry.TryGet(name, out var descriptor), $"未注册 {name}");
        Equal("apollo", descriptor!.Domain!);
        Equal(ApolloCommandCatalog.Source, registry.GetSource(name)!);
    }

    Equal(
        expected.Length,
        registry.All().Count(descriptor => descriptor.Name.StartsWith("apollo.", StringComparison.Ordinal)));

    True(registry.TryGet("apollo.key.clear", out var clear), "缺少 apollo.key.clear");
    Equal(CommandLevel.Ask, clear!.Level);
    True(clear.ConfirmPrompt != null, "删除密钥必须要人确认");

    True(registry.TryGet("apollo.model.list", out var models), "缺少 apollo.model.list");
    True(models!.Readonly, "列模型是只读的");

    True(registry.TryGet("apollo.chat.ask", out var ask), "缺少 apollo.chat.ask");
    True(!ask!.Readonly, "对话会发出计费请求，不应声明为只读");

    foreach (var name in new[] { "apollo.chat.ask", "apollo.chat.send" })
    {
        True(registry.TryGet(name, out var chat), $"缺少 {name}");
        var web = chat!.Parameters.SingleOrDefault(parameter => parameter.Name == "web");
        True(web is { Default: "false" }, $"{name} 必须提供默认关闭的 web 参数：联网另行计费，不能悄悄打开");
    }

    return Task.CompletedTask;
}

static Task TestSecretParameterContractAsync()
{
    using var scope = new StoreScope();
    using var client = FakeTransport.Client((_, _) => (HttpStatusCode.OK, "{}"));
    var registry = new CommandRegistry();
    ApolloCommandCatalog.Register(registry, scope.Store, client, new FakeSearch(_ => []));

    // 总线按参数名判定敏感值：名字必须以 token 结尾，位置 0 才会被位置传参的遮蔽覆盖。
    // 改名成 apikey / key 会让回显、命令历史和结果脱敏整体失效，所以在这里钉住。
    True(registry.TryGet("apollo.key.set", out var descriptor), "缺少 apollo.key.set");
    var secret = descriptor!.Parameters.Single(parameter => parameter.Required);
    Equal("token", secret.Name);
    Equal(0, secret.Position!.Value);
    return Task.CompletedTask;
}

/// <summary>用本机真实配置向 DeepSeek 发一次最小请求；只在 APOLLO_LIVE=1 时运行。</summary>
static async Task TestLiveCallAsync()
{
    var profile = ApolloRuntime.Providers.Resolve(null);
    True(profile.HasKey, $"{profile.Name} 未配置密钥，无法做联网实调");

    using var client = new ChatClient();
    var outcome = await client.CompleteAsync(
        profile,
        ChatTranscript.FromPrompt("只回一个词。", "用一个词回答：今天的天空通常是什么颜色？"),
        new ChatOptions { MaxTokens = 16, TimeoutSeconds = 60 },
        CancellationToken.None);

    True(!string.IsNullOrWhiteSpace(outcome.Content), "实调应返回非空正文");
    True(outcome.TotalTokens > 0, "实调应返回用量");
    Console.WriteLine($"     实调 {outcome.Provider}/{outcome.Model} → {outcome.Content.Trim()}"
                      + $"（{outcome.TotalTokens} tokens，{outcome.ElapsedMilliseconds}ms）");
}

static Task TestSearchProviderKindAsync()
{
    using var scope = new StoreScope();
    var bocha = scope.Store.Resolve(ProviderStore.SearchProvider);
    Equal(ProviderKind.Search, bocha.Kind);
    Equal("https://api.bochaai.com", bocha.BaseUrl);
    Equal(ProviderKind.Chat, scope.Store.Resolve(null).Kind);

    // 搜索服务的钥匙与对话供应商同库、同一条指令写入，但它不能被当成对话供应商来用。
    scope.Store.SetKey("bocha", "bocha-fake-key-0000");
    True(new ProviderStore(scope.Store.Path).Resolve("bocha").HasKey, "博查密钥应落盘");
    var misuse = Throws<ApolloInputException>(() => scope.Store.ResolveChat("bocha"));
    True(misuse.Message.Contains("web=true", StringComparison.Ordinal), "拒绝时应指出联网的正确用法");
    Throws<ApolloInputException>(() => scope.Store.SetDefaultProvider("bocha"));
    Equal("deepseek", scope.Store.DefaultProvider);
    return Task.CompletedTask;
}

static async Task TestWebSearchRequestAsync()
{
    using var scope = new StoreScope();
    string? captured = null;
    using var search = new WebSearchClient(scope.Store, FakeTransport.Http((request, body) =>
    {
        captured = body;
        Equal("https://api.bochaai.com/v1/web-search", request.RequestUri!.ToString());
        Equal(HttpMethod.Post, request.Method);
        Equal("Bearer", request.Headers.Authorization!.Scheme);
        return (HttpStatusCode.OK, """
            {"code":200,"msg":null,"data":{"webPages":{"value":[
              {"name":"SMC 薄型气缸 CDQ2B20-10D","url":"https://example.test/a","siteName":"SMC","datePublished":"2025-01-02T00:00:00+08:00","snippet":"短摘要","summary":"长摘要"},
              {"name":"只有片段","url":"https://example.test/b","snippet":"只有 snippet"},
              {"name":"没有链接的条目"}
            ]}}}
            """);
    }));

    // 机器上若已用环境变量配了博查密钥，「没配密钥」这一段无从验证，跳过它而不是误报。
    if (Environment.GetEnvironmentVariable("APOLLO_BOCHA_KEY") is null
        && Environment.GetEnvironmentVariable("BOCHA_API_KEY") is null)
    {
        var missing = await ThrowsAsync<ApolloInputException>(
            () => search.SearchAsync("CDQ2B20-10D", 5, 30, CancellationToken.None));
        True(missing.Message.Contains("apollo.key.set provider=bocha", StringComparison.Ordinal), "应指出配置博查密钥的办法");
        True(captured is null, "没有密钥时不得发出请求");
    }

    scope.Store.SetKey("bocha", "bocha-fake-key-0000");
    var hits = await search.SearchAsync("CDQ2B20-10D", 5, 30, CancellationToken.None);
    Equal(2, hits.Count);
    Equal("长摘要", hits[0].Summary);
    Equal("SMC", hits[0].SiteName!);
    Equal("只有 snippet", hits[1].Summary);

    using var sent = JsonDocument.Parse(captured!);
    Equal("CDQ2B20-10D", sent.RootElement.GetProperty("query").GetString()!);
    Equal(true, sent.RootElement.GetProperty("summary").GetBoolean());
    Equal(5, sent.RootElement.GetProperty("count").GetInt32());

    // 业务错误装在 HTTP 200 里：必须变成失败，不能当成「没有结果」让模型一本正经地答「查不到」。
    var rejected = Throws<ApolloRemoteException>(
        () => WebSearchClient.ReadHits("bocha", """{"code":403,"msg":"余额不足","data":null}"""));
    True(rejected.Message.Contains("余额不足", StringComparison.Ordinal), "业务错误应带出服务端原因");
    Equal(0, WebSearchClient.ReadHits("bocha", """{"code":"200","data":{}}""").Count);
}

static async Task TestWebSearchToolLoopAsync()
{
    var bodies = new List<string>();
    using var client = FakeTransport.Client((_, body) =>
    {
        bodies.Add(body!);
        return bodies.Count == 1
            ? (HttpStatusCode.OK, """
                {"model":"deepseek-flash","choices":[{"finish_reason":"tool_calls","message":{
                  "role":"assistant","content":"","reasoning_content":"先搜型号",
                  "tool_calls":[{"id":"call_1","type":"function","function":{"name":"web_search","arguments":"{\"query\":\"CDQ2B20-10D 品牌\"}"}}]}}],
                 "usage":{"prompt_tokens":100,"completion_tokens":10,"total_tokens":110}}
                """)
            : (HttpStatusCode.OK, """
                {"model":"deepseek-flash","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"{\"brand\":\"SMC\"}"}}],
                 "usage":{"prompt_tokens":300,"completion_tokens":5,"total_tokens":305}}
                """);
    });

    var search = new FakeSearch(_ =>
        [new WebSearchHit("SMC 薄型气缸", "https://example.test/smc", "SMC", null, "CDQ2B20-10D 是 SMC 的薄型气缸")]);
    var profile = new ProviderProfile("deepseek", "https://api.deepseek.com", "deepseek-chat", "sk-test", "store");
    var outcome = await client.CompleteAsync(
        profile,
        [new ChatMessage("user", "CDQ2B20-10D 是什么品牌")],
        new ChatOptions { WebSearch = true, JsonOutput = true },
        CancellationToken.None,
        search);

    Equal("{\"brand\":\"SMC\"}", outcome.Content);
    Equal(1, outcome.SearchCount);
    Equal("CDQ2B20-10D 品牌", outcome.Searches![0].Query);
    Equal(415, outcome.TotalTokens);
    Equal(1, search.Queries.Count);
    Equal(2, bodies.Count);

    using var first = JsonDocument.Parse(bodies[0]);
    Equal(
        "web_search",
        first.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString()!);

    // 第二轮：助手消息原样回放（含 reasoning_content），tool 结果按 tool_call_id 对上。
    using var second = JsonDocument.Parse(bodies[1]);
    var replay = second.RootElement.GetProperty("messages");
    Equal(3, replay.GetArrayLength());
    Equal("assistant", replay[1].GetProperty("role").GetString()!);
    Equal("先搜型号", replay[1].GetProperty("reasoning_content").GetString()!);
    Equal("call_1", replay[1].GetProperty("tool_calls")[0].GetProperty("id").GetString()!);
    Equal("tool", replay[2].GetProperty("role").GetString()!);
    Equal("call_1", replay[2].GetProperty("tool_call_id").GetString()!);
    True(
        replay[2].GetProperty("content").GetString()!.Contains("CDQ2B20-10D 是 SMC 的薄型气缸", StringComparison.Ordinal),
        "搜索结果必须作为 tool 消息交回模型");

    using var payload = JsonDocument.Parse(ChatTranscript.ToJson(outcome));
    Equal("CDQ2B20-10D 品牌", payload.RootElement.GetProperty("searches")[0].GetProperty("query").GetString()!);
    True(ChatTranscript.Describe(outcome, jsonOutput: false).Contains("联网搜索 1 次", StringComparison.Ordinal), "脚注应报告搜索次数");

    // 不开 web：请求里不得带工具；要求联网却没接搜索服务，是输入错误。
    var plainBodies = new List<string>();
    using var plain = FakeTransport.Client((_, body) =>
    {
        plainBodies.Add(body!);
        return (HttpStatusCode.OK, """{"choices":[{"finish_reason":"stop","message":{"content":"好"}}]}""");
    });
    var quiet = await plain.CompleteAsync(profile, [new ChatMessage("user", "hi")], new ChatOptions(), CancellationToken.None);
    Equal(0, quiet.SearchCount);
    using var plainBody = JsonDocument.Parse(plainBodies[0]);
    True(!plainBody.RootElement.TryGetProperty("tools", out _), "不开 web 时请求里不得带工具");
    await ThrowsAsync<ApolloInputException>(() => plain.CompleteAsync(
        profile,
        [new ChatMessage("user", "hi")],
        new ChatOptions { WebSearch = true },
        CancellationToken.None));
}

static async Task TestWebSearchBudgetAsync()
{
    var rounds = 0;
    using var client = FakeTransport.Client((_, _) =>
    {
        rounds++;
        return (HttpStatusCode.OK, $$$"""
            {"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":"",
              "tool_calls":[{"id":"call_{{{rounds}}}","type":"function","function":{"name":"web_search","arguments":"{\"query\":\"第 {{{rounds}}} 次\"}"}}]}}]}
            """);
    });

    var search = new FakeSearch(_ => []);
    var profile = new ProviderProfile("deepseek", "https://api.deepseek.com", "deepseek-chat", "sk-test", "store");
    var failure = await ThrowsAsync<ApolloRemoteException>(() => client.CompleteAsync(
        profile,
        [new ChatMessage("user", "一直搜")],
        new ChatOptions { WebSearch = true },
        CancellationToken.None,
        search));

    Equal(ChatClient.MaxSearches, search.Queries.Count);
    True(rounds > ChatClient.MaxSearches, "额度用完后应先告诉模型直接作答，再判卡住");
    True(failure.Message.Contains("始终没有作答", StringComparison.Ordinal), "卡住时失败回执应说明原因");
}

static async Task TestChatTraceAsync()
{
    var rounds = 0;
    using var client = FakeTransport.Client((_, _) => ++rounds == 1
        ? (HttpStatusCode.OK, """
            {"model":"deepseek-flash","choices":[{"finish_reason":"tool_calls","message":{
              "role":"assistant","content":"","reasoning_content":"先搜型号",
              "tool_calls":[{"id":"call_1","type":"function","function":{"name":"web_search","arguments":"{\"query\":\"CDQ2B20-10D 品牌\"}"}}]}}],
             "usage":{"prompt_tokens":100,"completion_tokens":10,"total_tokens":110}}
            """)
        : (HttpStatusCode.OK, """
            {"model":"deepseek-flash","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"{\"brand\":\"SMC\"}","reasoning_content":"官网写明是 SMC"}}],
             "usage":{"prompt_tokens":300,"completion_tokens":5,"total_tokens":305}}
            """));

    var search = new FakeSearch(_ =>
        [new WebSearchHit("SMC 薄型气缸 CDQ2B20-10D", "https://example.test/smc", "SMC", null, "摘要不进控制台")]);
    var profile = new ProviderProfile("deepseek", "https://api.deepseek.com", "deepseek-chat", "sk-test", "store");
    var trace = new ListProgress();
    await client.CompleteAsync(
        profile,
        [new ChatMessage("system", "你是采购助手"), new ChatMessage("user", "CDQ2B20-10D\n是什么品牌")],
        new ChatOptions { WebSearch = true, JsonOutput = true },
        CancellationToken.None,
        search,
        trace);

    // 一轮一段，一回来就报：提问 → 第 1 轮（思考 + 要搜的词）→ 搜索结果 → 第 2 轮（思考 + 答复 + 用量）。
    Equal(4, trace.Items.Count);
    var tag = trace.Items[0].Split(' ')[0];
    True(tag.Length > 1 && tag[0] == '#', "每段开头必须是调用编号：" + trace.Items[0]);
    True(trace.Items.All(item => item.StartsWith(tag + " ", StringComparison.Ordinal)), "同一次调用的每段都必须带同一个编号");

    True(trace.Items[0].Contains("deepseek/deepseek-chat 联网 提问：CDQ2B20-10D 是什么品牌", StringComparison.Ordinal), "提问复述最后一条消息并压成一行：" + trace.Items[0]);
    True(!trace.Items[0].Contains("你是采购助手", StringComparison.Ordinal), "系统提示不复述");
    True(trace.Items[1].Contains("第 1 轮 思考：先搜型号", StringComparison.Ordinal), "工具轮必须报思考：" + trace.Items[1]);
    True(trace.Items[1].Contains("第 1 轮 要求搜索：「CDQ2B20-10D 品牌」", StringComparison.Ordinal), "工具轮必须报要搜的词：" + trace.Items[1]);
    True(trace.Items[2].Contains("搜索「CDQ2B20-10D 品牌」1 条", StringComparison.Ordinal), "搜索必须报结果条数：" + trace.Items[2]);
    True(trace.Items[2].Contains("[1] SMC 薄型气缸 CDQ2B20-10D（SMC）", StringComparison.Ordinal), "搜索必须列出标题与来源：" + trace.Items[2]);
    True(!trace.Items[2].Contains("摘要不进控制台", StringComparison.Ordinal), "摘要只交给模型，不进控制台");
    True(trace.Items[3].Contains("第 2 轮 思考：官网写明是 SMC", StringComparison.Ordinal), "作答轮必须报思考：" + trace.Items[3]);
    True(trace.Items[3].Contains("第 2 轮 答复：{\"brand\":\"SMC\"}", StringComparison.Ordinal), "作答轮必须报答复原文：" + trace.Items[3]);
    True(trace.Items[3].Contains("完成：deepseek/deepseek-flash · 联网搜索 1 次 · 用量 400+15=415 tokens", StringComparison.Ordinal), "最后必须报用量：" + trace.Items[3]);
    True(!trace.Items[3].Contains("结束原因", StringComparison.Ordinal), "结束原因是 stop 时不追加");

    // 失败也要报出来，与失败回执同文；截断的答复要报结束原因。
    var failing = new ListProgress();
    using var rejected = FakeTransport.Client((_, _) => (HttpStatusCode.Unauthorized, """{"error": {"message": "Authentication Fails"}}"""));
    var failure = await ThrowsAsync<ApolloRemoteException>(() => rejected.CompleteAsync(
        profile, [new ChatMessage("user", "hi")], new ChatOptions(), CancellationToken.None, null, failing));
    Equal(2, failing.Items.Count);
    True(failing.Items[1].EndsWith("失败：" + failure.Message, StringComparison.Ordinal), "失败段必须与失败回执同文：" + failing.Items[1]);
    True(failing.Items[0].Split(' ')[0] != tag, "每次调用编号不同");

    var truncated = new ListProgress();
    using var cut = FakeTransport.Client((_, _) => (HttpStatusCode.OK, """{"choices":[{"finish_reason":"length","message":{"content":"{\"bra"}}]}"""));
    await cut.CompleteAsync(profile, [new ChatMessage("user", "hi")], new ChatOptions(), CancellationToken.None, null, truncated);
    True(truncated.Items[^1].EndsWith("· 结束原因 length", StringComparison.Ordinal), "被截断时必须报结束原因：" + truncated.Items[^1]);
    True(!truncated.Items[^1].Contains("第 1 轮", StringComparison.Ordinal), "不联网只有一轮，不标轮次");

    // 指令接线：apollo.chat.ask 把总线给的进度通道交给这次调用。
    using var scope = new StoreScope();
    scope.Store.SetKey(null, "sk-0123456789abcdef");
    using var plain = FakeTransport.Client((_, _) => (HttpStatusCode.OK, """{"model":"deepseek-chat","choices":[{"finish_reason":"stop","message":{"content":"好"}}]}"""));
    var registry = new CommandRegistry();
    ApolloCommandCatalog.Register(registry, scope.Store, plain, new FakeSearch(_ => []));
    True(registry.TryGet("apollo.chat.ask", out var ask), "缺少 apollo.chat.ask");
    var progress = new ListProgress();
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["prompt"] = "你好" };
    var result = await ask!.Handler(new CommandContext(ask, values, "test", progress, CancellationToken.None));
    True(result.Success, "指令应成功：" + result.Message);
    Equal(2, progress.Items.Count);
    True(progress.Items[1].Contains("答复：好", StringComparison.Ordinal), "指令必须把过程写进总线的进度通道：" + progress.Items[1]);
}

/// <summary>用本机真实配置做一次联网实调；只在 APOLLO_LIVE_WEB=1 时运行。</summary>
static async Task TestLiveWebCallAsync()
{
    var store = ApolloRuntime.Providers;
    var profile = store.ResolveChat(null);
    True(profile.HasKey, $"{profile.Name} 未配置密钥，无法做联网实调");
    True(store.Resolve(ProviderStore.SearchProvider).HasKey, "bocha 未配置密钥：先 apollo.key.set provider=bocha token=<密钥>");

    using var client = new ChatClient();
    using var search = new WebSearchClient(store);
    var outcome = await client.CompleteAsync(
        profile,
        ChatTranscript.FromPrompt("回答前必须先用 web_search 联网确认。只回答品牌名。", "气缸型号 CDQ2B20-10D 是哪个品牌的产品？"),
        new ChatOptions { WebSearch = true, MaxTokens = 200, TimeoutSeconds = 90 },
        CancellationToken.None,
        search);

    True(outcome.SearchCount > 0, "联网实调应至少发起一次搜索");
    True(!string.IsNullOrWhiteSpace(outcome.Content), "联网实调应返回非空正文");
    Console.WriteLine($"     实调 {outcome.Provider}/{outcome.Model} 搜索 {outcome.SearchCount} 次 → {outcome.Content.Trim()}"
                      + $"（{outcome.TotalTokens} tokens，{outcome.ElapsedMilliseconds}ms）");
}

// ------------------------------------------------------------------ 测试脚手架

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"期望 {expected}，实际 {actual}");
}

static void True(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static T Throws<T>(Action action)
    where T : Exception
{
    try
    {
        action();
    }
    catch (T expected)
    {
        return expected;
    }

    throw new InvalidOperationException($"期望抛出 {typeof(T).Name}，实际没有抛出");
}

static async Task<T> ThrowsAsync<T>(Func<Task> action)
    where T : Exception
{
    try
    {
        await action();
    }
    catch (T expected)
    {
        return expected;
    }

    throw new InvalidOperationException($"期望抛出 {typeof(T).Name}，实际没有抛出");
}

/// <summary>把 ProviderStore 指到一次性目录，测试不碰本机 AppData 里的真实密钥。</summary>
file sealed class StoreScope : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "HistoryApollo.Tests", Guid.NewGuid().ToString("N"));

    public StoreScope() => Store = new ProviderStore(Path.Combine(_root, "providers.json"));

    public ProviderStore Store { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>不出网的假传输：断言请求、给定响应。</summary>
file sealed class FakeTransport(Func<HttpRequestMessage, string?, (HttpStatusCode Status, string Body)> respond)
    : HttpMessageHandler
{
    public static ChatClient Client(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> respond)
        => new(Http(respond));

    public static HttpClient Http(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> respond)
        => new(new FakeTransport(respond));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content == null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        var (status, text) = respond(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(text) };
    }
}

/// <summary>
/// 同步收集过程输出。<see cref="Progress{T}"/> 会把回调投递到线程池，测试里拿不到确定的先后。
/// </summary>
file sealed class ListProgress : IProgress<string>
{
    public List<string> Items { get; } = [];

    public void Report(string value)
    {
        lock (Items)
            Items.Add(value);
    }
}

/// <summary>不出网的假搜索：记下模型搜了什么，给定结果。</summary>
file sealed class FakeSearch(Func<string, IReadOnlyList<WebSearchHit>> respond) : IWebSearch
{
    public List<string> Queries { get; } = [];

    public Task<IReadOnlyList<WebSearchHit>> SearchAsync(
        string query,
        int count,
        int timeoutSeconds,
        CancellationToken cancellation)
    {
        Queries.Add(query);
        return Task.FromResult(respond(query));
    }
}
