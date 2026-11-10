using System.Net;
using System.Text.Json;
using Ratio;
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
    ("vision provider", TestVisionProviderAsync),
    ("chat images", TestChatImagesAsync),
    ("local provider keyless", TestLocalProviderAsync),
    ("local commands", TestLocalCommandsAsync),
    ("adopt previous store", TestAdoptPreviousStoreAsync),
    ("service bundle", TestServiceBundleAsync),
    ("local probs", TestLocalProbsAsync),
    ("seq choose fallback", TestSeqChooseFallbackAsync),
    ("seq batch and append", TestSeqBatchAsync),
    ("seq stream", TestSeqStreamAsync),
    ("seq stream by steps", TestSeqStreamStepsAsync),
    ("chat stream", TestChatStreamAsync),
    ("inference commands", TestInferenceCommandsAsync),
    ("chat schema and think", TestChatSchemaAsync),
    ("embed texts", TestEmbedTextsAsync),
    ("native lifecycle and batch configuration", NativeTests.Lifecycle),
    ("native unload during model loading", NativeTests.UnloadDuringLoad),
    ("native rollback and snapshot failure", NativeTests.Rollback),
    ("native routing and lease drain", NativeTests.RoutingAndDrain),
    ("native stream cancellation", NativeTests.StreamAndCancellation),
    ("native automatic startup concurrency and cooldown", NativeTests.AutomaticStartup),
};

// 联网实调默认不跑：它要花钱、要外网，且依赖本机已配置的真实密钥。
// 交付验收时显式打开 RATIO_LIVE=1，让「能不能真的调通」这件事也有可重复的证据。
if (string.Equals(Environment.GetEnvironmentVariable("RATIO_LIVE"), "1", StringComparison.Ordinal))
    tests = [.. tests, ("live deepseek call", TestLiveCallAsync)];

// 识图实调另开一个开关：要通义千问（识图供应商）的密钥。
if (string.Equals(Environment.GetEnvironmentVariable("RATIO_LIVE_VISION"), "1", StringComparison.Ordinal))
    tests = [.. tests, ("live vision call", TestLiveVisionCallAsync)];

// 联网搜索实调另开一个开关：它还要博查密钥，不该让只配了 DeepSeek 的机器 VERIFY-LIVE 变红。
if (string.Equals(Environment.GetEnvironmentVariable("RATIO_LIVE_WEB"), "1", StringComparison.Ordinal))
    tests = [.. tests, ("live web search call", TestLiveWebCallAsync)];

var failed = 0;
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RATIO_NATIVE_LIVE_DLL")))
    tests = Environment.GetEnvironmentVariable("RATIO_NATIVE_ONLY") == "1"
        ? [("native live DLL", NativeTests.Live)] : [.. tests, ("native live DLL", NativeTests.Live)];
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RATIO_NATIVE_BENCH")))
    tests = [("native same-model benchmark", NativeBenchmark.Run)];
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
    Throws<RatioInputException>(() => scope.Store.Resolve("nowhere"));
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
    Throws<RatioInputException>(() => reopened.SetKey(null, "  "));
    Throws<RatioInputException>(() => reopened.SetKey(null, "sk-with space"));
    Throws<RatioInputException>(() => reopened.SetBaseUrl(null, "ftp://example.com"));
    Throws<RatioInputException>(() => reopened.SetBaseUrl("local", "http://localhost:9999/v1"));
    return Task.CompletedTask;
}

static Task TestDefineProviderAsync()
{
    using var scope = new StoreScope();
    True(!scope.Store.Knows("moonshot"), "库里本不该认识 moonshot");
    Throws<RatioInputException>(() => scope.Store.SetModel("moonshot", "kimi"));

    var defined = scope.Store.Define("MoonShot", "https://api.moonshot.cn/v1/", "kimi");
    Equal("moonshot", defined.Name);
    Equal("https://api.moonshot.cn/v1", defined.BaseUrl);
    Equal("kimi", defined.Model);
    True(scope.Store.Knows("moonshot"), "登记后应认识 moonshot");

    // 内置的四家（deepseek、识图 qwen、本机 local 与搜索服务 bocha）仍在，登记新供应商不会顶掉它们。
    Equal("https://api.deepseek.com", scope.Store.Resolve("deepseek").BaseUrl);
    Equal(5, scope.Store.All().Count);

    Throws<RatioInputException>(() => scope.Store.Define("bad name", "https://x.test", null));
    Throws<RatioInputException>(() => scope.Store.Define("ok", "not-a-url", null));
    return Task.CompletedTask;
}

