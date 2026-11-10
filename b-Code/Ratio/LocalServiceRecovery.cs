using System.Net.Sockets;

namespace Ratio;

/// <summary>RATIO-LOCAL-001：连接被拒绝时恢复本模块管理的回环服务，仅重发一次尚未送达的请求。</summary>
internal sealed class LocalServiceRecovery(Func<string> endpoint, Func<CancellationToken, Task> start)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _retryAfter;
    private string? _failure;
    private volatile bool _disabled;

    internal void Disable() => _disabled = true;

    internal bool Owns(Uri? uri)
        => !_disabled && uri is not null && uri.IsLoopback
            && Uri.TryCreate(endpoint(), UriKind.Absolute, out var local)
            && local.IsLoopback && uri.Scheme == local.Scheme && uri.Authority == local.Authority;

    internal async Task RecoverAsync(CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_disabled)
                throw new HttpRequestException("模块正在卸载，不再自动启动本机推理服务");
            if (_failure is not null && Environment.TickCount64 < _retryAfter)
                throw new HttpRequestException($"本机推理服务自动启动失败，30 秒内不重复启动：{_failure}");
            try
            {
                RatioRuntime.Log("local", "连接被拒绝，检查本机推理服务；未运行时自动启动");
                // StartAsync 本身先查状态，已运行则直接返回，不重启现有服务。
                await start(cancellation).ConfigureAwait(false);
                _failure = null;
            }
            catch (Exception ex) when (ex is RatioRemoteException or RatioInputException)
            {
                _failure = ex.Message;
                _retryAfter = Environment.TickCount64 + 30_000;
                throw new HttpRequestException($"本机推理服务自动启动失败：{ex.Message}", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal HttpClient CreateClient()
        => new(new LocalServiceRecoveryHandler(new HttpClientHandler(), this));
}

/// <summary>只处理 ConnectionRefused；HTTP 错误、超时、已开始的响应与远端请求不重放。</summary>
internal sealed class LocalServiceRecoveryHandler(HttpMessageHandler inner, LocalServiceRecovery recovery) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!recovery.Owns(request.RequestUri))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        using var retry = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };
        foreach (var header in request.Headers)
            retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options)
            retry.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        if (request.Content is not null)
        {
            retry.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            foreach (var header in request.Content.Headers)
                retry.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (Refused(ex) && !cancellationToken.IsCancellationRequested)
        {
            await recovery.RecoverAsync(cancellationToken).ConfigureAwait(false);
            return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool Refused(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                return true;
        return false;
    }
}
