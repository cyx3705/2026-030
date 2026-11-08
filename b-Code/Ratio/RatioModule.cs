using HistoryVulcan.Core.Modules;

namespace Ratio;

/// <summary>
/// 模块装配入口：把 Ratio 的指令接上宿主总线。
/// </summary>
/// <remarks>
/// 本模块**只提供一件事**：把本机与远端的推理资源变成宿主指令——模型调用，加上本机推理服务的启停与换模（0.6.0）。
/// 判断一段代码该不该进这个仓，用这条：它是否只是「把一次模型调用说清楚」。
/// 会话历史、提示词工程、结果加工都属于调用方，不进来。联网搜索算在「一次调用」之内
/// （DEC-007）：工具循环的状态只活在这一次调用里。
///
/// <see cref="Dispose"/> 由宿主在每轮热重载的拆除阶段调用，且早于新快照的
/// <see cref="Attach"/>，连接池因此跟着旧实例一起走。没有这一步，
/// 每重载一次就多留两个握着连接的 <c>HttpClient</c>。
/// </remarks>
public sealed class RatioModule : IModuleContextAware, IDisposable
{
    private readonly object _gate = new();
    private ChatClient? _client;
    private WebSearchClient? _search;
    private LocalService? _local;
    private LocalInference? _inference;

    /// <summary>宿主在装载时注入权威指令总线与命令注册器。</summary>
    public void Attach(IModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // 0.4.0：数据目录由宿主给，不再自己拼 %AppData% 下的包槽位路径（宿主 6.0.0 统一契约）。
        RatioRuntime.Use(context.Environment.DataDirectory);

        lock (_gate)
        {
            _client?.Dispose();
            _search?.Dispose();
            _local?.Dispose();
            _inference?.Dispose();
            var client = new ChatClient();
            var search = new WebSearchClient(RatioRuntime.Providers);
            var local = new LocalService(RatioRuntime.Providers);
            var inference = new LocalInference(RatioRuntime.Providers);
            _inference = inference;
            _client = client;
            _search = search;
            _local = local;
            context.RegisterCommands(registry =>
            {
                RatioCommandCatalog.Register(registry, RatioRuntime.Providers, client, search);
                RatioLocalCommands.Register(registry, local);
                RatioInferenceCommands.Register(registry, RatioRuntime.Providers, client, inference);
                RatioEmbedCommands.Register(registry, RatioRuntime.Providers, client);
            });
        }

        RatioRuntime.Log("module", $"已接入，默认供应商 {RatioRuntime.Providers.DefaultProvider}");
    }

    /// <summary>交还本模块占用的进程级资源。</summary>
    public void Dispose()
    {
        ChatClient? client;
        WebSearchClient? search;
        LocalService? local;
        LocalInference? inference;
        lock (_gate)
        {
            inference = _inference;
            _inference = null;
            client = _client;
            search = _search;
            local = _local;
            _client = null;
            _search = null;
            _local = null;
        }

        client?.Dispose();
        search?.Dispose();
        local?.Dispose();
        inference?.Dispose();
    }
}
