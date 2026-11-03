using HistoryVulcan.Core.Commands;

namespace Ratio;

/// <summary>
/// 本机推理服务的管理指令（<c>ratio.local.*</c>）。对话不在这里：<c>ratio.chat.ask provider=local</c>。
/// </summary>
/// <remarks>
/// 服务被 Echo、Codex 和本模块共用，换模、停服对所有客户端生效。停服是 Ask（MCP 不投影，要人确认）；
/// 换模失败时服务自己装回原模型，所以给 Run，AI 按任务需要可以直接换。
/// </remarks>
internal static class RatioLocalCommands
{
    private const string Domain = "ratio";

    public static void Register(ICommandRegistrar registry, LocalService local)
    {
        registry.Register(
            new CommandDescriptor
            {
                Name = "ratio.local.status",
                Domain = Domain,
                CommandClass = "local",
                Summary = "查看本机推理服务：是否在跑、当前模型、可切换模型与换模进度，以及同机其他推理监听。",
                Example = "ratio.local.status",
                Level = CommandLevel.Run,
                Readonly = true,
                Handler = Guard(async context =>
                {
                    var status = await local.StatusAsync(context.Cancellation).ConfigureAwait(false);
                    return CommandResult.Ok(LocalService.Describe(status), LocalService.ToJson(status));
                }),
            });

        registry.Register(
            new CommandDescriptor
            {
                Name = "ratio.local.start",
                Domain = Domain,
                CommandClass = "local",
                Summary = "启动本机推理服务（WSL 里的模型与 OpenAI 兼容代理），装上次选定的模型；已在运行则直接返回。",
                Example = "ratio.local.start",
                Level = CommandLevel.Run,
                Parameters = [TimeoutParameter(600)],
                Handler = Guard(async context =>
                {
                    var (status, started) = await local.StartAsync(context.GetInt("timeout", 600), context.Progress, context.Cancellation)
                        .ConfigureAwait(false);
                    var head = started ? "本机推理服务已启动" : "本机推理服务本来就在运行";
                    return CommandResult.Ok($"{head}{Environment.NewLine}{LocalService.Describe(status)}", LocalService.ToJson(status));
                }),
            });

        registry.Register(
            new CommandDescriptor
            {
                Name = "ratio.local.stop",
                Domain = Domain,
                CommandClass = "local",
                Summary = "停止本机推理服务（先保存 KV 缓存，存盘失败则不停）；共用它的客户端会一起断开。",
                Example = "ratio.local.stop",
                Level = CommandLevel.Ask,
                ConfirmPrompt = _ => "确认停止本机推理服务？Echo、Codex 等共用它的客户端会同时断开。",
                Parameters = [TimeoutParameter(300)],
                Handler = Guard(async context =>
                {
                    var status = await local.StopAsync(context.GetInt("timeout", 300), context.Progress, context.Cancellation)
                        .ConfigureAwait(false);
                    return CommandResult.Ok(
                        status.Running ? $"停止脚本已执行，但代理仍在答话{Environment.NewLine}{LocalService.Describe(status)}" : "本机推理服务已停止",
                        LocalService.ToJson(status));
                }),
            });

        registry.Register(
            new CommandDescriptor
            {
                Name = "ratio.local.use",
                Domain = Domain,
                CommandClass = "local",
                Summary = "把本机推理服务换成另一个已登记的模型，等到就绪再返回；换模期间所有客户端的新请求暂停，失败时服务自动装回原模型。",
                Example = "ratio.local.use qwen3.5:35b-a3b-q4_K_M",
                Level = CommandLevel.Run,
                Parameters =
                [
                    new ParameterSpec
                    {
                        Name = "name",
                        Description = "模型名，取自 ratio.local.status 的可切换模型。",
                        Required = true,
                        Position = 0,
                    },
                    TimeoutParameter(600),
                ],
                Handler = Guard(async context =>
                {
                    var status = await local.SwitchAsync(
                        context.RequireString("name"),
                        context.GetInt("timeout", 600),
                        context.Progress,
                        context.Cancellation).ConfigureAwait(false);
                    return CommandResult.Ok(
                        $"本机推理服务当前模型：{status.Current}{Environment.NewLine}{LocalService.Describe(status)}",
                        LocalService.ToJson(status));
                }),
            });
    }

    private static ParameterSpec TimeoutParameter(int seconds) => new()
    {
        Name = "timeout",
        Description = "最多等多少秒（1-1800）。",
        Type = ParamType.Int,
        Default = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static Func<CommandContext, Task<CommandResult>> Guard(Func<CommandContext, Task<CommandResult>> handler)
        => async context =>
        {
            try
            {
                return await handler(context).ConfigureAwait(false);
            }
            catch (RatioInputException ex)
            {
                return CommandResult.Fail(ex.Message);
            }
            catch (RatioRemoteException ex)
            {
                RatioRuntime.Log("local", $"失败：{ex.Message}");
                return CommandResult.Fail(ex.Message);
            }
        };
}
