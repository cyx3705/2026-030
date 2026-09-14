using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HistoryApollo;

/// <summary>
/// 对话补全与联网搜索共用的一次 HTTP 往返：超时、认证头与错误翻译。
/// </summary>
/// <remarks>
/// 失败一律翻译成 <see cref="ApolloRemoteException"/>，消息里带着服务端给的那句原因。
/// 两类远端各写一份的话，「超时」「不可达」的措辞迟早会分家，调用方就得认两套说法。
/// </remarks>
internal static class ApolloHttp
{
    public static async Task<string> SendAsync(
        HttpClient http,
        string serviceName,
        HttpMethod method,
        string url,
        string apiKey,
        string? json,
        int timeoutSeconds,
        CancellationToken cancellation)
    {
        var seconds = timeoutSeconds is > 0 and <= 600 ? timeoutSeconds : 120;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new ApolloRemoteException($"{serviceName} 请求超时（{seconds}s）");
        }
        catch (HttpRequestException ex)
        {
            throw new ApolloRemoteException($"{serviceName} 不可达：{ex.Message}");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ApolloRemoteException($"{serviceName} 返回 {(int)response.StatusCode}：{DescribeError(text)}");
            return text;
        }
    }

    /// <summary>从错误体里挑出人能读的那一句；挑不出就回截断的原文。</summary>
    public static string DescribeError(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                        return error.GetString() ?? text;
                    if (error.ValueKind == JsonValueKind.Object
                        && error.TryGetProperty("message", out var nested)
                        && nested.ValueKind == JsonValueKind.String)
                    {
                        return nested.GetString() ?? text;
                    }
                }

                // 博查的错误体不套 error：{"code":403,"msg":"…"} 或 {"message":"…"}。
                foreach (var name in new[] { "message", "msg" })
                {
                    if (root.TryGetProperty(name, out var flat)
                        && flat.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(flat.GetString()))
                    {
                        return flat.GetString()!;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        var single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= 300 ? single : single[..300] + "…";
    }
}
