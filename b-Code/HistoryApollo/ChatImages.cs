namespace HistoryApollo;

/// <summary>
/// <c>images=</c> 参数（0.5.0）：本机图片文件 → 请求里的 <c>data:</c> URL。
/// </summary>
/// <remarks>
/// <para>只收本机文件路径，不收网址：调用方（例如 HistoryStrenua 的图纸截图）手里本来就是文件；
/// 让模块替调用方去下载网址，等于多开一个出网口子。</para>
/// <para>限额按通义千问兼容接口的口径取保守值：单张 10 MB、一次 8 张。超了直接拒，不偷偷缩图——
/// 缩到多小模型还看得清，只有调用方知道。</para>
/// </remarks>
internal static class ChatImages
{
    /// <summary>一次调用最多附几张。</summary>
    public const int MaxCount = 8;

    /// <summary>单张上限（字节）。</summary>
    public const long MaxBytes = 10L * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> MediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".gif"] = "image/gif",
            [".bmp"] = "image/bmp",
        };

    /// <summary>参数说明（两条 chat 指令共用）。</summary>
    public static string Description =>
        $"随最后一条 user 消息附带的本机图片，绝对路径，多张用 | 分隔（png/jpg/webp/gif/bmp，单张 ≤ {MaxBytes / 1024 / 1024} MB，最多 {MaxCount} 张）；"
        + "给了图片且省略 provider 时改用识图供应商（apollo.provider.list 里标「识图」的那家）。";

    /// <summary>把 <c>a.png|b.jpg</c> 读成 data URL；空参数返回空表。</summary>
    /// <exception cref="ApolloInputException">文件不在、格式不认、太大或太多。</exception>
    public static IReadOnlyList<string> Load(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var paths = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.Trim('"'))
            .Where(path => path.Length > 0)
            .ToList();
        if (paths.Count > MaxCount)
            throw new ApolloInputException($"images 最多 {MaxCount} 张，收到 {paths.Count} 张");

        var images = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (!Path.IsPathFullyQualified(path))
                throw new ApolloInputException($"images 要写绝对路径：{path}");
            if (!MediaTypes.TryGetValue(Path.GetExtension(path), out var mediaType))
                throw new ApolloInputException($"图片格式不认（只收 {string.Join("/", MediaTypes.Keys)}）：{path}");
            var file = new FileInfo(path);
            if (!file.Exists)
                throw new ApolloInputException($"图片不存在：{path}");
            if (file.Length > MaxBytes)
                throw new ApolloInputException($"图片超过 {MaxBytes / 1024 / 1024} MB：{path}");

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ApolloInputException($"图片读不出来：{path}（{ex.Message}）");
            }

            images.Add($"data:{mediaType};base64,{Convert.ToBase64String(bytes)}");
        }

        return images;
    }

    /// <summary>把图片挂到最后一条 user 消息上；没有 user 消息时拒绝（图片总得是谁「发」的）。</summary>
    public static IReadOnlyList<ChatMessage> Attach(IReadOnlyList<ChatMessage> messages, IReadOnlyList<string> images)
    {
        if (images.Count == 0)
            return messages;
        var index = messages.Select((message, position) => (message, position))
            .LastOrDefault(item => item.message.Role == "user", (null!, -1)).position;
        if (index < 0)
            throw new ApolloInputException("给了 images，但消息里没有 user 消息可以附图");

        var result = messages.ToList();
        result[index] = result[index] with { Images = [.. result[index].Images ?? [], .. images] };
        return result;
    }
}
