using System.Reflection;
using BaseVariable;

namespace HistoryApollo;

/// <summary>
/// 模块身份。宿主按**全名**鸭子类型识别 <c>BaseVariable.ModuleInfoBase</c> 的公开非抽象子类，
/// 不靠引用相等，所以这个类必须存在且可被反射构造。
/// </summary>
public sealed class ModuleInfo : ModuleInfoBase
{
    /// <summary>
    /// 必须显式覆盖：缺省值是程序集名，而它同时是**指令域**。
    /// 程序集名与域一旦分家，域就会跟着程序集名跑。
    /// </summary>
    public override string ModuleName => "HistoryApollo";

    /// <summary>指令域前缀。</summary>
    public string CommandPrefix => "apollo";

    public override string Description => "模型调用：把 DeepSeek 等 OpenAI 兼容模型接成宿主指令";

    public override string Author => "OneHistory";

    /// <summary>
    /// 版本取自程序集而非字面量：宿主装载时比对 manifest 与本类的版本，
    /// 两者不一致会**静默跳过整个模块**，只在日志留一行 module.discovery 警告。
    /// </summary>
    public override string Version { get; } = ReadAssemblyVersion();

    /// <summary>指令一律经 <c>IModuleContext.RegisterCommands</c> 注册，不走反射投影。</summary>
    public override Type? MainClassType => null;

    private static string ReadAssemblyVersion()
    {
        var assembly = typeof(ModuleInfo).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