static Task TestEnvironmentKeyAsync()
{
    using var scope = new StoreScope();
    scope.Store.SetKey(null, "sk-from-store-000");
    Environment.SetEnvironmentVariable("RATIO_DEEPSEEK_KEY", "sk-from-env-1111");
    try
    {
        var profile = new ProviderStore(scope.Store.Path).Resolve(null);
        Equal("sk-from-env-1111", profile.ApiKey!);
        Equal("env:RATIO_DEEPSEEK_KEY", profile.KeySource);
    }
    finally
    {
        Environment.SetEnvironmentVariable("RATIO_DEEPSEEK_KEY", null);
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

    Throws<RatioInputException>(() => ChatTranscript.FromPrompt(null, "  "));
    return Task.CompletedTask;
}

static Task TestTranscriptParseAsync()
{
    var messages = ChatTranscript.Parse(
        """[{"role":"system","content":"a"},{"role":"user","content":"b"}]""");
    Equal(2, messages.Count);
    Equal("system", messages[0].Role);
    Equal("b", messages[1].Content);

    Throws<RatioInputException>(() => ChatTranscript.Parse("{}"));
    Throws<RatioInputException>(() => ChatTranscript.Parse("[]"));
    Throws<RatioInputException>(() => ChatTranscript.Parse("""[{"role":"root","content":"x"}]"""));
    Throws<RatioInputException>(() => ChatTranscript.Parse("""[{"role":"user"}]"""));
    Throws<RatioInputException>(() => ChatTranscript.Parse("not json"));
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
    var failure = await ThrowsAsync<RatioRemoteException>(() => client.CompleteAsync(
        profile,
        [new ChatMessage("user", "hi")],
        new ChatOptions(),
        CancellationToken.None));

    True(failure.Message.Contains("401", StringComparison.Ordinal), "失败回执应带状态码");
    True(failure.Message.Contains("Authentication Fails", StringComparison.Ordinal), "失败回执应带服务端原因");

    using var garbage = FakeTransport.Client((_, _) => (HttpStatusCode.OK, "<html>gateway</html>"));
    await ThrowsAsync<RatioRemoteException>(() => garbage.CompleteAsync(
        profile,
        [new ChatMessage("user", "hi")],
        new ChatOptions(),
        CancellationToken.None));
}

static async Task TestMissingKeyAsync()
{
    using var client = FakeTransport.Client((_, _) => throw new InvalidOperationException("不该发出请求"));
    var profile = new ProviderProfile("deepseek", "https://api.deepseek.com", "deepseek-chat", null, "none");

    var failure = await ThrowsAsync<RatioInputException>(() => client.CompleteAsync(
        profile,
        [new ChatMessage("user", "hi")],
        new ChatOptions(),
        CancellationToken.None));
    True(failure.Message.Contains("ratio.key.set", StringComparison.Ordinal), "应指出配置密钥的办法");

    await ThrowsAsync<RatioInputException>(() => client.CompleteAsync(
        profile with { ApiKey = "sk-test" },
        [],
        new ChatOptions(),
        CancellationToken.None));

    await ThrowsAsync<RatioInputException>(() => client.CompleteAsync(
        profile with { ApiKey = "sk-test" },
        [new ChatMessage("user", "hi")],
        new ChatOptions { MaxTokens = 0 },
        CancellationToken.None));

    // 没有默认模型时问服务要；服务报出不止一个就猜不得，仍要调用方指定。
    using var several = FakeTransport.Client((request, _) =>
    {
        Equal("https://api.deepseek.com/models", request.RequestUri!.ToString());
        return (HttpStatusCode.OK, """{"data":[{"id":"a"},{"id":"b"}]}""");
    });
    await ThrowsAsync<RatioInputException>(() => several.CompleteAsync(
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

// 0.9.0：向量。分批（每批 10 条）、按 index 拼回输入顺序、dimensions 原样下发；指令默认走 qwen，Data 是 JSON 文本。
static async Task TestEmbedTextsAsync()
{
    using var scope = new StoreScope();
    scope.Store.SetKey("qwen", "sk-test");
    var batches = new List<int>();
    using var client = FakeTransport.Client((request, body) =>
    {
        Equal("https://dashscope.aliyuncs.com/compatible-mode/v1/embeddings", request.RequestUri!.ToString());
        using var sent = JsonDocument.Parse(body!);
        Equal("text-embedding-v4", sent.RootElement.GetProperty("model").GetString());
        Equal(4, sent.RootElement.GetProperty("dimensions").GetInt32());
        var inputs = sent.RootElement.GetProperty("input").EnumerateArray().Select(item => item.GetString()!).ToList();
        batches.Add(inputs.Count);
        // 故意倒序回，验证按 index 拼回。
        var rows = inputs.Select((text, index) => $"{{\"index\":{index},\"embedding\":[{int.Parse(text[1..])},0,0,1]}}").Reverse();
        return (HttpStatusCode.OK, $"{{\"model\":\"text-embedding-v4\",\"data\":[{string.Join(",", rows)}],\"usage\":{{\"total_tokens\":{inputs.Count}}}}}");
    });

    var registry = new Registrar();
    RatioEmbedCommands.Register(registry, scope.Store, client);
    True(registry.TryGet("ratio.embed.texts", out var embed), "缺少 ratio.embed.texts");
    True(!embed!.Readonly, "向量是计费请求，不应声明为只读");

    var texts = JsonSerializer.Serialize(Enumerable.Range(0, 12).Select(index => $"t{index}"));
    var result = await embed.Handler(new CommandContext(embed,
        new Dictionary<string, string> { ["texts"] = texts, ["dimensions"] = "4" }, "test", null, CancellationToken.None));
    True(result.Success, result.Message);
    True(batches.SequenceEqual([10, 2]), $"每批最多 10 条，实际 {string.Join(",", batches)}");
    using var data = JsonDocument.Parse((string)result.Data!);
    var vectors = data.RootElement.GetProperty("vectors").EnumerateArray().ToList();
    Equal(12, vectors.Count);
    Equal(11f, vectors[11][0].GetSingle());
    Equal(4, data.RootElement.GetProperty("dimensions").GetInt32());
    Equal(12, data.RootElement.GetProperty("usage").GetProperty("totalTokens").GetInt32());
    True(!result.Message.Contains('[', StringComparison.Ordinal), "正文不放向量");

    var bad = await embed.Handler(new CommandContext(embed,
        new Dictionary<string, string> { ["texts"] = "[\"a\",\"\"]" }, "test", null, CancellationToken.None));
    True(!bad.Success && bad.Message.Contains("第 2 条", StringComparison.Ordinal), $"空文本必须拒绝: {bad.Message}");
    var noModel = await embed.Handler(new CommandContext(embed,
        new Dictionary<string, string> { ["texts"] = "[\"a\"]", ["provider"] = "deepseek" }, "test", null, CancellationToken.None));
    True(!noModel.Success && noModel.Message.Contains("model=", StringComparison.Ordinal), $"没有内置向量模型必须要求 model=: {noModel.Message}");
}

static Task TestCommandRegistrationAsync()
{
    using var scope = new StoreScope();
    using var client = FakeTransport.Client((_, _) => (HttpStatusCode.OK, "{}"));
    var registry = new Registrar();

    // 注册表在这里会同时校验 Ask 与 ConfirmPrompt 是否配套、命令类是否合法。
    RatioCommandCatalog.Register(registry, scope.Store, client, new FakeSearch(_ => []));

    string[] expected =
    [
        "ratio.chat.ask",
        "ratio.chat.send",
        "ratio.model.list",
        "ratio.provider.list",
        "ratio.provider.use",
        "ratio.provider.config",
        "ratio.key.set",
        "ratio.key.clear",
    ];
    foreach (var name in expected)
    {
        True(registry.TryGet(name, out var descriptor), $"未注册 {name}");
        Equal("ratio", descriptor!.Domain!);
    }

    Equal(
        expected.Length,
        registry.All.Count(descriptor => descriptor.Name.StartsWith("ratio.", StringComparison.Ordinal)));

    True(registry.TryGet("ratio.key.clear", out var clear), "缺少 ratio.key.clear");
    Equal(CommandLevel.Ask, clear!.Level);
    True(clear.ConfirmPrompt != null, "删除密钥必须要人确认");

    True(registry.TryGet("ratio.model.list", out var models), "缺少 ratio.model.list");
    True(models!.Readonly, "列模型是只读的");

    True(registry.TryGet("ratio.chat.ask", out var ask), "缺少 ratio.chat.ask");
    True(!ask!.Readonly, "对话会发出计费请求，不应声明为只读");

    foreach (var name in new[] { "ratio.chat.ask", "ratio.chat.send" })
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
    var registry = new Registrar();
    RatioCommandCatalog.Register(registry, scope.Store, client, new FakeSearch(_ => []));

    // 总线按参数名判定敏感值：名字必须以 token 结尾，位置 0 才会被位置传参的遮蔽覆盖。
    // 改名成 apikey / key 会让回显、命令历史和结果脱敏整体失效，所以在这里钉住。
    True(registry.TryGet("ratio.key.set", out var descriptor), "缺少 ratio.key.set");
    var secret = descriptor!.Parameters.Single(parameter => parameter.Required);
    Equal("token", secret.Name);
    Equal(0, secret.Position!.Value);
    return Task.CompletedTask;
}

/// <summary>
/// 实调用本机真实配置：模块装在宿主里时的数据目录。宿主 6.0.0 起由宿主给，测试进程里没有宿主，
/// 所以按 RATIO_DATA 指定，缺省取宿主的模块数据根（只在 RATIO_LIVE* 实调时用到）。
/// </summary>
static string LiveDataDirectory()
    => Environment.GetEnvironmentVariable("RATIO_DATA") is { Length: > 0 } path
        ? path
        : Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "HistoryVulcan", "ModuleData", "Ratio");

/// <summary>用本机真实配置向 DeepSeek 发一次最小请求；只在 RATIO_LIVE=1 时运行。</summary>
static async Task TestLiveCallAsync()
{
    RatioRuntime.Use(LiveDataDirectory());
    var profile = RatioRuntime.Providers.Resolve(null);
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
    var misuse = Throws<RatioInputException>(() => scope.Store.ResolveChat("bocha"));
    True(misuse.Message.Contains("web=true", StringComparison.Ordinal), "拒绝时应指出联网的正确用法");
    Throws<RatioInputException>(() => scope.Store.SetDefaultProvider("bocha"));
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
    if (Environment.GetEnvironmentVariable("RATIO_BOCHA_KEY") is null
        && Environment.GetEnvironmentVariable("BOCHA_API_KEY") is null)
    {
        var missing = await ThrowsAsync<RatioInputException>(
            () => search.SearchAsync("CDQ2B20-10D", 5, 30, CancellationToken.None));
        True(missing.Message.Contains("ratio.key.set provider=bocha", StringComparison.Ordinal), "应指出配置博查密钥的办法");
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
    var rejected = Throws<RatioRemoteException>(
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
    await ThrowsAsync<RatioInputException>(() => plain.CompleteAsync(
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
    var failure = await ThrowsAsync<RatioRemoteException>(() => client.CompleteAsync(
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
    var failure = await ThrowsAsync<RatioRemoteException>(() => rejected.CompleteAsync(
        profile, [new ChatMessage("user", "hi")], new ChatOptions(), CancellationToken.None, null, failing));
    Equal(2, failing.Items.Count);
    True(failing.Items[1].EndsWith("失败：" + failure.Message, StringComparison.Ordinal), "失败段必须与失败回执同文：" + failing.Items[1]);
    True(failing.Items[0].Split(' ')[0] != tag, "每次调用编号不同");

    var truncated = new ListProgress();
    using var cut = FakeTransport.Client((_, _) => (HttpStatusCode.OK, """{"choices":[{"finish_reason":"length","message":{"content":"{\"bra"}}]}"""));
    await cut.CompleteAsync(profile, [new ChatMessage("user", "hi")], new ChatOptions(), CancellationToken.None, null, truncated);
    True(truncated.Items[^1].EndsWith("· 结束原因 length", StringComparison.Ordinal), "被截断时必须报结束原因：" + truncated.Items[^1]);
    True(!truncated.Items[^1].Contains("第 1 轮", StringComparison.Ordinal), "不联网只有一轮，不标轮次");

    // 指令接线：ratio.chat.ask 把总线给的进度通道交给这次调用。
    using var scope = new StoreScope();
    scope.Store.SetKey(null, "sk-0123456789abcdef");
    using var plain = FakeTransport.Client((_, _) => (HttpStatusCode.OK, """{"model":"deepseek-chat","choices":[{"finish_reason":"stop","message":{"content":"好"}}]}"""));
    var registry = new Registrar();
    RatioCommandCatalog.Register(registry, scope.Store, plain, new FakeSearch(_ => []));
    True(registry.TryGet("ratio.chat.ask", out var ask), "缺少 ratio.chat.ask");
    var progress = new ListProgress();
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["prompt"] = "你好" };
    var result = await ask!.Handler(new CommandContext(ask, values, "test", progress, CancellationToken.None));
    True(result.Success, "指令应成功：" + result.Message);
    Equal(2, progress.Items.Count);
    True(progress.Items[1].Contains("答复：好", StringComparison.Ordinal), "指令必须把过程写进总线的进度通道：" + progress.Items[1]);
}

/// <summary>用本机真实配置做一次联网实调；只在 RATIO_LIVE_WEB=1 时运行。</summary>
static async Task TestLiveWebCallAsync()
{
    RatioRuntime.Use(LiveDataDirectory());
    var store = RatioRuntime.Providers;
    var profile = store.ResolveChat(null);
    True(profile.HasKey, $"{profile.Name} 未配置密钥，无法做联网实调");
    True(store.Resolve(ProviderStore.SearchProvider).HasKey, "bocha 未配置密钥：先 ratio.key.set provider=bocha token=<密钥>");

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

static Task TestVisionProviderAsync()
{
    using var scope = new StoreScope();
    // 没设过就是内置的通义千问；默认供应商仍是 deepseek，两者互不影响。
    Equal(ProviderStore.BuiltinVisionProvider, scope.Store.VisionProvider);
    var qwen = scope.Store.ResolveVision(null);
    Equal("qwen", qwen.Name);
    Equal("https://dashscope.aliyuncs.com/compatible-mode/v1", qwen.BaseUrl);
    Equal("qwen-vl-max", qwen.Model);
    Equal("deepseek", scope.Store.ResolveChat(null).Name);

    // 写了名字就听调用方的；搜索服务不能当识图供应商。
    Equal("deepseek", scope.Store.ResolveVision("deepseek").Name);
    Throws<RatioInputException>(() => scope.Store.SetVisionProvider("bocha"));
    Throws<RatioInputException>(() => scope.Store.SetVisionProvider("nowhere"));

    // 换识图供应商落盘，不碰默认供应商；0.4 的旧库（没有 vision 项）读出来是内置值。
    scope.Store.Define("doubao", "https://ark.cn-beijing.volces.com/api/v3", "ep-test");
    scope.Store.SetVisionProvider("doubao");
    var reopened = new ProviderStore(scope.Store.Path);
    Equal("doubao", reopened.VisionProvider);
    Equal("deepseek", reopened.DefaultProvider);
    File.WriteAllText(scope.Store.Path, """{"default":"deepseek","providers":{}}""");
    Equal("qwen", new ProviderStore(scope.Store.Path).VisionProvider);
    return Task.CompletedTask;
}

static async Task TestChatImagesAsync()
{
    using var scope = new StoreScope();
    var root = Path.GetDirectoryName(scope.Store.Path)!;
    Directory.CreateDirectory(root);
    var png = Path.Combine(root, "图纸 1.png");
    File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47]);

    // 读成 data URL；空参数没有图；相对路径、不认的格式、不存在、超量都拒。
    var images = ChatImages.Load($" \"{png}\" ");
    Equal(1, images.Count);
    Equal("data:image/png;base64,iVBORw==", images[0]);
    Equal(0, ChatImages.Load(null).Count);
    Throws<RatioInputException>(() => ChatImages.Load("图纸.png"));
    Throws<RatioInputException>(() => ChatImages.Load(Path.Combine(root, "a.tif")));
    Throws<RatioInputException>(() => ChatImages.Load(Path.Combine(root, "missing.png")));
    Throws<RatioInputException>(() => ChatImages.Load(string.Join("|", Enumerable.Repeat(png, ChatImages.MaxCount + 1))));

    // 挂在最后一条 user 消息上；没有 user 消息就拒。
    var attached = ChatImages.Attach([new ChatMessage("user", "a"), new ChatMessage("assistant", "b"), new ChatMessage("user", "c")], images);
    Equal(0, attached[0].ImageCount);
    Equal(1, attached[2].ImageCount);
    Throws<RatioInputException>(() => ChatImages.Attach([new ChatMessage("system", "x")], images));

    // 指令接线：带图且没写 provider → 走识图供应商的接入点，content 是「文字 + image_url」数组；控制台报附图张数。
    scope.Store.SetKey("qwen", "sk-qwen-test");
    string? url = null, body = null;
    using var client = FakeTransport.Client((request, sent) =>
    {
        (url, body) = (request.RequestUri!.ToString(), sent);
        return (HttpStatusCode.OK, """{"model":"qwen-vl-max","choices":[{"finish_reason":"stop","message":{"content":"一块板"}}]}""");
    });
    var registry = new Registrar();
    RatioCommandCatalog.Register(registry, scope.Store, client, new FakeSearch(_ => []));
    True(registry.TryGet("ratio.chat.ask", out var ask), "缺少 ratio.chat.ask");
    True(ask!.Parameters.Any(parameter => parameter.Name == "images"), "ratio.chat.ask 必须有 images 参数");
    var progress = new ListProgress();
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["prompt"] = "这是什么", ["images"] = png };
    var result = await ask.Handler(new CommandContext(ask, values, "test", progress, CancellationToken.None));
    True(result.Success, "带图调用应成功：" + result.Message);
    Equal("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", url!);
    using (var sent = JsonDocument.Parse(body!))
    {
        var content = sent.RootElement.GetProperty("messages")[0].GetProperty("content");
        Equal(JsonValueKind.Array, content.ValueKind);
        Equal("这是什么", content[0].GetProperty("text").GetString()!);
        Equal("image_url", content[1].GetProperty("type").GetString()!);
        Equal(images[0], content[1].GetProperty("image_url").GetProperty("url").GetString()!);
    }

    True(progress.Items[0].Contains("qwen/qwen-vl-max 附图 1 张 提问：这是什么", StringComparison.Ordinal), "控制台必须报附图张数：" + progress.Items[0]);
    True(!progress.Items[0].Contains("base64", StringComparison.Ordinal), "图片内容不进控制台");

    // 不带图：content 仍是字符串，走默认供应商（0.4 行为不变）。
    scope.Store.SetKey(null, "sk-0123456789abcdef");
    values.Remove("images");
    result = await ask.Handler(new CommandContext(ask, values, "test", new ListProgress(), CancellationToken.None));
    True(result.Success, "不带图调用应成功：" + result.Message);
    Equal("https://api.deepseek.com/chat/completions", url!);
    using (var sent = JsonDocument.Parse(body!))
        Equal(JsonValueKind.String, sent.RootElement.GetProperty("messages")[0].GetProperty("content").ValueKind);
}

/// <summary>用本机真实配置让识图供应商看一张图；只在 RATIO_LIVE_VISION=1 时运行，图片取 RATIO_LIVE_IMAGE。</summary>
static async Task TestLiveVisionCallAsync()
{
    RatioRuntime.Use(LiveDataDirectory());
    var profile = RatioRuntime.Providers.ResolveVision(null);
    True(profile.HasKey, $"{profile.Name} 未配置密钥：先 ratio.key.set provider={profile.Name} token=<密钥>");
    var image = Environment.GetEnvironmentVariable("RATIO_LIVE_IMAGE");
    True(!string.IsNullOrWhiteSpace(image), "RATIO_LIVE_IMAGE 要给一张本机图片的绝对路径");

    using var client = new ChatClient();
    var outcome = await client.CompleteAsync(
        profile,
        ChatImages.Attach(ChatTranscript.FromPrompt(null, "用一句话说这张图里是什么。"), ChatImages.Load(image)),
        new ChatOptions { MaxTokens = 200, TimeoutSeconds = 90 },
        CancellationToken.None);

    True(!string.IsNullOrWhiteSpace(outcome.Content), "识图实调应返回非空正文");
    Console.WriteLine($"     实调 {outcome.Provider}/{outcome.Model} → {outcome.Content.Trim()}"
                      + $"（{outcome.TotalTokens} tokens，{outcome.ElapsedMilliseconds}ms）");
}

// ------------------------------------------------------------------ 测试脚手架

static async Task TestLocalProviderAsync()
{
    using var scope = new StoreScope();
    var profile = scope.Store.ResolveChat("local");
    Equal("http://127.0.0.1:11435/v1", profile.BaseUrl);
    Equal(string.Empty, profile.Model);
    True(!profile.KeyRequired, "本机回环接入点不应要求密钥");
    True(scope.Store.Resolve("deepseek").KeyRequired, "远端供应商仍要密钥");
    True(!scope.Store.Define("ollama", "http://localhost:11434/v1", "qwen3:8b").KeyRequired, "localhost 也算本机");

    string? sent = null;
    var calls = new List<string>();
    using var client = FakeTransport.Client((request, body) =>
    {
        calls.Add($"{request.Method} {request.RequestUri}");
        True(request.Headers.Authorization is null, "没有密钥就不该带认证头");
        if (request.Method == HttpMethod.Get)
            return (HttpStatusCode.OK, """{"object":"list","data":[{"id":"qwen3:8b"}]}""");
        sent = body;
        return (HttpStatusCode.OK, """{"model":"qwen3:8b","choices":[{"message":{"content":"好"},"finish_reason":"stop"}],"usage":{"total_tokens":5}}""");
    });

    var outcome = await client.CompleteAsync(profile, [new ChatMessage("user", "hi")], new ChatOptions(), CancellationToken.None);
    Equal("好", outcome.Content);
    Equal("GET http://127.0.0.1:11435/v1/models", calls[0]);
    Equal("POST http://127.0.0.1:11435/v1/chat/completions", calls[1]);
    using var document = JsonDocument.Parse(sent!);
    Equal("qwen3:8b", document.RootElement.GetProperty("model").GetString()!);
}

static Task TestLocalCommandsAsync()
{
    using var scope = new StoreScope();
    using var local = new LocalService(scope.Store);
    var registry = new Registrar();
    RatioLocalCommands.Register(registry, local);
    foreach (var name in new[] { "ratio.local.status", "ratio.local.start", "ratio.local.stop", "ratio.local.use" })
    {
        True(registry.TryGet(name, out var descriptor), $"未注册 {name}");
        Equal("ratio", descriptor!.Domain!);
    }

    Equal(4, registry.All.Count);
    True(registry.TryGet("ratio.local.status", out var status) && status!.Readonly, "status 是只读");
    True(registry.TryGet("ratio.local.stop", out var stop) && stop!.Level == CommandLevel.Ask, "停服要确认");
    return Task.CompletedTask;
}

static Task TestServiceBundleAsync()
{
    // 真包：脚本都在、shell 脚本是 LF（CRLF 的 bash 脚本在 WSL 里直接报错）、文档不随包走。
    var files = ServiceBundle.Load();
    var names = files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
    foreach (var required in new[]
             {
                 "scripts/echo-kv/ratio-native.cpp", "scripts/echo-kv/echo-kv.h",
                 "scripts/echo-kv/build-native.ps1", "config/echo-models.json",
             })
        True(names.Contains(required), $"服务包缺 {required}");
    True(!names.Any(name => name.EndsWith(".md", StringComparison.Ordinal) || name.Contains("__pycache__", StringComparison.Ordinal)), "文档与缓存不进包");
    True(!names.Any(name => name.EndsWith(".sh", StringComparison.Ordinal) || name.Contains("proxy", StringComparison.OrdinalIgnoreCase)), "不再打包旧服务或代理实现");

    var root = Path.Combine(Path.GetTempPath(), "Ratio.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "status"));
        File.WriteAllText(Path.Combine(root, "status", "keep.json"), "{}");
        Equal(files.Count, ServiceBundle.Deploy(root, files));
        Equal(0, ServiceBundle.Deploy(root, files));
        File.WriteAllText(Path.Combine(root, "scripts", "echo-kv", "ratio-native.cpp"), "changed");
        Equal(1, ServiceBundle.Deploy(root, files));
        True(File.Exists(Path.Combine(root, "status", "keep.json")), "部署不删运行区自己的文件");
        Throws<RatioInputException>(() => ServiceBundle.Deploy(root, [new ServiceFile("../escape.txt", [1])]));
        Throws<RatioInputException>(() => ServiceBundle.Deploy(root, []));
        // Candidate builds contain large native resources; stream them and verify
        // byte identity without adding them to the in-memory source-file bundle.
        var assembly = typeof(ServiceBundle).Assembly;
        var resources = assembly.GetManifestResourceNames();
        True(!resources.Any(name => name.StartsWith("service/native/bin/cublas", StringComparison.OrdinalIgnoreCase)), "cuBLAS must stay outside the bounded module package");
        if (resources.Contains("service/native/bin/ggml-cuda.dll"))
        {
            Throws<RatioInputException>(() => ServiceBundle.DeployNative(root));
            foreach (var library in new[] { "cublas64_12.dll", "cublasLt64_12.dll" })
                File.WriteAllText(Path.Combine(root, "native", "bin", library), "machine-runtime");
        }
        ServiceBundle.DeployNative(root);
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("service/native/bin/", StringComparison.Ordinal)))
        {
            using var source = assembly.GetManifestResourceStream(resource)!;
            using var installed = File.OpenRead(Path.Combine(root, resource["service/".Length..]));
            True(System.Security.Cryptography.SHA256.HashData(source).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(installed)), "原生资源解出字节一致");
        }
        ServiceBundle.DeployNative(root); // Idempotent, does not rewrite matching DLLs.
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }

    return Task.CompletedTask;
}

static Task TestAdoptPreviousStoreAsync()
{
    var root = Path.Combine(Path.GetTempPath(), "Ratio.Tests", Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "HistoryApollo"));
        File.WriteAllText(Path.Combine(root, "HistoryApollo", "providers.json"), """{"default":"qwen","providers":{}}""");
        RatioRuntime.Use(Path.Combine(root, "Ratio"));
        Equal("qwen", RatioRuntime.Providers.DefaultProvider);
        True(File.Exists(Path.Combine(root, "HistoryApollo", "providers.json")), "旧库只复制不移动");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }

    return Task.CompletedTask;
}

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

static async Task TestLocalProbsAsync()
{
    using var local = new LocalInference(() => "http://127.0.0.1:11435/v1", FakeTransport.Http((request, body) =>
    {
        Equal("http://127.0.0.1:11435/completion", request.RequestUri!.ToString());
        var json = JsonDocument.Parse(body!).RootElement;
        Equal(1, json.GetProperty("n_predict").GetInt32());
        Equal(3, json.GetProperty("n_probs").GetInt32());
        return (HttpStatusCode.OK, """{"completion_probabilities":[{"top_logprobs":[{"token":" like","logprob":-0.1},{"token":" love","logprob":-2.5}]}]}""");
    }));
    var tokens = await local.ProbsAsync("I", 3, null, 10, CancellationToken.None);
    Equal(2, tokens.Count);
    Equal(" like", tokens[0].Token);
}

static async Task TestSeqChooseFallbackAsync()
{
    var ops = new List<string>();
    using var local = new LocalInference(() => "http://127.0.0.1:11435/v1", FakeTransport.Http((_, body) =>
    {
        var op = JsonDocument.Parse(body!).RootElement.GetProperty("op").GetString()!;
        ops.Add(op);
        return op switch
        {
            "create" => (HttpStatusCode.OK, """{"sequence_id":"7","cache_epoch":"e","prefix_tokens":12}"""),
            "select_text" => (HttpStatusCode.BadRequest, """{"error":{"message":"unknown_echo_operation"}}"""),
            "prefix" => (HttpStatusCode.OK, """{"prefix_tokens":12,"remainder":""}"""),
            "tokenize" => (HttpStatusCode.OK, """{"tokens":[1,2]}"""),
            "select" => (HttpStatusCode.OK, """{"results":[{"label":" A","decision_steps":1,"shared_prefix_tokens":12,"replayed_tokens":0}]}"""),
            "release" => (HttpStatusCode.OK, "{}"),
            _ => (HttpStatusCode.NotFound, "{}"),
        };
    }));
    var lease = await local.OpenAsync(null, "prompt", CancellationToken.None);
    Equal(1, local.OpenSequences);
    var row = await local.ChooseAsync(lease.SequenceId, lease.CacheEpoch, "prompt", "Label:", ["0", " A"], CancellationToken.None);
    Equal(" A", row["label"]!.GetValue<string>());
    await local.CloseAsync(lease.SequenceId, lease.CacheEpoch, CancellationToken.None);
    Equal(0, local.OpenSequences);
    Equal("create,select_text,prefix,tokenize,select,release", string.Join(",", ops));
    using var full = new LocalInference(() => "http://x/v1", FakeTransport.Http((_, _) =>
        (HttpStatusCode.Conflict, """{"error":{"message":"invalid_sequence_size"}}""")));
    var rejected = await ThrowsAsync<RatioKvException>(() => full.OpenAsync(null, "p", CancellationToken.None));
    Equal("invalid_sequence_size", rejected.Message);
}

static async Task TestSeqBatchAsync()
{
    // 新服务：整批一次 select_batch；省略 prefix 就不带 prefix_text；续接更新记账里的前缀长度。
    var bodies = new List<JsonElement>();
    using var local = new LocalInference(() => "http://127.0.0.1:11435/v1", FakeTransport.Http((_, body) =>
    {
        var json = JsonDocument.Parse(body!).RootElement.Clone();
        bodies.Add(json);
        return json.GetProperty("op").GetString() switch
        {
            "create" => (HttpStatusCode.OK, """{"sequence_id":"7","cache_epoch":"e","prefix_tokens":12}"""),
            "append" => (HttpStatusCode.OK, """{"sequence_id":"7","cache_epoch":"e","prefix_tokens":20,"appended_tokens":8}"""),
            "select_batch" => (HttpStatusCode.OK, """{"results":[{"label":" A","decision_steps":1},{"label":"0","decision_steps":1}],"groups":1,"elapsed_ms":3.5}"""),
            _ => (HttpStatusCode.OK, "{}"),
        };
    }));
    var lease = await local.OpenAsync(null, "prompt", CancellationToken.None);
    var grown = await local.AppendAsync(lease.SequenceId, lease.CacheEpoch, "more\n", CancellationToken.None);
    Equal(20, grown.PrefixTokens);
    Equal("more\n", bodies[^1].GetProperty("text").GetString());
    var (rows, serviceMs, batched) = await local.ChooseBatchAsync(lease.SequenceId, lease.CacheEpoch, null,
        [("x Label:", ["0", " A"]), ("y Label:", ["0", " A", " B"])], CancellationToken.None);
    True(batched, "新服务应走 select_batch");
    Equal(2, rows.Count);
    Equal("0", rows[1]!["label"]!.GetValue<string>());
    Equal(3.5, serviceMs);
    var sent = bodies[^1];
    True(!sent.TryGetProperty("prefix_text", out _), "省略 prefix 不应带 prefix_text");
    Equal(2, sent.GetProperty("jobs").GetArrayLength());
    Equal(" B", sent.GetProperty("jobs")[1].GetProperty("allowed_labels")[2].GetString());
    await local.ChooseBatchAsync(lease.SequenceId, lease.CacheEpoch, "prompt", [("x", ["0", " A"]), ("y", ["0"])], CancellationToken.None);
    Equal("prompt", bodies[^1].GetProperty("prefix_text").GetString());
    await ThrowsAsync<RatioInputException>(() => local.ChooseBatchAsync("7", "e", null, [], CancellationToken.None));

    // 服务回了任务没给的标签：整批作废。
    using var wrong = new LocalInference(() => "http://x/v1", FakeTransport.Http((_, _) =>
        (HttpStatusCode.OK, """{"results":[{"label":" Z"}]}""")));
    var invalid = await ThrowsAsync<RatioKvException>(() => wrong.ChooseBatchAsync("7", "e", null, [("x", ["0", " A"])], CancellationToken.None));
    Equal("invalid_shared_label", invalid.Message);

    // 旧服务没有 select_batch：退回逐个 select_text，结果同序。
    var ops = new List<string>();
    using var old = new LocalInference(() => "http://x/v1", FakeTransport.Http((_, body) =>
    {
        var json = JsonDocument.Parse(body!).RootElement;
        var op = json.GetProperty("op").GetString()!;
        ops.Add(op);
        return op switch
        {
            "select_batch" => (HttpStatusCode.BadRequest, """{"error":{"message":"unknown_echo_operation"}}"""),
            "select_text" => (HttpStatusCode.OK, json.GetProperty("suffix_text").GetString() == "x"
                ? """{"results":[{"label":" A","decision_steps":1}]}"""
                : """{"results":[{"label":"0","decision_steps":1}]}"""),
            _ => (HttpStatusCode.OK, "{}"),
        };
    }));
    var (fallback, _, viaBatch) = await old.ChooseBatchAsync("7", "e", "prompt", [("x", ["0", " A"]), ("y", ["0", " A"])], CancellationToken.None);
    True(!viaBatch, "旧服务应退回逐个选");
    Equal("select_batch,select_text,select_text", string.Join(",", ops));
    Equal(" A", fallback[0]!["label"]!.GetValue<string>());
    Equal("0", fallback[1]!["label"]!.GetValue<string>());
    var needPrefix = await ThrowsAsync<RatioKvException>(() => old.ChooseBatchAsync("7", "e", null, [("x", ["0"])], CancellationToken.None));
    Equal("select_batch_requires_prefix_on_old_service", needPrefix.Message);

    Equal(2, RatioInferenceCommands.ParseJobs("""[{"suffix":"a","labels":["0"," A"]},{"suffix":"b","labels":["0"]}]""").Count);
    Throws<RatioInputException>(() => RatioInferenceCommands.ParseJobs("[]"));
    Throws<RatioInputException>(() => RatioInferenceCommands.ParseJobs("""[{"suffix":"","labels":["0"]}]"""));
    Throws<RatioInputException>(() => RatioInferenceCommands.ParseJobs("""[{"suffix":"a","labels":[]}]"""));
    Throws<RatioInputException>(() => RatioInferenceCommands.ParseJobs("not json"));
}

static async Task<string> DrainAsync(LocalInference local, StreamSession session)
{
    var text = new System.Text.StringBuilder();
    var after = 0;
    while (true)
    {
        var chunk = await local.Streams.ReadAsync(session.Id, after, 2000, CancellationToken.None);
        text.Append(chunk.Text);
        after = chunk.Next;
        if (chunk.Done)
        {
            if (chunk.Error != null)
                throw new InvalidOperationException(chunk.Error);
            return text.ToString();
        }
    }
}

static async Task TestSeqStreamAsync()
{
    const string sse = "data: {\"delta\":\"Hel\"}\n\ndata: {\"delta\":\"lo\",\"done\":true}\n\ndata: [DONE]\n\n";
    using var local = new LocalInference(() => "http://127.0.0.1:11435/v1", FakeTransport.Http((_, _) => (HttpStatusCode.OK, sse)));
    Equal("Hello", await DrainAsync(local, local.StreamSequence("1", "e", 16)));
    Equal(0, local.Streams.Count);
}

static async Task TestSeqStreamStepsAsync()
{
    var step = 0;
    using var local = new LocalInference(() => "http://127.0.0.1:11435/v1", FakeTransport.Http((_, body) =>
    {
        var op = JsonDocument.Parse(body!).RootElement.GetProperty("op").GetString();
        if (op == "stream")
            return (HttpStatusCode.BadRequest, """{"error":{"message":"unknown_echo_operation"}}""");
        step++;
        return (HttpStatusCode.OK, step < 3 ? "{\"delta\":\"w" + step + " \"}" : """{"done":true}""");
    }));
    Equal("w1 w2 ", await DrainAsync(local, local.StreamSequence("1", "e", 16)));
}

static async Task TestChatStreamAsync()
{
    using var scope = new StoreScope();
    const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\" there\"}}]}\n\ndata: [DONE]\n\n";
    using var local = new LocalInference(() => "http://127.0.0.1:11435/v1", FakeTransport.Http((_, body) =>
    {
        True(body!.Contains("\"stream\":true"), "流式请求要带 stream=true");
        True(body.Contains("enable_thinking"), "think=false 要关思考");
        return (HttpStatusCode.OK, sse);
    }));
    var profile = scope.Store.Resolve("local");
    var body = ChatClient.BuildBody("m", new System.Text.Json.Nodes.JsonArray(), new ChatOptions { Think = false });
    Equal("Hi there", await DrainAsync(local, local.StreamChat(profile, "m", body)));
}

static Task TestInferenceCommandsAsync()
{
    using var scope = new StoreScope();
    using var client = FakeTransport.Client((_, _) => (HttpStatusCode.OK, "{}"));
    using var local = new LocalInference(() => "http://127.0.0.1:11435/v1", FakeTransport.Http((_, _) => (HttpStatusCode.OK, "{}")));
    var registry = new Registrar();
    RatioInferenceCommands.Register(registry, scope.Store, client, local);
    string[] expected = ["ratio.local.probs", "ratio.seq.caps", "ratio.seq.open", "ratio.seq.choose", "ratio.seq.batch", "ratio.seq.append", "ratio.seq.stream",
        "ratio.seq.close", "ratio.chat.stream", "ratio.stream.read", "ratio.stream.cancel"];
    foreach (var name in expected)
        True(registry.TryGet(name, out _), $"未注册 {name}");
    Equal(expected.Length, registry.All.Count);
    Equal(2, RatioInferenceCommands.ParseLabels("[\"0\",\" A\"]").Count);
    Throws<RatioInputException>(() => RatioInferenceCommands.ParseLabels("[]"));
    return Task.CompletedTask;
}

static Task TestChatSchemaAsync()
{
    var schema = RatioCommandCatalog.ParseSchema("""{"type":"object"}""");
    var body = ChatClient.BuildBody("m", new System.Text.Json.Nodes.JsonArray(), new ChatOptions { Schema = schema, Think = false });
    Equal("json_schema", body["response_format"]!["type"]!.GetValue<string>());
    True(body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>(), "schema 要 strict");
    Equal(false, body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
    var plain = ChatClient.BuildBody("m", new System.Text.Json.Nodes.JsonArray(), new ChatOptions());
    True(plain["chat_template_kwargs"] is null && plain["response_format"] is null, "缺省不写这两项");
    Throws<RatioInputException>(() => RatioCommandCatalog.ParseSchema("[1]"));
    return Task.CompletedTask;
}

/// <summary>把 ProviderStore 指到一次性目录，测试不碰本机 AppData 里的真实密钥。</summary>
file sealed class StoreScope : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Ratio.Tests", Guid.NewGuid().ToString("N"));

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

/// <summary>
/// 模块登记口的测试替身：只记下登记了哪些指令。宿主 6.0.0 起注册表是宿主内部类，
/// 登记口 ICommandRegistrar 就是模块能看到的全部；来源由宿主盖章，这里不再核对。
/// </summary>
sealed class Registrar : ICommandRegistrar
{
    private readonly Dictionary<string, CommandDescriptor> _commands = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<CommandDescriptor> All => _commands.Values;

    public void Register(CommandDescriptor descriptor)
    {
        if (!_commands.TryAdd(descriptor.Name, descriptor))
            throw new InvalidOperationException($"重复登记 {descriptor.Name}");
    }

    public bool TryGet(string name, out CommandDescriptor? descriptor) => _commands.TryGetValue(name, out descriptor);
}
