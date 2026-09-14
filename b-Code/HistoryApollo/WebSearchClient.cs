using System.Text.Json;
using System.Text.Json.Nodes;

namespace HistoryApollo;

/// <summary>一条网页搜索结果。</summary>
/// <param name="Title">网页标题；服务端没给时退回链接。</param>
/// <param name="Url">网页链接。</param>
/// <param name="SiteName">网站名称；可能为空。</param>
/// <param name="Published">发布时间原文；可能为空。</param>
/// <param name="Summary">摘要；优先取长摘要，没有时取片段。</param>
internal sealed record WebSearchHit(string Title, string Url, string? SiteName, string? Published, string Summary);

/// <summary>模型在对话中途要求联网时，由 <see cref="ChatClient"/> 调用的那一侧。</summary>
internal interface IWebSearch
{
    /// <summary>搜一次，拿回最多 <paramref name="count"/> 条结果。</summary>
    /// <exception cref="ApolloInputException">搜索服务没配密钥——这是配置错，整次调用应当失败。</exception>
    /// <exception cref="ApolloRemoteException">远端拒绝、超时或不可达——只是这一次搜索失败。</exception>
    Task<IReadOnlyList<WebSearchHit>> SearchAsync(
        string query,
        int count,
        int timeoutSeconds,
        CancellationToken cancellation);
}

/// <summary>
/// 博查 Web Search API：<c>POST /v1/web-search</c>。
/// </summary>
/// <remarks>
/// 接入点与密钥和对话供应商放在同一个 <see cref="ProviderStore"/> 里，名字固定为
/// <see cref="ProviderStore.SearchProvider"/>。换钥匙仍是 <c>apollo.key.set provider=bocha</c>，
/// 不另开一套密钥库，也就不必另写一套脱敏与环境变量规则。
/// </remarks>
internal sealed class WebSearchClient : IWebSearch, IDisposable
{
    private readonly ProviderStore _store;
    private readonly HttpClient _http;

    public WebSearchClient(ProviderStore store)
        : this(store, new HttpClient())
    {
    }

    /// <summary>测试用：注入自定义传输。</summary>
    internal WebSearchClient(ProviderStore store, HttpClient http)
    {
        _store = store;
        _http = http;
        // 超时逐次由 CancellationToken 控制，HttpClient 自身不再另设一层。
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public void Dispose() => _http.Dispose();

    public async Task<IReadOnlyList<WebSearchHit>> SearchAsync(
        string query,
        int count,
        int timeoutSeconds,
        CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ApolloInputException("搜索关键词不能为空");

        var profile = _store.Resolve(ProviderStore.SearchProvider);
        if (!profile.HasKey)
        {
            throw new ApolloInputException(
                $"搜索服务 {profile.Name} 未配置密钥：先执行 apollo.key.set provider={profile.Name} token=<密钥>，"
                + $"或设置环境变量 APOLLO_{profile.Name.ToUpperInvariant()}_KEY");
        }

        var body = new JsonObject
        {
            ["query"] = query.Trim(),
            ["summary"] = true,
            ["freshness"] = "noLimit",
            ["count"] = Math.Clamp(count, 1, 50),
        };

        var payload = await ApolloHttp.SendAsync(
            _http,
            profile.Name,
            HttpMethod.Post,
            profile.BaseUrl + "/v1/web-search",
            profile.ApiKey!,
            body.ToJsonString(),
            timeoutSeconds,
            cancellation).ConfigureAwait(false);

        return ReadHits(profile.Name, payload);
    }

    /// <summary>解析 <c>data.webPages.value</c>。没有链接的条目丢掉——模型拿到它也没法引用。</summary>
    internal static IReadOnlyList<WebSearchHit> ReadHits(string serviceName, string payload)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new ApolloRemoteException($"{serviceName} 返回的不是 JSON：{ex.Message}");
        }

        if (root is not JsonObject document)
            throw new ApolloRemoteException($"{serviceName} 返回的不是 JSON 对象");

        // HTTP 200 里仍可能装着业务错误（{"code":403,"msg":"余额不足"}）。
        // 把它当成「没有结果」，模型就会一本正经地回答「查不到」，而真正该看的是那句余额不足。
        if (document["code"] is { } code && !IsOk(code))
        {
            throw new ApolloRemoteException(
                $"{serviceName} 返回 {code.ToJsonString().Trim('"')}：{ApolloHttp.DescribeError(payload)}");
        }

        if (document["data"] is not JsonObject data
            || data["webPages"] is not JsonObject webPages
            || webPages["value"] is not JsonArray pages)
        {
            return [];
        }

        var hits = new List<WebSearchHit>(pages.Count);
        foreach (var page in pages.OfType<JsonObject>())
        {
            var url = Text(page, "url");
            if (url == null)
                continue;

            hits.Add(new WebSearchHit(
                Text(page, "name") ?? url,
                url,
                Text(page, "siteName"),
                Text(page, "datePublished"),
                Text(page, "summary") ?? Text(page, "snippet") ?? string.Empty));
        }

        return hits;
    }

    private static bool IsOk(JsonNode code)
        => code is JsonValue value
           && ((value.TryGetValue<int>(out var number) && number == 200)
               || (value.TryGetValue<string>(out var text) && text.Trim() == "200"));

    private static string? Text(JsonObject node, string name)
        => node[name] is JsonValue value
           && value.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
