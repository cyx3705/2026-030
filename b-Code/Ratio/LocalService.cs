using System.Text;
using System.Text.Json;

namespace Ratio;

/// <summary>本机推理服务登记的一个可切换模型（来自 Ratio 模型配置）。</summary>
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
    IReadOnlyList<LocalListener> Others,
    string Backend = "native-dll");

/// <summary>RATIO-NATIVE-002：本机推理仅由 Ratio 自有 DLL 提供，无外部进程后端。</summary>
internal sealed class LocalService : IDisposable
{
    internal const string DefaultRoot = @"F:\ModuleServicesData\Ratio";
    private readonly NativeLocalRuntime _native;
    private readonly Func<string> _endpoint;
    public LocalService(ProviderStore store)
    {
        Root = Environment.GetEnvironmentVariable("RATIO_LOCAL_ROOT")?.Trim() is { Length: > 0 } configured ? configured : DefaultRoot;
        _endpoint = () => store.Resolve(ProviderStore.LocalProvider).BaseUrl;
        _native = new NativeLocalRuntime(Root, _endpoint, ServiceBundle.Load);
    }
    public string Root { get; }
    public string ServiceUrl => _endpoint().TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? _endpoint().TrimEnd('/')[..^3] : _endpoint().TrimEnd('/');
    internal HttpMessageHandler LocalTransport(HttpMessageHandler remote) => _native.Transport(remote);
    public Task<LocalStatus> StatusAsync(CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(_native.Status()); }
    public Task<(LocalStatus Status, bool Started)> StartAsync(int seconds, IProgress<string>? progress, CancellationToken cancellation) => _native.StartAsync(seconds, progress, cancellation);
    public Task<LocalStatus> StopAsync(int seconds, IProgress<string>? progress, CancellationToken cancellation) => _native.StopAsync(seconds, progress, cancellation);
    public Task<LocalStatus> SwitchAsync(string name, int seconds, IProgress<string>? progress, CancellationToken cancellation) => _native.SwitchAsync(name, seconds, progress, cancellation);
    public void Dispose() => _native.Dispose();

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

    /// <summary>状态的结构化载荷（camelCase）。</summary>
    internal static string ToJson(LocalStatus status)
        => JsonSerializer.Serialize(
            new
            {
                endpoint = status.Endpoint,
                backend = status.Backend,
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
            text.AppendLine($"本机原生模型未加载；ratio.local.start 可启动");
        }
        else
        {
            text.AppendLine($"本机推理服务在运行（{status.Backend}），当前模型 {status.Current ?? "?"}");
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
