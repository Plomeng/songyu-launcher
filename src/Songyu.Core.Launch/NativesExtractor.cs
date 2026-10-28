using System.IO.Compression;
using Songyu.Core.Minecraft;

namespace Songyu.Core.Launch;

/// <summary>
/// 把 libraries 里声明为 natives 的归档解压到独立目录。
/// <para>
/// 两个容易踩的点：一是 classifier 里的 <c>${arch}</c> 在 32 位下是 "32"、64 位下是 "64"；
/// 二是必须跳过 META-INF，否则签名文件会让某些库加载异常。
/// </para>
/// </summary>
public static class NativesExtractor
{
    public static async Task<int> ExtractAsync(
        IEnumerable<Library> libraries,
        RuleContext context,
        string librariesDir,
        string nativesDir,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(nativesDir);

        var extracted = 0;
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (library.Natives is null || library.Natives.Count == 0)
            {
                continue;
            }

            if (!RuleEvaluator.IsAllowed(library.Rules, context))
            {
                continue;
            }

            if (!library.Natives.TryGetValue(context.OsName, out var classifierTemplate))
            {
                continue;
            }

            var classifier = classifierTemplate.Replace("${arch}", context.Arch == "x86" ? "32" : "64");

            if (library.Downloads?.Classifiers is null ||
                !library.Downloads.Classifiers.TryGetValue(classifier, out var info))
            {
                continue;
            }

            var relative = info.Path ?? MavenCoordinate.ToPath($"{library.Name}:{classifier}");
            var jarPath = Path.Combine(librariesDir, relative);
            if (!File.Exists(jarPath))
            {
                continue;
            }

            extracted += await ExtractArchiveAsync(jarPath, nativesDir, cancellationToken);
        }

        return extracted;
    }

    private static Task<int> ExtractArchiveAsync(string jarPath, string targetDir, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var count = 0;
            using var archive = ZipFile.OpenRead(jarPath);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("META-INF\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 目录项
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                var destination = Path.Combine(targetDir, entry.FullName);
                var parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                entry.ExtractToFile(destination, overwrite: true);
                count++;
            }

            return count;
        }, cancellationToken);
}
