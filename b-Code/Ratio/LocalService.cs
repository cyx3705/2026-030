using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ratio;

/// <summary>本机推理服务登记的一个可切换模型（来自服务的 <c>/v1/echo/models</c>）。</summary>
internal sealed record LocalModel(string Name, string Family, long Context, long Size, bool Installed, bool Supported, string Reason);

/// <summary>换模任务的当前阶段；服务端持久化在 <c>status/model-switch.json</c>。</summary>
internal sealed record LocalSwitchJob(string State, string Phase, string? Current, string? Target, string? Error);

/// <summary>同机其他推理监听（旧实验端点、Ollama）是否在答话；只报不管。</summary>
internal sealed record LocalListener(string Name, string Url, bool Up);

/// <summary>一次状态探测的结果。</summary>
internal sealed record LocalStatus(
    string Endpoint,
    bool Running,
    string? Current,
    LocalSwitchJob? Job,
    IReadOnlyList<LocalModel> Models,
    IReadOnlyList<LocalListener> Others);

/// <summary>跑一个外部进程：程序、参数、超时秒数、逐行输出、取消；返回退出码。</summary>
internal delegate Task<int> ProcessRunner(
    string fileName,
    IReadOnlyList<string> arguments,
    int timeoutSeconds,
    IProgress<string>? output,
    CancellationToken cancellation);

/// <summary>
/// 本机统一推理服务的管理面：状态、启停、换模。
/// </summary>
/// <remarks>
/// 服务归本模块所有（0.7.0）：源码在仓里 <c>b-Code-Service/</c>，随 DLL 发布（<see cref="ServiceBundle"/>），
/// 运行区是 F 盘特例 <c>F:\ModuleServicesData\Ratio</c>——WSL 里一个 llama-server（18769）前面挂 C++ 代理（11435，OpenAI 兼容）。
/// 同一时间只装一个模型，Echo、Codex 和本模块共用它，所以换模和停服影响所有客户端。
/// 换模走服务自己的管理协议；启停走运行区里的脚本，启动前先把脚本同步成本版本带的那份。
///
/// 对话不走这里：对话就是普通供应商 <c>local</c>，走 <see cref="ChatClient"/>。
/// 代理的地址取 <c>local</c> 供应商的接入点（去掉 <c>/v1</c>），改端口用 <c>ratio.provider.config</c>。
/// </remarks>
internal sealed class LocalService : IDisposable
{
    /// <summary>服务目录的默认位置；环境变量 <c>RATIO_LOCAL_ROOT</c> 可改。</summary>
    internal const string DefaultRoot = @"F:\ModuleServicesData\Ratio";

    /// <summary>服务跑在哪个 WSL 发行版里；与服务自带的 start-wsl-model-service.ps1 一致。</summary>
    private const string Distro = "Ubuntu";

    private static readonly (string Name, string Url)[] OtherListeners =
    [
        // Windows 版 Ollama 与统一服务共用 F:\Models\Ollama，在跑会抢模型库；启动脚本会先停掉它。
        ("Windows 版 Ollama（11434）", "http://127.0.0.1:11434/api/version"),
    ];

    private readonly Func<string> _endpoint;
    private readonly HttpClient _http;
    private readonly ProcessRunner _run;
    private readonly Func<IReadOnlyList<ServiceFile>> _bundle;
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public LocalService(ProviderStore store)
        : this(() => store.Resolve(ProviderStore.LocalProvider).BaseUrl, new HttpClient(), RunProcessAsync, null, ServiceBundle.Load)
    {
    }

    /// <summary>测试用：注入接入点、传输、进程、服务目录与服务文件。</summary>
    internal LocalService(Func<string> endpoint, HttpClient http, ProcessRunner run, string? root, Func<IReadOnlyList<ServiceFile>>? bundle = null)
    {
        _endpoint = endpoint;
        _http = http;
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _run = run;
        _bundle = bundle ?? (() => []);
        Root = root ?? ReadRoot();
    }

    /// <summary>服务目录（Windows 路径）。</summary>
    public string Root { get; }

    /// <summary>换模轮询间隔；测试调小。</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public void Dispose() => _http.Dispose();

