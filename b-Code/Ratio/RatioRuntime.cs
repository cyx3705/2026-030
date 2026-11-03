using System.Diagnostics;

namespace Ratio;

/// <summary>
/// 模块自有的运行态：数据目录、配置库和日志。
/// </summary>
/// <remarks>
/// 0.4.0 起数据目录由宿主给（宿主 6.0.0 统一契约，<c>IModuleContext.Environment.DataDirectory</c>，
/// 即 <c>ModuleData\Ratio</c>）：独立于包槽位，装包、热重载、卸载都不动它，密钥不会被一次热装抹掉。
/// 0.3.x 落在运行包 <c>data/</c> 的旧数据已在宿主 6.0.0 切换时一次性搬过来，模块不再认旧布局。
/// </remarks>
internal static class RatioRuntime
{
    private static readonly object Gate = new();
    private static string? _dataRoot;
    private static ProviderStore? _providers;

    /// <summary>模块运行态目录；宿主接入前读取即报错。</summary>
    internal static string DataRoot
        => _dataRoot ?? throw new InvalidOperationException("Ratio 尚未接入宿主，数据目录未知。");

    /// <summary>供应商配置与密钥库。</summary>
    internal static ProviderStore Providers
        => _providers ?? throw new InvalidOperationException("Ratio 尚未接入宿主，配置库未打开。");

    /// <summary>
    /// 接入时由宿主给数据目录。同一目录重复接入（热重载）沿用已打开的配置库。
    /// </summary>
    internal static void Use(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var root = Path.GetFullPath(dataDirectory);
        lock (Gate)
        {
            if (string.Equals(_dataRoot, root, StringComparison.OrdinalIgnoreCase) && _providers is not null)
                return;
            Directory.CreateDirectory(root);
            AdoptPreviousStore(root);
            _dataRoot = root;
            _providers = new ProviderStore(Path.Combine(root, "providers.json"));
        }
    }

    /// <summary>
    /// 0.6.0 改名（HistoryApollo → Ratio）后宿主给的数据目录跟着换成 <c>ModuleData\Ratio</c>。
    /// 新目录还没有配置库、而旧模块的还在时，复制一份过来——密钥和供应商设置不必重配。
    /// 只复制不移动：旧模块卸载前两边都能用；之后的修改只落在新库。
    /// </summary>
    private static void AdoptPreviousStore(string root)
    {
        var target = Path.Combine(root, "providers.json");
        var parent = Path.GetDirectoryName(root);
        if (File.Exists(target) || parent is null)
            return;
        var previous = Path.Combine(parent, "HistoryApollo", "providers.json");
        if (!File.Exists(previous))
            return;
        try
        {
            File.Copy(previous, target);
            Trace.WriteLine($"[Ratio:store] 已从 {previous} 接过供应商配置");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>写 Trace 与本地文件的轻量日志；不依赖宿主日志接口。</summary>
    internal static void Log(string category, string message)
    {
        Trace.WriteLine($"[Ratio:{category}] {message}");
        var root = _dataRoot;
        if (root is null)
            return;
        try
        {
            var directory = Path.Combine(root, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, $"ratio-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
