using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ratio;

// RATIO-NATIVE-001: opt-in same-model throughput probe; never stops a service.
internal static class NativeBenchmark
{
    internal static async Task Run()
    {
        var backend = Environment.GetEnvironmentVariable("RATIO_NATIVE_BENCH")!;
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "native-benchmark.json"));
        var workload = JsonNode.Parse(fixture)!;
        var root = Directory.CreateTempSubdirectory("Ratio.Native.Benchmark.").FullName;
        NativeEngine? engine = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = timeout.Token;
        try
        {
            HttpMessageHandler transport;
            var endpoint = Environment.GetEnvironmentVariable("RATIO_NATIVE_BENCH_URL") ?? "http://127.0.0.1:11435";
            if (backend == "dll")
            {
                var dll = Environment.GetEnvironmentVariable("RATIO_NATIVE_LIVE_DLL") ?? throw new Exception("Missing DLL");
                var model = Environment.GetEnvironmentVariable("RATIO_NATIVE_LIVE_MODEL") ?? throw new Exception("Missing GGUF");
                using var probe = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
                try
                {
                    using var old = await probe.GetAsync(endpoint + "/v1/models", token);
                    if (old.IsSuccessStatusCode) throw new Exception("Stop the existing model explicitly before running the same-GPU DLL benchmark");
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                var config = JsonNode.Parse(Encoding.UTF8.GetString(ServiceBundle.Load().First(f => f.RelativePath.Replace('\\', '/') == "config/echo-models.json").Content))!;
                var profile = config["models"]!.AsArray().OfType<JsonObject>().Single(p => p["name"]!.GetValue<string>() == workload["model"]!.GetValue<string>());
                using var runtime = new NativeLocalRuntime(root, () => endpoint + "/v1", () => []);
                Directory.CreateDirectory(Path.Combine(root, "native", "slots"));
                engine = new NativeEngine(dll);
                engine.Start(runtime.Arguments(profile, model));
                while (engine.State != 2)
                {
                    if (engine.State < 0) throw new Exception("Native benchmark model failed to load");
                    await Task.Delay(100, token);
                }
                transport = new NativeTransport(engine);
            }
            else throw new Exception("RATIO_NATIVE_BENCH must be dll");
            using var http = new HttpClient(transport) { Timeout = TimeSpan.FromMinutes(5) };
            async Task<JsonNode> Call(string op, JsonObject? fields = null)
            {
                fields ??= new(); fields["op"] = op;
                using var response = await http.PostAsync(endpoint + "/v1/kv/op", new StringContent(fields.ToJsonString(), Encoding.UTF8, "application/json"), token);
                var value = await response.Content.ReadAsStringAsync(token);
                if (!response.IsSuccessStatusCode) throw new Exception(value);
                return JsonNode.Parse(value)!;
            }
            var state = await Call("runtime");
            if (state["active_sequences"]?.GetValue<int>() != 0 || state["active_branches"]?.GetValue<int>() != 0)
                throw new Exception("Active model leases: benchmark refused");
            var caps = await Call("capabilities");
            var rows = new JsonArray();
            JsonArray? labels = null;
            var jobs = workload["jobs"]!.AsArray();
            for (var round = -1; round < 5; round++)
            {
                var total = Stopwatch.StartNew();
                var lease = await Call("create", new() { ["text"] = workload["prefix"]!.DeepClone() });
                var prefillMs = total.Elapsed.TotalMilliseconds;
                JsonObject Fields() => new() { ["sequence_id"] = lease["sequence_id"]!.DeepClone(), ["cache_epoch"] = lease["cache_epoch"]!.DeepClone() };
                try
                {
                    var body = Fields(); body["jobs"] = jobs.DeepClone();
                    var watch = Stopwatch.StartNew();
                    var result = await Call("select_batch", body);
                    var batchMs = watch.Elapsed.TotalMilliseconds;
                    var values = result["results"]!.AsArray();
                    if (values.Count != jobs.Count) throw new Exception("Missing batch results");
                    var currentLabels = new JsonArray(values.Select(v => JsonValue.Create(v!["label"]!.GetValue<string>()) as JsonNode).ToArray());
                    if (labels is not null && !JsonNode.DeepEquals(labels, currentLabels)) throw new Exception("Labels changed across rounds");
                    labels ??= currentLabels;
                    if (round >= 0)
                        rows.Add(new JsonObject { ["prefillMs"] = prefillMs, ["batchMs"] = batchMs,
                            ["serviceMs"] = result["elapsed_ms"]!.DeepClone(), ["wordsPerSecond"] = jobs.Count * 1000 / batchMs,
                            ["includingPrefillWordsPerSecond"] = jobs.Count * 1000 / total.Elapsed.TotalMilliseconds });
                    Console.WriteLine($"BENCH {backend} round={round} jobs={jobs.Count} batch={batchMs:0.0}ms words/s={jobs.Count * 1000 / batchMs:0.00}");
                }
                finally { await Call("release", Fields()); }
            }
            state = await Call("runtime");
            if (state["active_sequences"]!.GetValue<int>() != 0 || state["active_branches"]!.GetValue<int>() != 0) throw new Exception("Benchmark leaked KV leases");
            var report = new JsonObject { ["backend"] = backend, ["model"] = workload["model"]!.DeepClone(),
                ["workloadSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture))),
                ["jobs"] = jobs.Count, ["caps"] = caps, ["rounds"] = rows, ["labels"] = labels, ["zeroLeases"] = true };
            var output = Environment.GetEnvironmentVariable("RATIO_NATIVE_BENCH_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "ratio-benchmark-" + backend + ".json");
            File.WriteAllText(output, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("BENCH report=" + output);
        }
        finally { engine?.Dispose(); Directory.Delete(root, true); }
    }

    private sealed class NativeTransport(INativeEngine engine) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => engine.SendAsync(request, token);
    }
}
