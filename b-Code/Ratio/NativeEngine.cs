using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace Ratio;

/// <summary>RATIO-NATIVE-001：原生 ABI；与命令、HTTP 网络及 WSL 无关。</summary>
internal interface INativeEngine : IDisposable
{
    int State { get; }
    void Start(IReadOnlyList<string> arguments);
    bool Stop(int timeoutMilliseconds);
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation);
}

/// <summary>
/// Ratio 自有 DLL 的 C ABI。HttpResponseMessage 只复用现有 JSON/SSE 消费代码，
/// 不会建立 HTTP 连接。所有回调在调用返回前有效；DLL 未停稳绝不 Free。
/// </summary>
internal sealed class NativeEngine : INativeEngine
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int VersionFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StartFn([MarshalAs(UnmanagedType.LPUTF8Str)] string args);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StopFn(int timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CancelFn(ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Callback(IntPtr context, int kind, int status, IntPtr bytes, nuint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RequestFn(ulong id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string method, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string body, Callback callback, IntPtr context);

    private readonly IntPtr _library;
    private readonly VersionFn _state;
    private readonly StartFn _start;
    private readonly StopFn _stop;
    private readonly CancelFn _cancel;
    private readonly RequestFn _request;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;
    private int _calls;
    private static long _serial;

    public NativeEngine(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new RatioInputException("原生推理 DLL 需要 Windows x64");
        if (!File.Exists(path)) throw new RatioInputException("尚未构建 Ratio 原生推理 DLL；先运行 scripts/echo-kv/build-native.ps1");
        // Load dependencies only from the DLL directory and Windows system dirs.
        _library = LoadLibraryEx(Path.GetFullPath(path), IntPtr.Zero, 0x100 | 0x1000);
        if (_library == IntPtr.Zero) throw new RatioInputException($"加载 Ratio 原生推理 DLL 失败（Win32 {Marshal.GetLastWin32Error()}）；检查配套 CUDA/ggml DLL");
        try
        {
            if (Get<VersionFn>("ratio_abi_version")() != 1) throw new RatioInputException("Ratio 原生推理 ABI 版本不匹配");
            _state = Get<VersionFn>("ratio_state");
            _start = Get<StartFn>("ratio_start");
            _stop = Get<StopFn>("ratio_stop");
            _cancel = Get<CancelFn>("ratio_cancel");
            _request = Get<RequestFn>("ratio_request");
        }
        catch { NativeLibrary.Free(_library); throw; }
    }

    [DllImport("kernel32", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
    private T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    public int State { get { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _state(); } } }
    public void Start(IReadOnlyList<string> arguments)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_start(System.Text.Json.JsonSerializer.Serialize(arguments)) != 0)
                throw new RatioRemoteException("Ratio 原生引擎已有模型或仍在停止，未加载第二份模型");
        }
    }
    public bool Stop(int timeoutMilliseconds) { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _stop(timeoutMilliseconds) == 0; } }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        var ready = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true });
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _shutdown.Token);
        var token = lifetime.Token;
        var id = (ulong)Interlocked.Increment(ref _serial);
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _calls++; }
        _ = Task.Run(() =>
        {
            Callback callback = (_, kind, status, bytes, length) =>
            {
                try
                {
                    if (token.IsCancellationRequested) return 0;
                    if (kind == 3) return 1;
                    var data = new byte[checked((int)length)];
                    Marshal.Copy(bytes, data, 0, data.Length);
                    if (kind == 0)
                    {
                        var response = new HttpResponseMessage((HttpStatusCode)status)
                        {
                            Content = new StreamContent(new NativeBodyStream(chunks.Reader, lifetime)),
                        };
                        response.Content.Headers.TryAddWithoutValidation("Content-Type", Encoding.UTF8.GetString(data));
                        if (!ready.TrySetResult(response)) response.Dispose();
                    }
                    else chunks.Writer.WriteAsync(data, token).AsTask().GetAwaiter().GetResult();
                    return 1;
                }
                catch { return 0; } // Never unwind managed exceptions through C++.
            };
            try
            {
                using var registration = token.Register(() => _cancel(id));
                token.ThrowIfCancellationRequested();
                var result = _request(id, request.Method.Method, request.RequestUri!.PathAndQuery, body, callback, IntPtr.Zero);
                token.ThrowIfCancellationRequested();
                if (result != 0) throw new HttpRequestException($"Ratio 原生请求失败（{result}）");
                if (!ready.Task.IsCompleted) throw new HttpRequestException("Ratio 原生请求没有返回状态");
                chunks.Writer.TryComplete();
            }
            catch (Exception error) { ready.TrySetException(error); chunks.Writer.TryComplete(error); }
            finally
            {
                GC.KeepAlive(callback);
                lock (_gate) { _calls--; Monitor.PulseAll(_gate); }
                // Stream owns lifetime after headers; failed pre-header calls own it here.
                if (!ready.Task.IsCompletedSuccessfully) lifetime.Dispose();
            }
        });
        try { return await ready.Task.WaitAsync(cancellation).ConfigureAwait(false); }
        catch
        {
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
            _ = ready.Task.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); }, TaskScheduler.Default);
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _shutdown.Cancel();
        var stopped = _stop(30_000) == 0;
        lock (_gate)
        {
            var deadline = Environment.TickCount64 + 30_000;
            while (stopped && _calls != 0 && Environment.TickCount64 < deadline) Monitor.Wait(_gate, 100);
            if (!stopped || _calls != 0)
            {
                RatioRuntime.Log("native", "原生引擎尚未停稳，保留 DLL 映射，禁止不安全卸载；需检查宿主日志");
                return;
            }
            NativeLibrary.Free(_library);
        }
    }
}

/// <summary>有界 SSE 缓冲；消费方 Dispose 立即取消上游原生请求。</summary>
internal sealed class NativeBodyStream(ChannelReader<byte[]> reader, CancellationTokenSource lifetime) : Stream
{
    private readonly CancellationToken _token = lifetime.Token;
    private int _disposed;
    private byte[]? _chunk;
    private int _offset;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _token);
        while (_chunk is null || _offset == _chunk.Length)
        {
            if (!await reader.WaitToReadAsync(linked.Token).ConfigureAwait(false)) return 0;
            if (!reader.TryRead(out _chunk)) continue;
            _offset = 0;
        }
        var count = Math.Min(buffer.Length, _chunk.Length - _offset);
        _chunk.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) { lifetime.Cancel(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
