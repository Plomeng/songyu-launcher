using System.IO;
using System.Net.Http;
using System.Text.Json;
using Songyu.Core.Download;
using Songyu.Core.Minecraft;

namespace Songyu.Services;

public sealed record ModInstallResult(IReadOnlyList<string> InstalledFiles);

/// <summary>
/// 把 Modrinth 的 Mod 装进某个实例的 mods 目录，并递归补齐 required 依赖。
/// <para>
/// 依赖的版本选择用「该实例的游戏版本 + 加载器」过滤后取最新一个，
/// 和 Modrinth 网页上的默认行为一致。已经有同名文件就跳过，重复安装是幂等的。
/// </para>
/// </summary>
public sealed class ModInstaller(HttpClient http, MinecraftPaths paths, ModrinthClient modrinth)
{
    private const int MaxAttempts = 3;

    public async Task<ModInstallResult> InstallAsync(
        string instanceId,
        ModVersion version,
        IProgress<string>? log = null,
        IProgress<DownloadProgress>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        var context = ResolveContext(instanceId);
        if (context.McVersion is null)
        {
            throw new InvalidOperationException(
                $"{instanceId} 是原版实例，不能装 Mod。请先安装 Fabric / Quilt / Forge 版本。");
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var installed = new List<string>();

        await InstallRecursiveAsync(instanceId, version, context, visited, installed, log, downloadProgress, cancellationToken);

        return new ModInstallResult(installed);
    }

    private async Task InstallRecursiveAsync(
        string instanceId,
        ModVersion version,
        GameContext context,
        HashSet<string> visited,
        List<string> installed,
        IProgress<string>? log,
        IProgress<DownloadProgress>? downloadProgress,
        CancellationToken cancellationToken)
    {
        if (!visited.Add(version.Id))
        {
            return;
        }

        var file = version.PrimaryFile;
        if (file is null)
        {
            return;
        }

        var modsDirectory = Path.Combine(paths.InstancesDir, instanceId, "mods");
        Directory.CreateDirectory(modsDirectory);

        var targetPath = Path.Combine(modsDirectory, file.Filename);
        if (!File.Exists(targetPath))
        {
            log?.Report($"从 {SourceHost(file.Url)} 下载 {file.Filename}");
            await DownloadWithRetryAsync(file.Url, targetPath, downloadProgress, log, cancellationToken);
            installed.Add(file.Filename);
        }

        foreach (var dependency in version.Dependencies)
        {
            if (dependency.DependencyType != "required" || string.IsNullOrEmpty(dependency.ProjectId))
            {
                continue;
            }

            var candidates = await modrinth.GetVersionsAsync(
                dependency.ProjectId, context.McVersion, context.Loader, cancellationToken);

            var best = candidates.FirstOrDefault();
            if (best is null)
            {
                log?.Report($"跳过依赖 {dependency.ProjectId}（没有适配 {context.McVersion} 的版本）");
                continue;
            }

            await InstallRecursiveAsync(instanceId, best, context, visited, installed, log, downloadProgress, cancellationToken);
        }
    }

    /// <summary>从版本 json 推出这个实例对应的游戏版本与加载器。</summary>
    private GameContext ResolveContext(string instanceId)
    {
        var jsonPath = paths.VersionJsonPath(instanceId);
        if (!File.Exists(jsonPath))
        {
            return new GameContext(null, null);
        }

        var json = JsonSerializer.Deserialize<VersionJson>(File.ReadAllText(jsonPath), SongyuJson.Options);
        if (json is null)
        {
            return new GameContext(null, null);
        }

        // 加载器版本的 inheritsFrom 指向原版；原版自己就是游戏版本
        var mcVersion = string.IsNullOrEmpty(json.InheritsFrom) ? json.Id : json.InheritsFrom;
        var loader = DetectLoader(json.Id);

        return new GameContext(mcVersion, loader);
    }

    private static string? DetectLoader(string versionId)
    {
        if (versionId.StartsWith("fabric-loader", StringComparison.OrdinalIgnoreCase))
        {
            return "fabric";
        }

        if (versionId.StartsWith("quilt-loader", StringComparison.OrdinalIgnoreCase))
        {
            return "quilt";
        }

        if (versionId.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
        {
            return "neoforge";
        }

        if (versionId.Contains("forge", StringComparison.OrdinalIgnoreCase))
        {
            return "forge";
        }

        return null;
    }

    /// <summary>
    /// 带重试的下载。Mod 卡住基本上是 CDN 那一侧的问题，
    /// 重试之间退避一下通常就能过；三次都失败才把错误抛给界面。
    /// </summary>
    private async Task DownloadWithRetryAsync(
        string url,
        string targetPath,
        IProgress<DownloadProgress>? progress,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DownloadOnceAsync(url, targetPath, progress, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < MaxAttempts)
            {
                log?.Report($"第 {attempt} 次下载失败（{ex.Message}），{attempt * 2} 秒后重试…");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }
    }

    private async Task DownloadOnceAsync(
        string url,
        string targetPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var host = SourceHost(url);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"从 {host} 下载失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var total = response.Content.Headers.ContentLength;
        var fileName = Path.GetFileName(targetPath);
        var meter = new SpeedMeter();
        var buffer = new byte[81920];
        long received = 0;

        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(targetPath);

            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                received += read;
                meter.Add(read);
                progress?.Report(new DownloadProgress(received, total, meter.BytesPerSecond, fileName));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 传输中断也要把来源说出来，否则用户只看到一句 "下载失败"
            throw new HttpRequestException($"从 {host} 下载中断：{ex.Message}", ex);
        }
    }

    /// <summary>取出下载地址的域名，用来在界面上标明「这个文件是从哪来的」。</summary>
    private static string SourceHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "未知来源";

    private sealed record GameContext(string? McVersion, string? Loader);
}
