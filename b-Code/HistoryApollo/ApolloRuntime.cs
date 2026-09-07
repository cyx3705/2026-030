using System.Diagnostics;

namespace HistoryApollo;

/// <summary>
/// 模块自有的运行态：数据目录、配置库和日志。
/// </summary>
/// <remarks>
/// 宿主 5.1 起 <c>IModuleContext</c> 只给命令总线和注册器，不给设置、日志或数据根，
/// 所以这些由模块自己持有。落盘位置刻意选运行包的 <c>data/</c>：
/// 按模块 API 合同，升级时宿主保留该目录内容，密钥不会被一次热装抹掉。
/// </remarks>
internal static class ApolloRuntime
{
    private static readonly string ModuleRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HistoryVulcan",
        "Modules",
        "HistoryApollo");

    /// <summary>模块运行态目录；不入 SHA256SUMS，升级时保留。</summary>
    internal static string DataRoot { get; } = Path.Combine(ModuleRoot, "data");

    /// <summary>供应商配置与密钥库。</summary>
    internal static ProviderStore Providers { get; } =
        new(Path.Combine(DataRoot, "providers.json"));

    /// <summary>写 Trace 与本地文件的轻量日志；不依赖宿主日志接口。</summary>
    internal static void Log(string category, string message)
    {
        Trace.WriteLine($"[HistoryApollo:{category}] {message}");
        try
        {
            var directory = Path.Combine(DataRoot, "logs");
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
