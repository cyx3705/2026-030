using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ratio;

internal static class NativeTests
{
    internal static async Task Live()
    {
        var dll = Environment.GetEnvironmentVariable("RATIO_NATIVE_LIVE_DLL") ?? throw new Exception("Missing native DLL");
        var model = Environment.GetEnvironmentVariable("RATIO_NATIVE_LIVE_MODEL") ?? throw new Exception("Missing GGUF model");
        var device = Environment.GetEnvironmentVariable("RATIO_NATIVE_LIVE_DEVICE") ?? "CUDA0";
        using var engine = new NativeEngine(dll);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var snapshots = Directory.CreateTempSubdirectory("Ratio.Native.Live.").FullName;
        string[] arguments = ["-m", model, "--alias", "ratio-native-test", "--device", device, "-ngl", device == "none" ? "0" : "all",
            "-c", "2048", "-np", "2", "--kv-unified", "--fit", "off", "--load-mode", "none", "-b", "256", "-ub", "128",
            "-ctk", "q8_0", "-ctv", "q8_0", "-fa", "on", "-t", "8", "-tb", "8", "--jinja", "--no-webui", "--offline",
            "--chat-template-kwargs", "{\"enable_thinking\":false}", "--reasoning-budget", "0", "--ratio-kv-branches", "8",
            "--slots", "--slot-save-path", snapshots];
        engine.Start(arguments);
        while (engine.State != 2)
        {
            if (engine.State < 0) throw new Exception("Native model load failed");
            await Task.Delay(100, timeout.Token);
        }
        Console.WriteLine("Native live: model ready");
        using var inference = new LocalInference(() => "http://ratio-native/v1", new HttpClient(new DirectNativeTransport(engine)));
        var caps = await inference.CapabilitiesAsync(null, timeout.Token);
        if (caps["select_batch"]?.GetValue<bool>() != true || caps["branch_batch_size"]?.GetValue<int>() != 8)
            throw new Exception("Native DLL lost batch capabilities");
        const string prefix = "<|im_start|>system\nAnswer the question briefly.<|im_end|>\n<|im_start|>user\nSay Hello world.<|im_end|>\n<|im_start|>assistant\n";
        var sequence = await inference.OpenAsync(null, prefix, timeout.Token);
        var jobs = new List<(string, IReadOnlyList<string>)>
        {
            ("\nChoose A for yes or B for no. Answer:", new[] { " A", " B" }),
            ("\nChoose C for Hello or D for Goodbye. Answer:", new[] { " C", " D" }),
            ("\nChoose E for English or F for French. Answer:", new[] { " E", " F" }),
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var batch = await inference.ChooseBatchAsync(sequence.SequenceId, sequence.CacheEpoch, null, jobs, timeout.Token);
        if (!batch.Batched || batch.Rows.Count != jobs.Count) throw new Exception("Native batch result missing");
        for (var index = 0; index < jobs.Count; index++)
        {
            var job = jobs[index];
            var single = await inference.ChooseAsync(sequence.SequenceId, sequence.CacheEpoch, prefix, job.Item1, job.Item2, timeout.Token);
            if (single["label"]?.GetValue<string>() != batch.Rows[index]?["label"]?.GetValue<string>())
                throw new Exception("Native batch/single labels disagree");
        }
        var elapsed = watch.Elapsed.TotalMilliseconds;
        var appended = await inference.AppendAsync(sequence.SequenceId, sequence.CacheEpoch, "Hello", timeout.Token);
        if (appended.PrefixTokens <= sequence.PrefixTokens) throw new Exception("Native append did not grow KV");
        var stream = inference.StreamSequence(sequence.SequenceId, sequence.CacheEpoch, 8);
        var text = new StringBuilder();
        var after = 0;
        while (true)
        {
            var read = await stream.ReadAsync(after, 1000, timeout.Token);
            text.Append(read.Text); after = read.Next;
            if (read.Error is not null) throw new Exception(read.Error);
            if (read.Done) break;
        }
        if (text.Length == 0) throw new Exception("Native stream produced no text");
        await inference.CloseAsync(sequence.SequenceId, sequence.CacheEpoch, timeout.Token);
        var final = await inference.CapabilitiesAsync(null, timeout.Token);
        if (final["active_sequences"]?.GetValue<int>() != 0 || final["active_branches"]?.GetValue<int>() != 0
            || final["forks"]?.GetValue<long>() != final["releases"]?.GetValue<long>()) throw new Exception("Native KV lease leak");
        async Task NativeCall(string method, string path, string? body = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), "http://ratio-native" + path);
            if (body is not null) request.Content = new StringContent(body);
            using var response = await engine.SendAsync(request, timeout.Token);
            var result = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode) throw new Exception("Native management route failed: " + path + " " + result);
        }
        await NativeCall("GET", "/slots");
        await NativeCall("POST", "/completion", "{\"prompt\":\"Hello\",\"n_predict\":1,\"id_slot\":0}");
        for (var slot = 0; slot < 2; slot++)
            await NativeCall("POST", $"/slots/{slot}?action=save", $$"""{"filename":"slot-{{slot}}.bin"}""");
        if (!engine.Stop(30_000)) throw new Exception("Native model did not stop");
        engine.Start(arguments);
        while (engine.State != 2)
        {
            if (engine.State < 0) throw new Exception("Native model restart failed");
            await Task.Delay(100, timeout.Token);
        }
        for (var slot = 0; slot < 2; slot++)
            await NativeCall("POST", $"/slots/{slot}?action=restore", $$"""{"filename":"slot-{{slot}}.bin"}""");
        if (!engine.Stop(30_000)) throw new Exception("Native restarted model did not stop");
        Directory.Delete(snapshots, true);
        Console.WriteLine($"Native live ({device}): batch service={batch.ServiceMs:0.0}ms, batch+3 single calls={elapsed:0.0}ms, stream chars={text.Length}, zero leases, restart and snapshot restore passed");
        if (device != "none") await LiveController(dll, model, timeout.Token);
    }

    private static async Task LiveController(string dll, string model, CancellationToken cancellation)
    {
        var directory = Directory.CreateTempSubdirectory("Ratio.Native.Controller.").FullName;
        try
        {
            var root = Path.Combine(directory, "ModuleServicesData", "Ratio");
            var models = Path.Combine(directory, "Models", "Ollama");
            var manifests = Path.Combine(models, "manifests", "registry.ollama.ai", "library", "test");
            var blobs = Path.Combine(models, "blobs");
            Directory.CreateDirectory(manifests); Directory.CreateDirectory(blobs);
            using var source = File.OpenRead(model);
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).ToLowerInvariant();
            File.Copy(model, Path.Combine(blobs, "sha256-" + digest));
            File.WriteAllText(Path.Combine(manifests, "small"), $$"""{"layers":[{"mediaType":"application/vnd.ollama.image.model","digest":"sha256:{{digest}}","size":{{source.Length}}}]}""");
            var config = """{"default":"test","models":[{"name":"test","family":"qwen","context":2048,"manifest":"test/small","kv_branches":8},{"name":"alternate","family":"qwen","context":2048,"manifest":"test/small","kv_branches":8}]}""";
            using var runtime = new NativeLocalRuntime(root, () => "http://127.0.0.1:11435/v1",
                () => [new("config/echo-models.json", Encoding.UTF8.GetBytes(config))], _ => new NativeEngine(dll));
            await runtime.StartAsync(60, null, cancellation);
            await runtime.SwitchAsync("alternate", 60, null, cancellation);
            await runtime.StopAsync(60, null, cancellation);
            await runtime.StartAsync(60, null, cancellation);
            if (runtime.Status().Current != "alternate") throw new Exception("Native controller lost active model");
            await runtime.StopAsync(60, null, cancellation);
            Console.WriteLine("Native live controller: load, switch, save, stop, reload and restore passed");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class DirectNativeTransport(INativeEngine engine) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => engine.SendAsync(request, token);
    }

    internal static async Task Lifecycle()
    {
        using var scope = new NativeFixture();
        if (scope.Runtime.Status().Running || scope.Engine.Starts != 0) throw new Exception("Status must not load a model");
        var (status, started) = await scope.Runtime.StartAsync(2, null, CancellationToken.None);
        if (!started || !status.Running || status.Current != "qwen3:8b") throw new Exception("Native start failed");
        var args = scope.Engine.Arguments;
        foreach (var value in new[] { "--ratio-kv-branches", "32", "2048", "1024", "--kv-unified" })
            if (!args.Contains(value)) throw new Exception("Lost batch setting: " + value);
        if (args.Any(value => value.Contains("wsl", StringComparison.OrdinalIgnoreCase))) throw new Exception("Native backend launched WSL");
        await scope.Runtime.StartAsync(2, null, CancellationToken.None);
        if (scope.Engine.Starts != 1) throw new Exception("Loaded the model twice");
        await scope.Runtime.SwitchAsync("alternate", 2, null, CancellationToken.None);
        if (scope.Runtime.Status().Current != "alternate") throw new Exception("Switch failed");
        await scope.Runtime.StopAsync(2, null, CancellationToken.None);
        if (scope.Runtime.Status().Running || scope.Engine.Saves < 4) throw new Exception("Stop must save public slots");
    }

    internal static async Task AutomaticStartup()
    {
        using var scope = new NativeFixture();
        using var http = new HttpClient(scope.Runtime.Transport(new RemoteTransport()));
        scope.Engine.HoldLoading = true;
        var requests = Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            using var response = await http.GetAsync("http://127.0.0.1:11435/v1/models");
            response.EnsureSuccessStatusCode();
        }));
        await Task.Delay(30);
        if (requests.IsCompleted || scope.Engine.Starts != 1) throw new Exception("Concurrent requests did not wait for the shared load");
        scope.Engine.CompleteLoading();
        await requests;
        if (scope.Engine.Starts != 1) throw new Exception("Concurrent requests loaded multiple models");
        await scope.Runtime.StopAsync(2, null, CancellationToken.None);
        scope.Engine.FailStart = true;
        for (var index = 0; index < 3; index++)
        {
            try { await scope.Runtime.StartAsync(2, null, CancellationToken.None, automatic: true); throw new Exception("Expected startup failure"); }
            catch (RatioRemoteException) { }
        }
        if (scope.Engine.Starts != 2) throw new Exception("Failed startup ignored cooldown");
        scope.Engine.FailStart = false;
        await scope.Runtime.StartAsync(2, null, CancellationToken.None);
        if (!scope.Runtime.Status().Running || scope.Engine.Starts != 3) throw new Exception("Explicit start could not retry");
    }

    internal static async Task Rollback()
    {
        using var scope = new NativeFixture();
        await scope.Runtime.StartAsync(2, null, CancellationToken.None);
        scope.Engine.FailAlternate = true;
        try { await scope.Runtime.SwitchAsync("alternate", 2, null, CancellationToken.None); throw new Exception("Expected failure"); }
        catch (RatioRemoteException) { }
        if (!scope.Runtime.Status().Running || scope.Runtime.Status().Current != "qwen3:8b") throw new Exception("Failed to restore previous model");
        scope.Engine.FailSave = true;
        var stops = scope.Engine.Stops;
        try { await scope.Runtime.StopAsync(2, null, CancellationToken.None); throw new Exception("Expected save failure"); }
        catch (RatioRemoteException) { }
        if (!scope.Runtime.Status().Running || scope.Engine.Stops != stops) throw new Exception("Save failure stopped the model");
    }

    internal static async Task UnloadDuringLoad()
    {
        using var scope = new NativeFixture();
        scope.Engine.HoldLoading = true;
        var loading = scope.Runtime.StartAsync(600, null, CancellationToken.None);
        if (scope.Engine.Starts != 1) throw new Exception("Expected pending native load");
        scope.Runtime.Dispose();
        try { await loading; throw new Exception("Unload did not cancel model loading"); }
        catch (OperationCanceledException) { }
        if (scope.Engine.State != 0) throw new Exception("Unload left model active");
    }

    internal static async Task RoutingAndDrain()
    {
        using var scope = new NativeFixture();
        var remote = new RemoteTransport();
        using var http = new HttpClient(scope.Runtime.Transport(remote));
        using var local = await http.PostAsync("http://127.0.0.1:11435/v1/kv/op", new StringContent("{\"op\":\"capabilities\"}"));
        if (!local.IsSuccessStatusCode || remote.Calls != 0 || scope.Engine.Starts != 1) throw new Exception("Local call escaped native transport");
        using var outside = await http.GetAsync("https://example.invalid/v1/models");
        using var otherPort = await http.GetAsync("http://127.0.0.1:9999/v1/models");
        if (remote.Calls != 2) throw new Exception("Hijacked a remote provider or other local service");
        scope.Engine.Active = 1;
        var stop = scope.Runtime.StopAsync(2, null, CancellationToken.None);
        await Task.Delay(30);
        using var release = await http.PostAsync("http://127.0.0.1:11435/v1/kv/op", new StringContent("{\"op\":\"release\"}"));
        await stop;
        if (scope.Runtime.Status().Running) throw new Exception("Lease release could not drain during stop");
    }

    internal static async Task StreamAndCancellation()
    {
        var channel = Channel.CreateBounded<byte[]>(1);
        using var lifetime = new CancellationTokenSource();
        using (var stream = new NativeBodyStream(channel.Reader, lifetime))
        {
            await channel.Writer.WriteAsync(Encoding.UTF8.GetBytes("data: 中文\n\n"));
            channel.Writer.Complete();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            if (await reader.ReadToEndAsync() != "data: 中文\n\n") throw new Exception("SSE bytes corrupted");
        }
        if (!lifetime.IsCancellationRequested) throw new Exception("Stream dispose did not cancel native producer");
        using var blockedLifetime = new CancellationTokenSource();
        using var blocked = new NativeBodyStream(Channel.CreateBounded<byte[]>(1).Reader, blockedLifetime);
        var pending = blocked.ReadAsync(new byte[1]).AsTask();
        blocked.Dispose();
        try { await pending; throw new Exception("Read did not cancel"); } catch (OperationCanceledException) { }
    }

    private sealed class RemoteTransport : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }); }
    }

    private sealed class NativeFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Ratio.Native.Tests", Guid.NewGuid().ToString("N"));
        internal FakeNative Engine { get; } = new();
        internal NativeLocalRuntime Runtime { get; }
        internal NativeFixture()
        {
            var root = Path.Combine(_directory, "ModuleServicesData", "Ratio");
            var manifests = Path.Combine(_directory, "Models", "Ollama", "manifests", "registry.ollama.ai", "library", "qwen3");
            var blobs = Path.Combine(_directory, "Models", "Ollama", "blobs");
            Directory.CreateDirectory(manifests); Directory.CreateDirectory(blobs);
            var digest = new string('a', 64);
            File.WriteAllBytes(Path.Combine(blobs, "sha256-" + digest), "GGUFtest"u8.ToArray());
            File.WriteAllText(Path.Combine(manifests, "8b"), $$"""{"layers":[{"mediaType":"application/vnd.ollama.image.model","digest":"sha256:{{digest}}","size":8}]}""");
            var config = """{"default":"qwen3:8b","models":[{"name":"qwen3:8b","family":"qwen","context":32768,"manifest":"qwen3/8b","kv_branches":32,"batch":2048,"ubatch":1024},{"name":"alternate","family":"qwen","context":32768,"manifest":"qwen3/8b"}]}""";
            Runtime = new(root, () => "http://127.0.0.1:11435/v1", () => [new("config/echo-models.json", Encoding.UTF8.GetBytes(config))], _ => Engine);
        }
        public void Dispose() { Runtime.Dispose(); Directory.Delete(_directory, true); }
    }

    private sealed class FakeNative : INativeEngine
    {
        public int State { get; private set; }
        internal int Starts, Stops, Saves, Active;
        internal bool FailAlternate, FailSave, HoldLoading, FailStart;
        internal IReadOnlyList<string> Arguments = [];
        public void Start(IReadOnlyList<string> arguments)
        {
            Starts++; Arguments = arguments;
            State = FailStart ? -1 : HoldLoading ? 1 : FailAlternate && arguments.Contains("alternate") ? -1 : 2;
        }
        internal void CompleteLoading() { HoldLoading = false; State = 2; }
        public bool Stop(int timeoutMilliseconds) { Stops++; State = 0; return true; }
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(token));
            var path = request.RequestUri!.AbsolutePath;
            var code = HttpStatusCode.OK;
            var text = "{}";
            if (path == "/slots") text = "[{\"is_processing\":false},{\"is_processing\":false}]";
            if (body?["op"]?.GetValue<string>() == "runtime") text = $$"""{"active_sequences":{{Active}},"active_branches":0}""";
            if (body?["op"]?.GetValue<string>() == "release") Active = 0;
            if (path.StartsWith("/slots/", StringComparison.Ordinal) && request.RequestUri.Query.Contains("save", StringComparison.Ordinal))
            { Saves++; if (FailSave) { code = HttpStatusCode.Conflict; text = "save failed"; } }
            return new(code) { Content = new StringContent(text) };
        }
        public void Dispose() { State = 0; }
    }
}
