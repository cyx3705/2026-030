using System.Text.Json;
using System.Text.Json.Serialization;

namespace HistoryApollo;

/// <summary>一个已解析好的供应商档案：接入点、默认模型和当次可用的密钥。</summary>
/// <param name="Name">供应商标识（小写）。</param>
/// <param name="BaseUrl">OpenAI 兼容接入点根地址，不含 <c>/chat/completions</c>。</param>
/// <param name="Model">未显式指定 <c>model=</c> 时使用的模型。</param>
/// <param name="ApiKey">密钥；未配置时为 null。</param>
/// <param name="KeySource">密钥来自哪里：<c>env:变量名</c> 或 <c>store</c>；未配置时为 <c>none</c>。</param>
internal sealed record ProviderProfile(
    string Name,
    string BaseUrl,
    string Model,
    string? ApiKey,
    string KeySource)
{
    /// <summary>是否已经拿得到密钥。</summary>
    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>配置或输入不满足调用前提；这类错误直接变成失败回执，不是异常堆栈。</summary>
internal sealed class ApolloInputException(string message) : Exception(message);

/// <summary>
/// 供应商配置与密钥库。
/// </summary>
/// <remarks>
/// **密钥永远不进仓库、不进命令回执。** 库文件落在模块 <c>data/</c> 下，
/// 对外只暴露 <see cref="Mask"/> 后的尾段。
///
/// 环境变量优先于库文件：CI 或临时会话可以不落盘就换一把钥匙，
/// 也让「本机配置」与「本次运行」两件事不必互相覆盖。
/// </remarks>
internal sealed class ProviderStore(string path)
{
    private readonly object _gate = new();
    private Snapshot? _cache;

    /// <summary>内置供应商。新增一家只需要在这里加一行——协议都是 OpenAI 兼容的 chat/completions。</summary>
    private static readonly IReadOnlyDictionary<string, ProviderDefaults> Builtin =
        new Dictionary<string, ProviderDefaults>(StringComparer.OrdinalIgnoreCase)
        {
            ["deepseek"] = new("https://api.deepseek.com", "deepseek-chat"),
        };

    private const string BuiltinDefaultProvider = "deepseek";

    /// <summary>库文件路径；测试与诊断用。</summary>
    public string Path { get; } = path;

    /// <summary>当前默认供应商。</summary>
    public string DefaultProvider
    {
        get
        {
            lock (_gate)
                return Load().Default;
        }
    }

    /// <summary>全部已知供应商，已按名称排序。</summary>
    public IReadOnlyList<ProviderProfile> All()
    {
        lock (_gate)
        {
            var snapshot = Load();
            return snapshot.Entries.Keys
                .Concat(Builtin.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => Resolve(snapshot, name))
                .ToList();
        }
    }

    /// <summary>解析一个供应商档案；<paramref name="name"/> 为空时取默认供应商。</summary>
    /// <exception cref="ApolloInputException">名称既不在库里也不是内置供应商。</exception>
    public ProviderProfile Resolve(string? name)
    {
        lock (_gate)
        {
            var snapshot = Load();
            var resolved = string.IsNullOrWhiteSpace(name) ? snapshot.Default : name.Trim();
            if (!snapshot.Entries.ContainsKey(resolved) && !Builtin.ContainsKey(resolved))
            {
                var known = string.Join(", ", snapshot.Entries.Keys.Concat(Builtin.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
                throw new ApolloInputException($"未知供应商 {resolved}；已知：{known}");
            }

            return Resolve(snapshot, resolved);
        }
    }

    /// <summary>写入密钥。空值按清除处理由调用方拦下，这里只拒绝。</summary>
    public ProviderProfile SetKey(string? name, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ApolloInputException("密钥不能为空");
        if (token.Any(char.IsWhiteSpace))
            throw new ApolloInputException("密钥不能含空白字符；请检查是否粘贴了多余的引号或换行");

        return Mutate(name, entry => entry with { ApiKey = token.Trim() });
    }

    /// <summary>清除库里保存的密钥。环境变量给的密钥不归本库管，清不掉。</summary>
    public ProviderProfile ClearKey(string? name) => Mutate(name, entry => entry with { ApiKey = null });

    /// <summary>改这个供应商的默认模型。</summary>
    public ProviderProfile SetModel(string? name, string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ApolloInputException("模型名不能为空");
        return Mutate(name, entry => entry with { Model = model.Trim() });
    }

    /// <summary>
    /// 登记一家新的供应商。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SetBaseUrl"/> 的区别只在这一点：它接受一个库里还没有的名字。
    /// 写操作默认不建新条目——那样一个拼错的 <c>provider=</c> 会安静地长出一家
    /// 没有密钥、也永远调不通的供应商；新增必须是明说的动作。
    /// </remarks>
    public ProviderProfile Define(string name, string baseUrl, string? model)
    {
        var normalized = (name ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0 || !normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            throw new ApolloInputException($"供应商名只能是字母、数字、连字符或下划线：{name}");

        var entry = new StoredProvider { BaseUrl = NormalizeBaseUrl(baseUrl), Model = model?.Trim() };
        lock (_gate)
        {
            var snapshot = Load();
            var entries = new Dictionary<string, StoredProvider>(snapshot.Entries, StringComparer.OrdinalIgnoreCase)
            {
                [normalized] = entry,
            };
            Save(snapshot with { Entries = entries });
        }

        return Resolve(normalized);
    }

    /// <summary>改这个供应商的接入点根地址。</summary>
    public ProviderProfile SetBaseUrl(string? name, string baseUrl)
        => Mutate(name, entry => entry with { BaseUrl = NormalizeBaseUrl(baseUrl) });

    /// <summary>是否已经认识这个名字（库里有，或者是内置的）。</summary>
    public bool Knows(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;
        var trimmed = name.Trim();
        lock (_gate)
            return Load().Entries.ContainsKey(trimmed) || Builtin.ContainsKey(trimmed);
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ApolloInputException($"接入点必须是 http/https 绝对地址：{baseUrl}");
        }

        return baseUrl.Trim().TrimEnd('/');
    }

    /// <summary>切换默认供应商。</summary>
    public ProviderProfile SetDefaultProvider(string name)
    {
        var profile = Resolve(name);
        lock (_gate)
        {
            var snapshot = Load();
            Save(snapshot with { Default = profile.Name });
        }

        return Resolve(profile.Name);
    }

    /// <summary>只留头尾各几位的密钥展示形式。任何回执、日志和结构化载荷都只能出现它。</summary>
    public static string Mask(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "(未配置)";
        var trimmed = key.Trim();
        if (trimmed.Length <= 8)
            return new string('*', trimmed.Length);
        return $"{trimmed[..4]}{new string('*', 6)}{trimmed[^4..]}";
    }

    private ProviderProfile Mutate(string? name, Func<StoredProvider, StoredProvider> change)
    {
        // 先解析一次：未知供应商在这里就被挡住，不会因为写操作凭空建出一家来。
        var profile = Resolve(name);
        lock (_gate)
        {
            var snapshot = Load();
            var current = snapshot.Entries.GetValueOrDefault(profile.Name) ?? new StoredProvider();
            var entries = new Dictionary<string, StoredProvider>(snapshot.Entries, StringComparer.OrdinalIgnoreCase)
            {
                [profile.Name] = change(current),
            };
            Save(snapshot with { Entries = entries });
        }

        return Resolve(profile.Name);
    }

    private static ProviderProfile Resolve(Snapshot snapshot, string name)
    {
        var defaults = Builtin.GetValueOrDefault(name) ?? new ProviderDefaults(string.Empty, string.Empty);
        var stored = snapshot.Entries.GetValueOrDefault(name) ?? new StoredProvider();

        var (envKey, envSource) = ReadEnvironmentKey(name);
        var key = envKey ?? stored.ApiKey;
        var source = envKey != null ? envSource : stored.ApiKey != null ? "store" : "none";

        return new ProviderProfile(
            name.ToLowerInvariant(),
            (stored.BaseUrl ?? defaults.BaseUrl).TrimEnd('/'),
            stored.Model ?? defaults.Model,
            key,
            source);
    }

    /// <summary>
    /// 环境变量密钥：先看模块自己的 <c>APOLLO_&lt;供应商&gt;_KEY</c>，
    /// 再看供应商官方惯例的 <c>&lt;供应商&gt;_API_KEY</c>。
    /// </summary>
    private static (string? Key, string Source) ReadEnvironmentKey(string name)
    {
        var upper = name.ToUpperInvariant();
        foreach (var variable in new[] { $"APOLLO_{upper}_KEY", $"{upper}_API_KEY" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
                return (value.Trim(), $"env:{variable}");
        }

        return (null, "none");
    }

    private Snapshot Load()
    {
        if (_cache is { } cached)
            return cached;

        Snapshot snapshot;
        try
        {
            snapshot = File.Exists(Path)
                ? Parse(File.ReadAllText(Path))
                : Snapshot.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 库读不出来不该让整个模块失能：退回内置默认，调用时再以「密钥未配置」失败。
            ApolloRuntime.Log("store", $"配置库不可读，本次使用内置默认：{ex.Message}");
            snapshot = Snapshot.Empty;
        }

        _cache = snapshot;
        return snapshot;
    }

    private static Snapshot Parse(string text)
    {
        var document = JsonSerializer.Deserialize<StoreDocument>(text);
        if (document == null)
            return Snapshot.Empty;

        var entries = new Dictionary<string, StoredProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in document.Providers ?? [])
            entries[pair.Key] = pair.Value;

        return new Snapshot(
            string.IsNullOrWhiteSpace(document.Default) ? BuiltinDefaultProvider : document.Default,
            entries);
    }

    private void Save(Snapshot snapshot)
    {
        var document = new StoreDocument
        {
            Default = snapshot.Default,
            Providers = snapshot.Entries.ToDictionary(pair => pair.Key, pair => pair.Value),
        };

        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, Path, overwrite: true);
        _cache = snapshot;
    }

    private sealed record ProviderDefaults(string BaseUrl, string Model);

    private sealed record Snapshot(string Default, IReadOnlyDictionary<string, StoredProvider> Entries)
    {
        public static Snapshot Empty { get; } = new(
            BuiltinDefaultProvider,
            new Dictionary<string, StoredProvider>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed record StoredProvider
    {
        [JsonPropertyName("baseUrl")]
        public string? BaseUrl { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("apiKey")]
        public string? ApiKey { get; init; }
    }

    private sealed class StoreDocument
    {
        [JsonPropertyName("default")]
        public string? Default { get; set; }

        [JsonPropertyName("providers")]
        public Dictionary<string, StoredProvider>? Providers { get; set; }
    }
}
