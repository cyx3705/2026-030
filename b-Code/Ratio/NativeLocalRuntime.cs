using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ratio;

/// <summary>RATIO-NATIVE-001：Ratio 拥有模型、DLL、换模和快照；业务原语仍由原服务器队列执行。</summary>
internal sealed class NativeLocalRuntime(string root, Func<string> endpoint, Func<IReadOnlyList<ServiceFile>> bundle,
    Func<string, INativeEngine>? factory = null) : IDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _state = new();
    private INativeEngine? _engine;
    private string? _current;
    private string? _modelDigest;
    private string? _snapshotBuild;
    private long _retryAfter;
    private string? _startupError;
    private LocalSwitchJob? _job;
    private bool _changing;
    private bool _starting;
    private bool _disposed;
    private int _admitting;
    private string DllPath => Path.Combine(root, "native", "bin", "ratio-inference.dll");
    private string Slots => Path.Combine(root, "native", "slots");

    internal bool Owns(Uri? uri) => uri is not null && uri.IsLoopback && Uri.TryCreate(endpoint(), UriKind.Absolute, out var local)
        && local.IsLoopback && uri.Scheme == local.Scheme && uri.Authority == local.Authority;
    internal HttpMessageHandler Transport(HttpMessageHandler remote) => new NativeRoutingHandler(this, remote);

    private JsonObject Catalog()
    {
        var path = Path.Combine(root, "config", "echo-models.json");
        var json = File.Exists(path) ? File.ReadAllText(path) : Encoding.UTF8.GetString(
            bundle().First(file => file.RelativePath.Replace('\\', '/') == "config/echo-models.json").Content);
        return JsonNode.Parse(json)!.AsObject();
    }

    internal (LocalModel Model, string? File) Inspect(JsonObject profile)
    {
        var name = profile["name"]!.GetValue<string>();
        var family = profile["family"]?.GetValue<string>() ?? "";
        var context = profile["context"]!.GetValue<long>();
        var disabled = profile["disabled_reason"]?.GetValue<string>();
        try
        {
            var models = Environment.GetEnvironmentVariable("RATIO_MODEL_ROOT")
                ?? Path.Combine(Directory.GetParent(root)!.Parent!.FullName, "Models");
            var manifestRoot = Path.GetFullPath(Path.Combine(models, "Ollama", "manifests", "registry.ollama.ai", "library"));
            var manifest = Path.GetFullPath(Path.Combine(manifestRoot, profile["manifest"]!.GetValue<string>()));
            if (!manifest.StartsWith(manifestRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("模型清单路径越界");
            var layers = JsonNode.Parse(File.ReadAllText(manifest))!["layers"]!.AsArray();
            var layer = layers.First(item => item?["mediaType"]?.GetValue<string>() == "application/vnd.ollama.image.model")!;
            var digest = layer["digest"]!.GetValue<string>();
            if (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal)
                || !digest[7..].All(char.IsAsciiHexDigit)) throw new IOException("模型摘要无效");
            var path = Path.Combine(models, "Ollama", "blobs", digest.Replace(':', '-'));
            var size = layer["size"]!.GetValue<long>();
            using var file = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            file.ReadExactly(magic);
            var installed = file.Length == size && magic.SequenceEqual("GGUF"u8);
            return (new(name, family, context, size, installed, installed && disabled is null,
                disabled ?? (installed ? "" : "模型文件未下载完整")), installed ? path : null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Text.Json.JsonException or KeyNotFoundException)
        { return (new(name, family, context, 0, false, false, disabled ?? "模型文件或清单不可用"), null); }
    }

    internal IReadOnlyList<string> Arguments(JsonObject profile, string file)
    {
        string Number(string key, int fallback) => (profile[key]?.GetValue<int>() ?? fallback).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var args = new List<string> { "-m", file, "--alias", profile["name"]!.GetValue<string>(), "--device", "CUDA0", "-ngl", "all",
            "-ncmoe", Number("cpu_moe_layers", 0), "--fit", "off", "--load-mode", "none" };
        args.AddRange((profile["extra"] as JsonArray ?? []).Select(value => value!.GetValue<string>()));
        // Preserve fable5.2's model-specific fan-out, batch and microbatch sizes.
        args.AddRange(["-c", Number("context", 32768), "-np", "2", "--kv-unified", "--cache-prompt", "--cache-ram", "1024",
            "--ctx-checkpoints", "8", "--checkpoint-min-step", "1024", "--slots", "--slot-save-path", Slots,
            "-b", Number("batch", 256), "-ub", Number("ubatch", 128), "-ctk", profile["kv_type"]?.GetValue<string>() ?? "q8_0",
            "-ctv", profile["kv_type"]?.GetValue<string>() ?? "q8_0", "-fa", "on", "-t", "8", "-tb", "16", "--jinja"]);
        if (profile["template"]?.GetValue<string>() is { } template)
        {
            if (Path.GetFileName(template) != template) throw new RatioInputException("模型模板必须在 config 目录内");
            args.AddRange(["--chat-template-file", Path.Combine(root, "config", template)]);
        }
        args.AddRange(["--chat-template-kwargs", "{\"enable_thinking\":false}", "--reasoning-budget", "0", "--no-webui", "--offline",
            "--no-context-shift", "--perf", "-lv", Number("verbosity", 4)]);
        // Native-specific option is parsed by the ABI, not a process-global env var.
        args.AddRange(["--ratio-kv-branches", Number("kv_branches", 1)]);
        return args;
    }

    internal LocalStatus Status()
    {
        lock (_state)
        {
            var running = !_disposed && _engine?.State == 2;
            return new(endpoint(), running, running ? _current : null, _job,
                Catalog()["models"]!.AsArray().OfType<JsonObject>().Select(profile => Inspect(profile).Model).ToList(), [], "native-dll");
        }
    }

    internal async Task<(LocalStatus Status, bool Started)> StartAsync(int seconds, IProgress<string>? progress, CancellationToken cancellation, bool automatic = false)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _shutdown.Token);
        cancellation = operation.Token;
        await _lifecycle.WaitAsync(cancellation).ConfigureAwait(false);
        var attempted = false;
        try
        {
            lock (_state)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_engine?.State == 2) return (Status(), false);
                if (automatic && _startupError is not null && Environment.TickCount64 < _retryAfter)
                    throw new RatioRemoteException("原生模型自动加载失败，30 秒内不重复加载：" + _startupError);
                _changing = true;
                attempted = true;
                _starting = true;
            }
            if (!Owns(new Uri(endpoint()))) throw new RatioInputException("原生后端只接管 local 的回环接入点，不接管远端地址");
            if (factory is null)
            {
                // Refuse competing local inference runtimes; this is only a health probe.
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                try
                {
                    var service = endpoint().TrimEnd('/');
                    if (service.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) service = service[..^3];
                    using var old = await probe.GetAsync(service + "/v1/models", cancellation).ConfigureAwait(false);
                    if (old.IsSuccessStatusCode) throw new RatioInputException("发现其他本机推理服务；停用后再加载 Ratio 定制 DLL，避免重复占用显存");
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
            }
            ServiceBundle.Deploy(root, bundle());
            if (factory is null) ServiceBundle.DeployNative(root);
            var selected = Path.Combine(root, "status", "active-model.json");
            var name = File.Exists(selected) ? JsonNode.Parse(File.ReadAllText(selected))?["name"]?.GetValue<string>() : null;
            name ??= Catalog()["default"]!.GetValue<string>();
            await LoadAsync(name, seconds, progress, cancellation).ConfigureAwait(false);
            _startupError = null;
            return (Status(), true);
        }
        catch (Exception error) when (attempted && error is (RatioInputException or RatioRemoteException))
        {
            _startupError = error.Message;
            _retryAfter = Environment.TickCount64 + 30_000;
            throw;
        }
        finally { lock (_state) { _changing = false; _starting = false; } _lifecycle.Release(); }
    }

    private async Task LoadAsync(string name, int seconds, IProgress<string>? progress, CancellationToken cancellation)
    {
        var profile = Catalog()["models"]!.AsArray().OfType<JsonObject>().FirstOrDefault(p => p["name"]?.GetValue<string>() == name)
            ?? throw new RatioInputException($"未登记的模型：{name}");
        var info = Inspect(profile);
        if (!info.Model.Supported || info.File is null) throw new RatioInputException($"{name} 不可用：{info.Model.Reason}");
        Directory.CreateDirectory(Slots);
        _engine ??= (factory ?? (path => new NativeEngine(path)))(DllPath);
        if (_engine.State != 0 && !_engine.Stop(30_000)) throw new RatioRemoteException("上一次模型加载尚未停止，未加载第二份模型");
        progress?.Report($"Ratio DLL 正在加载 {name}");
        _engine.Start(Arguments(profile, info.File));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 1800)));
        try
        {
            while (_engine.State != 2)
            {
                if (_engine.State < 0) throw new RatioRemoteException("Ratio 原生模型加载失败，请检查宿主原生日志");
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
            _current = name;
            _modelDigest = Path.GetFileName(info.File);
            await SnapshotsAsync("restore", cancellation).ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(root, "status"));
            var selection = Path.Combine(root, "status", "active-model.json");
            File.WriteAllText(selection + ".ratio-tmp", new JsonObject { ["name"] = name }.ToJsonString());
            File.Move(selection + ".ratio-tmp", selection, true);
        }
        catch
        {
            _engine.Stop(30_000);
            throw;
        }
    }

    private async Task<JsonNode> CallAsync(string method, string path, JsonObject? body, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "http://ratio-native" + path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString());
        using var response = await _engine!.SendAsync(request, cancellation).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new RatioRemoteException($"原生引擎拒绝 {path}：{text}");
        return JsonNode.Parse(text)!;
    }

    private async Task WaitIdleAsync(int seconds, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 1800)));
        while (true)
        {
            bool admitting;
            lock (_state) admitting = _admitting != 0;
            if (admitting) { await Task.Delay(100, timeout.Token).ConfigureAwait(false); continue; }
            var slots = (await CallAsync("GET", "/slots", null, timeout.Token).ConfigureAwait(false)).AsArray();
            var kv = await CallAsync("POST", "/v1/kv/op", new JsonObject { ["op"] = "runtime" }, timeout.Token).ConfigureAwait(false);
            if (!slots.Any(slot => slot?["is_processing"]?.GetValue<bool>() == true)
                && kv["active_sequences"]?.GetValue<int>() == 0 && kv["active_branches"]?.GetValue<int>() == 0) return;
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task SnapshotsAsync(string action, CancellationToken cancellation)
    {
        // Native snapshots have a separate namespace: Linux ABI snapshots are
        // preserved, never interpreted as Windows state. Save failure keeps model alive.
        if (_snapshotBuild is null)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (File.Exists(DllPath))
                foreach (var dll in Directory.EnumerateFiles(Path.GetDirectoryName(DllPath)!, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
                {
                    hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(dll).ToLowerInvariant()));
                    using var stream = File.OpenRead(dll);
                    hash.AppendData(SHA256.HashData(stream));
                }
            _snapshotBuild = Convert.ToHexString(hash.GetHashAndReset())[..16];
        }
        var build = _snapshotBuild;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_current + ":" + _modelDigest)))[..16];
        for (var slot = 0; slot < 2; slot++)
        {
            var filename = $"native-{build}-{key}-{slot}.bin";
            if (action == "restore" && !File.Exists(Path.Combine(Slots, filename))) continue;
            var pending = action == "save" ? filename + ".pending" : filename;
            try
            {
                await CallAsync("POST", $"/slots/{slot}?action={action}", new JsonObject { ["filename"] = pending }, cancellation).ConfigureAwait(false);
                if (action == "save" && File.Exists(Path.Combine(Slots, pending)))
                {
                    var total = new DirectoryInfo(Slots).EnumerateFiles().Sum(file => file.Length);
                    if (total > 40_000_000_000) throw new RatioRemoteException("原生 KV 快照超过 40 GB 预算，保留运行中的模型与已有快照");
                    File.Move(Path.Combine(Slots, pending), Path.Combine(Slots, filename), true);
                }
            }
            finally { if (action == "save") File.Delete(Path.Combine(Slots, pending)); }
        }
    }

    internal async Task<LocalStatus> StopAsync(int seconds, IProgress<string>? progress, CancellationToken cancellation)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _shutdown.Token);
        cancellation = operation.Token;
        await _lifecycle.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            lock (_state) { ObjectDisposedException.ThrowIf(_disposed, this); _changing = true; }
            if (_engine?.State == 2)
            {
                progress?.Report("等待现有请求和共享 KV 租约释放，然后保存原生快照");
                await WaitIdleAsync(seconds, cancellation).ConfigureAwait(false);
                await SnapshotsAsync("save", cancellation).ConfigureAwait(false);
            }
            if (_engine is not null && !_engine.Stop(Math.Clamp(seconds, 1, 1800) * 1000))
                throw new RatioRemoteException("Ratio 原生引擎未停稳，保留 DLL");
            return Status();
        }
        finally { lock (_state) _changing = false; _lifecycle.Release(); }
    }

    internal async Task<LocalStatus> SwitchAsync(string name, int seconds, IProgress<string>? progress, CancellationToken cancellation)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _shutdown.Token);
        cancellation = operation.Token;
        await _lifecycle.WaitAsync(cancellation).ConfigureAwait(false);
        string? previous = null;
        var stopped = false;
        try
        {
            lock (_state)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_engine?.State != 2) throw new RatioInputException("原生引擎未运行；先执行 ratio.local.start");
                if (_current == name) return Status();
                var target = Status().Models.FirstOrDefault(model => model.Name == name);
                if (target is null || !target.Supported) throw new RatioInputException($"模型 {name} 未登记或不可用");
                previous = _current;
                _changing = true;
                _job = new("switching", "waiting", previous, name, null);
            }
            await WaitIdleAsync(seconds, cancellation).ConfigureAwait(false);
            _job = _job with { Phase = "saving" };
            await SnapshotsAsync("save", cancellation).ConfigureAwait(false);
            if (!_engine!.Stop(30_000)) throw new RatioRemoteException("旧模型未停止，未加载新模型");
            stopped = true;
            _job = _job with { Phase = "loading" };
            await LoadAsync(name, seconds, progress, cancellation).ConfigureAwait(false);
            _job = new("ready", "ready", name, name, null);
            return Status();
        }
        catch (Exception error)
        {
            var message = error.Message;
            if (stopped && previous is not null && !_shutdown.IsCancellationRequested)
            {
                try { await LoadAsync(previous, seconds, progress, _shutdown.Token).ConfigureAwait(false); message += "；已恢复原模型"; }
                catch (Exception rollback) { message += "；恢复原模型失败：" + rollback.Message; }
            }
            _job = new("failed", "failed", _current, name, message);
            if (error is OperationCanceledException) throw;
            throw new RatioRemoteException(message);
        }
        finally { lock (_state) _changing = false; _lifecycle.Release(); }
    }

    internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        var release = request.RequestUri?.AbsolutePath == "/v1/kv/op" && request.Content is not null
            && JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false))?["op"]?.GetValue<string>() == "release";
        if (request.RequestUri?.AbsolutePath == "/v1/kv/op" && request.Content is not null)
        {
            var op = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false))?["op"]?.GetValue<string>();
            if (op == "capabilities" && _current is not null && Catalog()["models"]!.AsArray().OfType<JsonObject>()
                .First(p => p["name"]!.GetValue<string>() == _current)["shared_kv"]?.GetValue<bool>() == false)
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"supported\":false,\"reason\":\"model_shared_kv_disabled\"}") };
        }
        INativeEngine engine;
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_changing && !_starting && !release) throw new RatioRemoteException("原生模型正在切换，请稍后重试");
            if (!_starting && _engine?.State == 2) engine = _engine;
            else engine = null!;
        }
        if (engine is null)
        {
            await StartAsync(600, null, cancellation, automatic: true).ConfigureAwait(false);
            lock (_state) engine = _engine!;
        }
        lock (_state)
        {
            if (_changing && !_starting && !release) throw new RatioRemoteException("原生模型正在切换，请稍后重试");
            _admitting++;
        }
        try { return await engine.SendAsync(request, cancellation).ConfigureAwait(false); }
        finally { lock (_state) _admitting--; }
    }

    public void Dispose()
    {
        lock (_state) { if (_disposed) return; _disposed = true; }
        _shutdown.Cancel(); // Break pending model-load/drain waits before taking the lifecycle gate.
        _lifecycle.Wait();
        try { _engine?.Dispose(); }
        finally { _lifecycle.Release(); }
    }
}

internal sealed class NativeRoutingHandler(NativeLocalRuntime runtime, HttpMessageHandler remote) : DelegatingHandler(remote)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => runtime.Owns(request.RequestUri) ? runtime.SendAsync(request, cancellationToken) : base.SendAsync(request, cancellationToken);
}
