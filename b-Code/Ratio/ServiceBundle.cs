namespace Ratio;

/// <summary>随 DLL 发布的一份服务文件：运行区里的相对路径与原始字节。</summary>
internal sealed record ServiceFile(string RelativePath, byte[] Content);

/// <summary>
/// 本机推理服务的源码包（0.7.0）：仓里 <c>b-Code-Service/</c> 的脚本与配置，以嵌入资源随 Ratio.dll 发布。
/// </summary>
/// <remarks>
/// 运行区（默认 <c>F:\ModuleServicesData\Ratio</c>）里的脚本只从这里来：<see cref="Deploy"/> 只写内容变了的文件，
/// 从不删除运行区里的东西——编译产物、CUDA 库、KV 快照、状态和日志都是运行区自己的，与源码包无关。
/// 字节原样写出：shell 脚本必须是 LF，仓里用 <c>-text</c> 钉住，这里也不做任何换行转换。
/// </remarks>
internal static class ServiceBundle
{
    private const string Prefix = "service/";

    /// <summary>读出本程序集里的全部服务文件。</summary>
    public static IReadOnlyList<ServiceFile> Load()
    {
        var assembly = typeof(ServiceBundle).Assembly;
        var files = new List<ServiceFile>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(Prefix, StringComparison.Ordinal) || normalized.StartsWith("service/native/bin/", StringComparison.Ordinal))
                continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            files.Add(new ServiceFile(normalized[Prefix.Length..], buffer.ToArray()));
        }

        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList();
    }

    /// <summary>逐文件流式解出大型原生依赖，避免为 CUDA 库构造多份 byte[]。</summary>
    internal static void DeployNative(string root)
    {
        var assembly = typeof(ServiceBundle).Assembly;
        const string nativePrefix = "service/native/bin/";
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(nativePrefix, StringComparison.Ordinal)) continue;
            var filename = normalized[nativePrefix.Length..];
            if (Path.GetFileName(filename) != filename) throw new RatioInputException("原生依赖文件名无效");
            var target = Path.Combine(root, "native", "bin", filename);
            using var source = assembly.GetManifestResourceStream(name)!;
            if (File.Exists(target))
            {
                using var installed = File.OpenRead(target);
                if (installed.Length == source.Length && System.Security.Cryptography.SHA256.HashData(installed).AsSpan()
                    .SequenceEqual(System.Security.Cryptography.SHA256.HashData(source))) continue;
                source.Position = 0;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".ratio-tmp";
            try
            {
                using (var output = File.Create(temporary)) source.CopyTo(output);
                File.Move(temporary, target, true);
            }
            finally { File.Delete(temporary); }
        }
    }

    /// <summary>把服务文件同步到运行区；返回实际写了几个。</summary>
    /// <exception cref="RatioInputException">包里没有任何服务文件，或某个相对路径跳出了运行区。</exception>
    public static int Deploy(string root, IReadOnlyList<ServiceFile> files)
    {
        if (files.Count == 0)
            throw new RatioInputException("Ratio 包里没有本机推理服务文件，无法部署");

        var full = Path.GetFullPath(root);
        var written = 0;
        foreach (var file in files)
        {
            var target = Path.GetFullPath(Path.Combine(full, file.RelativePath));
            if (!target.StartsWith(full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new RatioInputException($"服务文件路径越界：{file.RelativePath}");
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(file.Content))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + ".ratio-tmp";
            File.WriteAllBytes(temporary, file.Content);
            File.Move(temporary, target, overwrite: true);
            written++;
        }

        return written;
    }
}
