using System.Collections.Concurrent;
using System.Text;

namespace Ratio;

/// <summary>一次读取拿到的增量。</summary>
/// <param name="Text">从调用方给的位置到现在新到的正文。</param>
/// <param name="Next">下次读取要传的位置（已累积正文的字符数）。</param>
/// <param name="Done">生成已经结束（成功或失败）。</param>
/// <param name="Error">失败原因；成功或未结束为 null。</param>
internal sealed record StreamChunk(string Text, int Next, bool Done, string? Error);

/// <summary>
/// 一路流式生成：后台把增量写进来，调用方用 <c>ratio.stream.read</c> 按位置取。
/// </summary>
internal sealed class StreamSession
{
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private TaskCompletionSource _changed = NewSignal();
    private bool _done;
    private string? _error;

    public StreamSession(string id)
    {
        Id = id;
        Touched = DateTime.UtcNow;
    }

    public string Id { get; }

    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>最后一次被读取或写入的时间；闲置太久的流由登记表回收。</summary>
    public DateTime Touched { get; private set; }

    public bool Done
    {
        get
        {
            lock (_gate)
                return _done;
        }
    }

    public void Append(string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return;
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_done)
                return;
            _text.Append(delta);
            Touched = DateTime.UtcNow;
            signal = Swap();
        }

        signal.TrySetResult();
    }

    public void Complete(string? error)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_done)
                return;
            _done = true;
            _error = error;
            Touched = DateTime.UtcNow;
            signal = Swap();
        }

        signal.TrySetResult();
    }

    /// <summary>取 <paramref name="after"/> 之后的正文；没有新内容且未结束时最多等 <paramref name="waitMilliseconds"/>。</summary>
    public async Task<StreamChunk> ReadAsync(int after, int waitMilliseconds, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Clamp(waitMilliseconds, 0, 30000));
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                Touched = DateTime.UtcNow;
                var start = Math.Clamp(after, 0, _text.Length);
                if (start < _text.Length || _done)
                    return new StreamChunk(_text.ToString(start, _text.Length - start), _text.Length, _done, _error);
                wait = _changed.Task;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                lock (_gate)
                    return new StreamChunk(string.Empty, _text.Length, _done, _error);
            }

            try
            {
                await wait.WaitAsync(left, cancellation).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    private TaskCompletionSource Swap()
    {
        var old = _changed;
        _changed = NewSignal();
        return old;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// 进行中的流式生成登记表（0.8.0）。
/// </summary>
/// <remarks>
/// 指令一次只回一份回执，流式就拆成「开流拿编号 + 按位置读增量」两步，这样总线、MCP、CLI 都能用同一套指令。
/// 调用方不读了（页面关掉、进程退出）的流，闲置 <see cref="IdleLimit"/> 后自动取消并删除，上游请求跟着断开。
/// </remarks>
internal sealed class StreamRegistry : IDisposable
{
    /// <summary>同时最多几路流；超了说明调用方在漏关。</summary>
    internal const int MaxStreams = 32;

    internal static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(90);

    private readonly ConcurrentDictionary<string, StreamSession> _streams = new(StringComparer.Ordinal);
    private readonly Timer _sweep;
    private int _disposed;

    public StreamRegistry()
    {
        _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public int Count => _streams.Count;

    /// <summary>登记一路流并在后台跑生产者；生产者抛出的异常记为流的失败原因。</summary>
    public StreamSession Open(Func<StreamSession, CancellationToken, Task> producer)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(StreamRegistry));
        Sweep();
        if (_streams.Count >= MaxStreams)
            throw new RatioInputException($"同时进行的流式生成已达上限 {MaxStreams} 路；先读完或用 ratio.stream.cancel 关掉不用的");

        var session = new StreamSession(Guid.NewGuid().ToString("N"));
        _streams[session.Id] = session;
        var token = session.Cancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await producer(session, token).ConfigureAwait(false);
                session.Complete(null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                session.Complete("已取消");
            }
            catch (RatioRemoteException ex)
            {
                session.Complete(ex.Message);
            }
            catch (RatioInputException ex)
            {
                session.Complete(ex.Message);
            }
            catch (Exception ex)
            {
                session.Complete($"{ex.GetType().Name}: {ex.Message}");
            }
        });
        return session;
    }

    public StreamSession Get(string id)
        => _streams.TryGetValue(id?.Trim() ?? string.Empty, out var session)
            ? session
            : throw new RatioInputException($"没有编号为 {id} 的流（已读完关闭、闲置超时或从未打开）");

    /// <summary>读一段；读到结束的那一次之后流即删除。</summary>
    public async Task<StreamChunk> ReadAsync(string id, int after, int waitMilliseconds, CancellationToken cancellation)
    {
        var session = Get(id);
        var chunk = await session.ReadAsync(after, waitMilliseconds, cancellation).ConfigureAwait(false);
        if (chunk.Done)
            Remove(session);
        return chunk;
    }

    /// <summary>取消并删除；不存在时返回 false。</summary>
    public bool Cancel(string id)
    {
        if (!_streams.TryGetValue(id?.Trim() ?? string.Empty, out var session))
            return false;
        Remove(session);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _sweep.Dispose();
        foreach (var session in _streams.Values)
            Remove(session);
    }

    private void Remove(StreamSession session)
    {
        _streams.TryRemove(session.Id, out _);
        try
        {
            session.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var session in _streams.Values)
        {
            if (now - session.Touched > IdleLimit)
                Remove(session);
        }
    }
}
