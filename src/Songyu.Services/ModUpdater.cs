using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Songyu.Services;

/// <summary>一次更新检查的结果摘要。</summary>
public sealed record UpdateCheckSummary(int Total, int Updatable, GameContext Context);

/// <summary>
/// Mod 更新检查与应用。
/// <para>
/// 检查用 Modrinth 的 <c>POST /v2/version_files/update_many</c>：
/// 传一批已装文件的 sha1 + 实例的加载器/MC 版本，返回"有新版本的那些"。
/// 结果写回本地索引，界面直接读。
/// </para>
/// <para>
/// 应用更新是"先下到临时文件、校验 sha1、再挪走旧文件"，
/// 任何一步失败都不动现有文件 —— 更新失败不该把玩家的实例搞坏。
/// </para>
/// </summary>
public sealed class ModUpdater(
    HttpClient http,
    MinecraftPaths paths,
    ContentIndex index,
    ModrinthClient modrinth)
{
    /// <summary>
    /// 检查一个实例里所有 Mod 的更新。
    /// 原版实例（没有加载器）直接返回，因为 Modrinth 的 loader 过滤会一个都匹配不上。
    /// </summary>
    public async Task<UpdateCheckSummary> CheckAsync(
        string instanceId,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var context = GameContextResolver.Resolve(paths, versionId);

        var modFiles = index.GetFiles(instanceId)
            .Where(f => f.Kind == ContentKind.Mod && !f.Missing)
            .ToList();

        var sha1s = modFiles.Select(f => f.Sha1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sha1s.Count == 0)
        {
            return new UpdateCheckSummary(0, 0, context);
        }

        // 版本信息缺失的实例（比如整合包导入留下的历史目录）：
        // 从 Mod 文件本身推断加载器和 MC 版本，总比直接说"查不了"有用。
        var loader = context.Loader ?? InferLoader(modFiles);
        var mcVersion = context.McVersion ?? InferMcVersion(modFiles);

        if (loader is null)
        {
            throw new InvalidOperationException(
                "没法确定这个实例用的是哪个加载器，因此没法查更新。\n\n" +
                "通常是因为它指向的版本没有安装（找不到版本 json），" +
                "Mod 文件里也读不出加载器信息。");
        }

        var effectiveContext = new GameContext(mcVersion, loader);

        // 当前版本号：用来排除"API 把文件自己又返回一遍"的情况
        var currentBySha1 = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in modFiles)
        {
            currentBySha1.TryAdd(file.Sha1, file.VersionId);
        }

        var updates = await modrinth.CheckUpdatesAsync(
            sha1s,
            effectiveContext.Loaders,
            mcVersion is { Length: > 0 } ? [mcVersion] : [],
            cancellationToken);

        var map = new Dictionary<string, UpdateInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var (sha1, version) in updates)
        {
            // update_many 会把"本来就是最新"的文件也返回一次（就是它自己），
            // 版本 ID 相同就不算更新 —— Modrinth 官方客户端也是这么过滤的。
            if (currentBySha1.TryGetValue(sha1, out var current) &&
                string.Equals(current, version.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            map[sha1] = new UpdateInfo(version.Id, version.VersionNumber);
        }

        index.ApplyUpdateChecks(instanceId, map);

        return new UpdateCheckSummary(sha1s.Count, map.Count, effectiveContext);
    }

    /// <summary>从已装 Mod 的元数据里取出现次数最多的加载器。</summary>
    private static string? InferLoader(IReadOnlyList<IndexedFile> mods)
        => mods.Select(m => m.ModLoader)
            .Where(l => !string.IsNullOrWhiteSpace(l) && l != "unknown")
            .GroupBy(l => l!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

    /// <summary>
    /// 从文件名里猜 MC 版本（形如 1.20.1 / 1.19 / 1.21.4），取出现最多的那个。
    /// 只在版本 json 缺失时兜底用，准确度靠"大多数 Mod 文件名都带版本号"这一点。
    /// </summary>
    private static string? InferMcVersion(IReadOnlyList<IndexedFile> mods)
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            @"(?<!\d)(1\.\d{1,2}(?:\.\d{1,2})?)(?!\d)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        return mods
            .Select(m => pattern.Match(m.FileName))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
    }

    /// <summary>
    /// 把一个文件更新到 <paramref name="targetVersionId"/>。
    /// 返回新文件路径；旧文件挪进 mods/.trash。
    /// </summary>
    public async Task<string> ApplyAsync(
        IndexedFile file,
        string targetVersionId,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var version = await modrinth.GetVersionAsync(targetVersionId, cancellationToken);
        var target = version.PrimaryFile
                     ?? throw new InvalidOperationException($"{version.VersionNumber} 没有可下载的文件。");

        var modsDirectory = Path.GetDirectoryName(file.FullPathFor(paths))
                            ?? paths.InstanceModsDir(file.InstanceId);
        Directory.CreateDirectory(modsDirectory);

        var tempDirectory = Path.Combine(modsDirectory, ".tmp");
        Directory.CreateDirectory(tempDirectory);

        var tempPath = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.part");

        try
        {
            await DownloadAsync(target, tempPath, progress, cancellationToken);

            // 有 sha1 就校验；不匹配说明下载被截断或换了内容，宁可失败也不装
            if (!string.IsNullOrEmpty(target.Sha1))
            {
                var actual = ContentIndex.ComputeSha1(tempPath, cancellationToken);
                if (!string.Equals(actual, target.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"下载的文件校验失败（sha1 不匹配），已放弃这次更新。\n" +
                        $"期望 {target.Sha1}\n实际 {actual}");
                }
            }

            // 旧文件先挪走（进 .trash），再把新文件放到目标位置
            if (File.Exists(file.FullPathFor(paths)))
            {
                ModScanner.Delete(file.FullPathFor(paths));
            }

            var destination = Path.Combine(modsDirectory, target.Filename);
            if (File.Exists(destination))
            {
                ModScanner.Delete(destination);
            }

            File.Move(tempPath, destination);
            return destination;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // 清临时文件失败不影响结果
            }
        }
    }

    /// <summary>
    /// 批量更新：逐个应用，返回 (成功, 失败列表)。
    /// 单个失败不打断其它 —— 283 个 Mod 里有 3 个源挂了，剩下 280 个应该照常更新。
    /// </summary>
    public async Task<(int Succeeded, IReadOnlyList<string> Failures)> ApplyAllAsync(
        IReadOnlyList<IndexedFile> files,
        IProgress<(int Done, int Total, string Name)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var succeeded = 0;
        var failures = new List<string>();

        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[i];
            progress?.Report((i + 1, files.Count, file.Name));

            try
            {
                await ApplyAsync(file, file.UpdateVersionId!, null, cancellationToken);
                succeeded++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{file.Name}：{ex.Message}");
            }
        }

        return (succeeded, failures);
    }

    private async Task DownloadAsync(
        ModFile file,
        string targetPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? file.Size;
        var buffer = new byte[81920];
        long received = 0;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(targetPath);

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            received += read;
            if (total > 0)
            {
                progress?.Report((double)received / total);
            }
        }
    }
}