    /// <summary>代理根地址：<c>local</c> 接入点去掉末尾的 <c>/v1</c>。</summary>
    public string ServiceUrl
    {
        get
        {
            var url = _endpoint().TrimEnd('/');
            return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;
        }
    }

    /// <summary>探测一次：代理不答话即视为没在跑，不报错。</summary>
    public async Task<LocalStatus> StatusAsync(CancellationToken cancellation)
    {
        var service = ServiceUrl;
        var others = new List<LocalListener>();
        foreach (var (name, url) in OtherListeners)
            others.Add(new LocalListener(name, url, await AnswersAsync(url, cancellation).ConfigureAwait(false)));

        string text;
        try
        {
            text = await RatioHttp.SendAsync(_http, "本机推理服务", HttpMethod.Get, service + "/v1/echo/models", null, null, 5, cancellation)
                .ConfigureAwait(false);
        }
        catch (RatioRemoteException)
        {
            return new LocalStatus(service, false, null, null, [], others);
        }

        var root = JsonNode.Parse(text) as JsonObject ?? throw new RatioRemoteException("本机推理服务返回的模型清单不是 JSON 对象");
        var models = (root["models"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(item => new LocalModel(
                Str(item["name"]) ?? "?",
                Str(item["family"]) ?? string.Empty,
                Num(item["context"]),
                Num(item["size"]),
                Bool(item["installed"]),
                Bool(item["supported"]),
                Str(item["reason"]) ?? string.Empty))
            .ToList();
        return new LocalStatus(service, true, Str(root["current"]), ReadJob(root["switch"] as JsonObject), models, others);
    }

    /// <summary>
    /// 起服务：已在跑就直接回状态；否则先把本版本带的服务脚本同步到运行区，再执行其中的 Windows 启动脚本
    /// （它会先让出 Windows 版 Ollama 占着的模型库，再在 WSL 里起模型与代理），然后等代理答话。
    /// 只在没在跑时同步：正在跑的服务换脚本，停服时就会用上一半新一半旧的那套。
    /// </summary>
    public async Task<(LocalStatus Status, bool Started)> StartAsync(int timeoutSeconds, IProgress<string>? progress, CancellationToken cancellation)
    {
        await _startGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync(timeoutSeconds, progress, cancellation).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<(LocalStatus Status, bool Started)> StartCoreAsync(int timeoutSeconds, IProgress<string>? progress, CancellationToken cancellation)
    {
        var status = await StatusAsync(cancellation).ConfigureAwait(false);
        if (status.Running)
            return (status, false);

        var files = _bundle();
        if (files.Count > 0)
        {
            var written = ServiceBundle.Deploy(Root, files);
            progress?.Report(written == 0
                ? $"服务脚本已是本版本（{files.Count} 个文件）"
                : $"已把 {written}/{files.Count} 个服务文件同步到 {Root}");
        }

        var script = Path.Combine(Root, "scripts", "start-wsl-model-service.ps1");
        if (!File.Exists(script))
            throw new RatioInputException($"找不到服务启动脚本：{script}；服务目录不对时设环境变量 RATIO_LOCAL_ROOT");

        progress?.Report($"启动本机推理服务：{script}");
        var exit = await _run(
            "powershell.exe",
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script],
            timeoutSeconds,
            progress,
            cancellation).ConfigureAwait(false);
        if (exit != 0)
            throw new RatioRemoteException($"启动脚本退出码 {exit}，日志在 {Path.Combine(Root, "linux-logs")}");

        // 脚本返回时模型多半已就绪；保险起见再等代理答话，最多 30 秒。
        for (var attempt = 0; attempt < 30; attempt++)
        {
            status = await StatusAsync(cancellation).ConfigureAwait(false);
            if (status.Running)
                return (status, true);
            await Task.Delay(PollInterval, cancellation).ConfigureAwait(false);
        }

        throw new RatioRemoteException($"启动脚本已返回，但 {ServiceUrl} 仍不答话，日志在 {Path.Combine(Root, "linux-logs")}");
    }

    /// <summary>停服务：服务脚本会先把 KV 缓存存盘再停，存盘失败就不停。</summary>
    public async Task<LocalStatus> StopAsync(int timeoutSeconds, IProgress<string>? progress, CancellationToken cancellation)
    {
        var script = Path.Combine(Root, "scripts", "wsl-model-service.sh");
        if (!File.Exists(script))
            throw new RatioInputException($"找不到服务脚本：{script}；服务目录不对时设环境变量 RATIO_LOCAL_ROOT");

        progress?.Report("停止本机推理服务（先保存 KV 缓存）");
        var exit = await _run(
            "wsl.exe",
            ["-d", Distro, "-e", "bash", ToWslPath(script), "stop"],
            timeoutSeconds,
            progress,
            cancellation).ConfigureAwait(false);
        if (exit != 0)
            throw new RatioRemoteException($"停止脚本退出码 {exit}；KV 缓存存盘失败时服务会保持运行，日志在 {Path.Combine(Root, "linux-logs")}");

        return await StatusAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// 换模：先按清单校验，再交给服务的换模任务，逐阶段报进度直到就绪或失败。
    /// 失败时服务自己会装回原模型，回执带它的原话。
    /// </summary>
    public async Task<LocalStatus> SwitchAsync(string name, int timeoutSeconds, IProgress<string>? progress, CancellationToken cancellation)
    {
        var status = await StatusAsync(cancellation).ConfigureAwait(false);
        if (!status.Running)
            throw new RatioInputException("本机推理服务没在运行；先执行 ratio.local.start");

        var model = status.Models.FirstOrDefault(item => item.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new RatioInputException(
                $"服务没有登记模型 {name}；可选：{string.Join(", ", status.Models.Select(item => item.Name))}");
        if (!model.Supported)
            throw new RatioInputException($"{model.Name} 不能切换：{(model.Reason.Length > 0 ? model.Reason : "服务判为不支持")}");
        if (status.Current == model.Name && status.Job?.State != "switching")
            return status;

        var service = ServiceUrl;
        // 服务只收本机应用的 JSON 请求：不带 Origin、Content-Type 必须是 application/json。
        var started = await RatioHttp.SendAsync(
            _http,
            "本机推理服务",
            HttpMethod.Post,
            service + "/v1/echo/model",
            null,
            new JsonObject { ["name"] = model.Name }.ToJsonString(),
            30,
            cancellation).ConfigureAwait(false);
        var job = ReadJob(JsonNode.Parse(started) as JsonObject);
        progress?.Report($"换模 {status.Current ?? "?"} → {model.Name}：{Phase(job?.Phase)}");

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds is > 0 and <= 1800 ? timeoutSeconds : 600);
        var lastPhase = job?.Phase;
        while (job is null || job.State == "switching")
        {
            if (DateTime.UtcNow > deadline)
                throw new RatioRemoteException($"换模超时，服务仍在「{Phase(lastPhase)}」；任务没有取消，稍后用 ratio.local.status 查看");
            await Task.Delay(PollInterval, cancellation).ConfigureAwait(false);
            try
            {
                var text = await RatioHttp.SendAsync(_http, "本机推理服务", HttpMethod.Get, service + "/v1/echo/model", null, null, 10, cancellation)
                    .ConfigureAwait(false);
                job = ReadJob(JsonNode.Parse(text) as JsonObject);
            }
            catch (RatioRemoteException)
            {
                // 代理重启期间会短暂不答话；服务文档说明换模任务不受影响，继续等。
                continue;
            }

            if (job is not null && job.Phase != lastPhase)
            {
                lastPhase = job.Phase;
                progress?.Report($"换模：{Phase(job.Phase)}");
            }
        }

        if (job.State == "failed")
            throw new RatioRemoteException($"换模失败：{job.Error ?? "服务未给原因"}");

        return await StatusAsync(cancellation).ConfigureAwait(false);
    }

    /// <summary>换模阶段的中文说法。</summary>
    internal static string Phase(string? phase) => phase switch
    {
        "waiting" => "等现有请求结束",
        "saving" => "保存 KV 缓存",
        "stopping" => "停旧模型",
        "loading" => "加载新模型",
        "restoring" => "恢复 KV 缓存",
        "rollback" => "失败，正在装回原模型",
        "ready" => "就绪",
        "failed" => "失败",
        null => "未知",
        _ => phase,
    };

    /// <summary><c>F:\A\b.sh</c> → <c>/mnt/f/A/b.sh</c>。</summary>
    internal static string ToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        if (full.Length < 3 || full[1] != ':')
            throw new RatioInputException($"服务目录必须在本机盘符下：{windowsPath}");
        return $"/mnt/{char.ToLowerInvariant(full[0])}{full[2..].Replace('\\', '/')}";
    }

    private static string ReadRoot()
    {
        var configured = Environment.GetEnvironmentVariable("RATIO_LOCAL_ROOT");
        return string.IsNullOrWhiteSpace(configured) ? DefaultRoot : configured.Trim();
    }

    private async Task<bool> AnswersAsync(string url, CancellationToken cancellation)
    {
        try
        {
            await RatioHttp.SendAsync(_http, url, HttpMethod.Get, url, null, null, 2, cancellation).ConfigureAwait(false);
            return true;
        }
        catch (RatioRemoteException)
        {
            return false;
        }
    }

    private static LocalSwitchJob? ReadJob(JsonObject? job)
        => job is null
            ? null
            : new LocalSwitchJob(Str(job["state"]) ?? "ready", Str(job["phase"]) ?? "ready", Str(job["current"]), Str(job["target"]), Str(job["error"]));

    private static string? Str(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long Num(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    private static bool Bool(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>默认进程执行：不开窗口，逐行转发输出，超时或取消就整树结束。</summary>
    private static async Task<int> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        IProgress<string>? output,
        CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, line) => { if (!string.IsNullOrWhiteSpace(line.Data)) output?.Report(line.Data); };
        process.ErrorDataReceived += (_, line) => { if (!string.IsNullOrWhiteSpace(line.Data)) output?.Report(line.Data); };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new RatioRemoteException($"无法运行 {fileName}：{ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds is > 0 and <= 1800 ? timeoutSeconds : 600));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            if (cancellation.IsCancellationRequested)
                throw;
            throw new RatioRemoteException($"{fileName} 超时（{timeoutSeconds}s）已结束；WSL 里的服务可能仍在启动，用 ratio.local.status 查看");
        }

        return process.ExitCode;
    }

    /// <summary>状态的结构化载荷（camelCase）。</summary>
    internal static string ToJson(LocalStatus status)
        => JsonSerializer.Serialize(
            new
            {
                endpoint = status.Endpoint,
                running = status.Running,
                current = status.Current,
                @switch = status.Job is null
                    ? null
                    : new { state = status.Job.State, phase = status.Job.Phase, current = status.Job.Current, target = status.Job.Target, error = status.Job.Error },
                models = status.Models.Select(model => new
                {
                    name = model.Name,
                    family = model.Family,
                    context = model.Context,
                    size = model.Size,
                    installed = model.Installed,
                    supported = model.Supported,
                    reason = model.Reason,
                }),
                others = status.Others.Select(other => new { name = other.Name, url = other.Url, up = other.Up }),
            },
            new JsonSerializerOptions { WriteIndented = true });

    /// <summary>状态的中文回执。</summary>
    internal static string Describe(LocalStatus status)
    {
        var text = new StringBuilder();
        if (!status.Running)
        {
            text.AppendLine($"本机推理服务没在运行（{status.Endpoint} 不答话）；ratio.local.start 可启动");
        }
        else
        {
            text.AppendLine($"本机推理服务在运行：{status.Endpoint}，当前模型 {status.Current ?? "?"}");
            if (status.Job is { State: "switching" } job)
                text.AppendLine($"  正在换模 → {job.Target}：{Phase(job.Phase)}");
            else if (status.Job is { State: "failed" } failed)
                text.AppendLine($"  上次换模失败：{failed.Error}");
            text.AppendLine("  可切换模型：");
            foreach (var model in status.Models)
            {
                var mark = model.Name == status.Current ? "*" : " ";
                var note = !model.Installed ? "未下载" : !model.Supported ? $"不可用：{model.Reason}" : $"{model.Size / (1024 * 1024 * 1024.0):0.#} GB";
                text.AppendLine($"  {mark} {model.Name}  上下文 {model.Context}  {note}");
            }
        }

        foreach (var other in status.Others)
            text.AppendLine($"{other.Name}：{(other.Up ? "在答话" : "未运行")}");
        return text.ToString().TrimEnd();
    }
}
