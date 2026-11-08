using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace Ratio;

/// <summary>一次向量调用的结果；向量按输入顺序。</summary>
internal sealed record EmbedOutcome(
    string Provider,
    string Model,
    int Dimensions,
    IReadOnlyList<float[]> Vectors,
    int TotalTokens,
    long ElapsedMs);

/// <summary>
/// 文本转向量（0.9.0）：给要做语义检索的调用方（Diana 能力地图、Usus 经验库）一个推理原语。
/// </summary>
/// <remarks>
/// 只做「一次调用」：不建索引、不存向量、不算相似度——那些是调用方的事，各自的语料和缓存失效规则只有它们自己知道。
/// 默认走通义千问：DeepSeek 没有向量接口，本机推理服务同一时间只装一个对话模型。
/// </remarks>
internal static class RatioEmbedCommands
{
    /// <summary>省略 provider= 时走这家。</summary>
    public const string DefaultProvider = "qwen";

    /// <summary>内置供应商的默认向量模型；别家必须显式写 model=。</summary>
    private static readonly IReadOnlyDictionary<string, string> DefaultModels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["qwen"] = "text-embedding-v4",
        };

    // 通义 text-embedding-v4 一次最多 10 条；别家按同一批量发也不吃亏。
    private const int BatchSize = 10;
    private const int MaximumTexts = 500;
    private const int MaximumTextLength = 2000;

    public static void Register(ICommandRegistrar registry, ProviderStore store, ChatClient client)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "ratio.embed.texts",
            Domain = "ratio",
            CommandClass = "embed",
            Summary = "把一组文本转成向量，供语义检索用；默认通义千问 text-embedding-v4，按输入顺序返回。",
            Example = "ratio.embed.texts texts=[\"新建项目\",\"创建新项目并初始化仓库\"] dimensions=512",
            // 0.9.0 实调：dimensions=8 被通义 400 拒（只认档位），所以示例写 512。
            Level = CommandLevel.Run,
            // 不写本机，但每次都是计费外网请求，与 chat 指令同理不声明 Readonly。
            Readonly = false,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "texts", Required = true, Position = 0,
                    Description = $"JSON 字符串数组，最多 {MaximumTexts} 条，每条不超过 {MaximumTextLength} 字。",
                },
                new ParameterSpec
                {
                    Name = "provider",
                    Description = "供应商名称；省略时用通义千问 qwen（DeepSeek 没有向量接口）。",
                },
                new ParameterSpec
                {
                    Name = "model",
                    Description = "向量模型；省略时 qwen 用 text-embedding-v4，别的供应商必须写。",
                },
                new ParameterSpec
                {
                    Name = "dimensions", Type = ParamType.Int,
                    Description = "向量维数；省略时按模型默认。text-embedding-v4 默认 1024，只认 64/128/256/512/768/1024/1536/2048 这几档。",
                },
                new ParameterSpec
                {
                    Name = "timeout", Type = ParamType.Int, Default = "120",
                    Description = "每一批请求的超时秒数（1-600）。",
                },
            ],
            Handler = async context =>
            {
                try
                {
                    var texts = ParseTexts(context.GetString("texts"));
                    var profile = store.ResolveChat(context.GetString("provider") is { Length: > 0 } named ? named : DefaultProvider);
                    var model = context.GetString("model") is { Length: > 0 } explicitModel
                        ? explicitModel.Trim()
                        : DefaultModels.GetValueOrDefault(profile.Name)
                          ?? throw new RatioInputException($"{profile.Name} 没有内置向量模型，请写 model=");
                    int? dimensions = context.GetString("dimensions") is { Length: > 0 } ? context.GetInt("dimensions") : null;
                    if (dimensions is < 1 or > 4096)
                        throw new RatioInputException("dimensions 必须在 1~4096 之间");

                    var outcome = await client.EmbedAsync(
                        profile, model, texts, dimensions, BatchSize, context.GetInt("timeout", 120), context.Cancellation).ConfigureAwait(false);
                    RatioRuntime.Log("embed", $"{outcome.Provider}/{outcome.Model} {texts.Count} 条 {outcome.TotalTokens} tokens {outcome.ElapsedMs}ms");
                    // 正文不放向量：几百条上千维的数字进控制台只会刷屏，向量只在 Data 里。
                    return CommandResult.Ok(
                        $"{outcome.Provider}/{outcome.Model}：{texts.Count} 条 → {outcome.Dimensions} 维向量，用量 {outcome.TotalTokens} tokens，{outcome.ElapsedMs} ms",
                        JsonSerializer.Serialize(new
                        {
                            provider = outcome.Provider,
                            model = outcome.Model,
                            dimensions = outcome.Dimensions,
                            vectors = outcome.Vectors,
                            usage = new { totalTokens = outcome.TotalTokens },
                            elapsedMs = outcome.ElapsedMs,
                        }));
                }
                catch (RatioInputException ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
                catch (RatioRemoteException ex)
                {
                    RatioRuntime.Log("embed", $"调用失败：{ex.Message}");
                    return CommandResult.Fail(ex.Message);
                }
            },
        });
    }

    internal static List<string> ParseTexts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new RatioInputException("texts 不能为空");
        List<string>? texts;
        try
        {
            texts = JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException ex)
        {
            throw new RatioInputException($"texts 必须是 JSON 字符串数组：{ex.Message}");
        }

        if (texts is null || texts.Count == 0)
            throw new RatioInputException("texts 至少要有一条");
        if (texts.Count > MaximumTexts)
            throw new RatioInputException($"texts 最多 {MaximumTexts} 条，实际 {texts.Count} 条；请分次调用");
        for (var i = 0; i < texts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(texts[i]))
                throw new RatioInputException($"texts 第 {i + 1} 条是空的");
            if (texts[i].Length > MaximumTextLength)
                throw new RatioInputException($"texts 第 {i + 1} 条 {texts[i].Length} 字，超过 {MaximumTextLength}");
        }

        return texts;
    }
}
