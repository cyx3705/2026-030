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
};

// 联网实调默认不跑：它要花钱、要外网，且依赖本机已配置的真实密钥。
// 交付验收时显式打开 APOLLO_LIVE=1，让「能不能真的调通」这件事也有可重复的证据。
if (string.Equals(Environment.GetEnvironmentVariable("APOLLO_LIVE"), "1", StringComparison.Ordinal))
    tests = [.. tests, ("live deepseek call", TestLiveCallAsync)];

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

    // 内置的那家仍在，登记新供应商不会顶掉它。
    Equal("https://api.deepseek.com", scope.Store.Resolve("deepseek").BaseUrl);
    Equal(2, scope.Store.All().Count);

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
    ApolloCommandCatalog.Register(registry, scope.Store, client);

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
    return Task.CompletedTask;
}

static Task TestSecretParameterContractAsync()
{
    using var scope = new StoreScope();
    using var client = FakeTransport.Client((_, _) => (HttpStatusCode.OK, "{}"));
    var registry = new CommandRegistry();
    ApolloCommandCatalog.Register(registry, scope.Store, client);

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

static void Throws<T>(Action action)
    where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
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
        => new(new HttpClient(new FakeTransport(respond)));

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
