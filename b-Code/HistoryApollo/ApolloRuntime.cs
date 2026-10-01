using System.Diagnostics;

namespace HistoryApollo;

/// <summary>
/// 模块自有的运行态：数据目录、配置库和日志。
/// </summary>
/// <remarks>
/// 0.4.0 起数据目录由宿主给（宿主 6.0.0 统一契约，<c>IModuleContext.Environment.DataDirectory</c>，
/// 即 <c>ModuleData\HistoryApollo</c>）：独立于包槽位，装包、热重载、卸载都不动它，密钥不会被一次热装抹掉。
/// 0.3.x 落在运行包 <c>data/</c> 的旧数据已在宿主 6.0.0 切换时一次性搬过来，模块不再认旧布局。
/// </remarks>
internal static class ApolloRuntime
{
    private static readonly object Gate = new();
    private static string? _dataRoot;
    private static ProviderStore? _providers;

    /// <summary>模块运行态目录；宿主接入前读取即报错。</summary>
    internal static string DataRoot
        => _dataRoot ?? throw new InvalidOperationException("Apollo 尚未接入宿主，数据目录未知。");

    /// <summary>供应商配置与密钥库。</summary>
    internal static ProviderStore Providers
        => _providers ?? throw new InvalidOperationException("Apollo 尚未接入宿主，配置库未打开。");

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
            _dataRoot = root;
            _providers = new ProviderStore(Path.Combine(root, "providers.json"));
        }
    }

    /// <summary>写 Trace 与本地文件的轻量日志；不依赖宿主日志接口。</summary>
    internal static void Log(string category, string message)
    {
        Trace.WriteLine($"[HistoryApollo:{category}] {message}");
        var root = _dataRoot;
        if (root is null)
            return;
        try
        {
            var directory = Path.Combine(root, "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, $"apollo-{DateTime.Now:yyyyMMdd}.log"),
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
