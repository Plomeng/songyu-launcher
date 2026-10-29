using System.IO;
using System.Net.Http;
using System.Text.Json;
using Songyu.Core.Download;
using Songyu.Core.Minecraft;

namespace Songyu.Services;

/// <summary>
/// 版本安装：拉清单 → 落 version json → 下载 jar / libraries / assets / log 配置。
/// <para>
/// 游戏文件使用 Mojang 提供的地址下载。
/// </para>
/// </summary>
public sealed class MinecraftInstaller
{
    /// <summary>官方资源对象地址：resources.download.minecraft.net/&lt;hash 前两位&gt;/&lt;hash&gt;。</summary>
    private const string ResourcesBase = "https://resources.download.minecraft.net/";

    private readonly HttpClient _http;
    private readonly MinecraftPaths _paths;
    private readonly FileDownloader _downloader;
    private readonly VersionManifestClient _manifestClient;
    private readonly SpeedMeter _meter = new();
    private readonly int _concurrency;
    private long _lastReportAt;

    public MinecraftInstaller(
        HttpClient http,
        MinecraftPaths paths,
        AppConfig? config = null,
        Action<string>? trace = null)
    {
        _http = http;
        _paths = paths;
        _concurrency = Math.Max(1, config?.Concurrency ?? 8);
        _downloader = new FileDownloader(http) { Trace = trace };
        _manifestClient = new VersionManifestClient(http);
    }

    public async Task<string> InstallAsync(
        string versionId,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        Report(progress, new InstallProgress("获取版本清单", 0, 0, null));

        VersionManifest manifest;
        try
        {
            manifest = await _manifestClient.GetManifestAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            throw new InvalidOperationException(NetworkHint, ex);
        }

        var version = manifest.Versions.FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.Ordinal))
                      ?? throw new InvalidOperationException($"版本清单里没有找到 {versionId}。");

        Report(progress, new InstallProgress("获取版本信息", 0, 0, null));
        var jsonText = await _http.GetStringAsync(version.Url, cancellationToken);
        var versionDir = _paths.VersionDir(versionId);
        Directory.CreateDirectory(versionDir);
        await File.WriteAllTextAsync(_paths.VersionJsonPath(versionId), jsonText, cancellationToken);

        var json = JsonSerializer.Deserialize<VersionJson>(jsonText, SongyuJson.Options)
                   ?? throw new InvalidOperationException($"版本 {versionId} 的 JSON 解析失败。");

        var context = RuleContext.Current();

        // ── 阶段 1：客户端 jar、依赖库、资源索引、日志配置 ──
        var stageOne = new List<ResourceSpec>();

        if (json.Downloads?.Client is { Url.Length: > 0 } client)
        {
            stageOne.Add(new ResourceSpec(
                DownloadKind.ClientJar, versionId + ".jar", client.Url, client.Sha1,
                _paths.VersionJarPath(versionId), client.Size));
        }

        stageOne.AddRange(CollectLibraries(json, context));

        string? assetIndexPath = null;
        if (json.AssetIndex is { Url.Length: > 0 } assetIndex)
        {
            assetIndexPath = Path.Combine(_paths.AssetsDir, "indexes", assetIndex.Id + ".json");
            stageOne.Add(new ResourceSpec(
                DownloadKind.AssetIndex, assetIndex.Id + ".json", assetIndex.Url, assetIndex.Sha1,
                assetIndexPath, assetIndex.Size));
        }

        if (json.Logging?.Client?.File is { Url.Length: > 0 } logFile)
        {
            stageOne.Add(new ResourceSpec(
                DownloadKind.LogConfig, logFile.Id + ".xml", logFile.Url, logFile.Sha1,
                Path.Combine(_paths.AssetsDir, "log_configs", logFile.Id + ".xml"), logFile.Size));
        }

        await RunStageAsync(stageOne, "下载客户端与依赖", progress, cancellationToken);

        // ── 阶段 2：资源对象（依赖索引已落地）──
        if (assetIndexPath is not null && File.Exists(assetIndexPath))
        {
            var indexJson = await File.ReadAllTextAsync(assetIndexPath, cancellationToken);
            var index = JsonSerializer.Deserialize<AssetIndexJson>(indexJson, SongyuJson.Options);

            if (index is not null)
            {
                var assetJobs = index.Objects.Values
                    .DistinctBy(o => o.Hash)
                    .Select(o => new ResourceSpec(
                        DownloadKind.Asset,
                        o.Hash[..2] + "/" + o.Hash,
                        ResourcesBase + o.Hash[..2] + "/" + o.Hash,
                        o.Hash,
                        Path.Combine(_paths.AssetsDir, "objects", o.Hash[..2], o.Hash),
                        o.Size))
                    .ToList();

                await RunStageAsync(assetJobs, "下载资源文件", progress, cancellationToken);
            }
        }

        Report(progress, new InstallProgress("安装完成", 1, 1, versionId, _meter.BytesPerSecond));
        return versionDir;
    }

    private List<ResourceSpec> CollectLibraries(VersionJson json, RuleContext context)
    {
        var jobs = new List<ResourceSpec>();

        foreach (var library in json.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, context))
            {
                continue;
            }

            if (LibraryResolver.Resolve(library) is { } artifact)
            {
                jobs.Add(new ResourceSpec(
                    DownloadKind.Library,
                    artifact.RelativePath,
                    artifact.Url,
                    artifact.Sha1,
                    Path.Combine(_paths.LibrariesDir, Normalize(artifact.RelativePath)),
                    artifact.Size));
            }

            if (library.Natives is not null &&
                library.Natives.TryGetValue(context.OsName, out var classifierTemplate))
            {
                var classifier = classifierTemplate.Replace("${arch}", context.Arch == "x86" ? "32" : "64");
                if (library.Downloads?.Classifiers is { } classifiers &&
                    classifiers.TryGetValue(classifier, out var nativeInfo) &&
                    nativeInfo.Url.Length > 0)
                {
                    var relative = nativeInfo.Path ?? MavenCoordinate.ToPath($"{library.Name}:{classifier}");
                    jobs.Add(new ResourceSpec(
                        DownloadKind.Library,
                        relative,
                        nativeInfo.Url,
                        nativeInfo.Sha1,
                        Path.Combine(_paths.LibrariesDir, Normalize(relative)),
                        nativeInfo.Size));
                }
            }
        }

        return jobs;
    }

    private async Task RunStageAsync(
        IReadOnlyList<ResourceSpec> jobs,
        string stage,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
        {
            return;
        }

        Report(progress, new InstallProgress(stage, 0, jobs.Count, null));

        var completed = 0;

        await Parallel.ForEachAsync(
            jobs,
            new ParallelOptions { MaxDegreeOfParallelism = _concurrency, CancellationToken = cancellationToken },
            async (job, token) =>
            {
                await _downloader.DownloadAsync(job, _meter, null, token);
                var done = Interlocked.Increment(ref completed);

                Report(progress, new InstallProgress(
                    stage, done, jobs.Count, job.DisplayName, _meter.BytesPerSecond));
            });
    }

    /// <summary>
    /// 进度节流：文件下载很快时每完成一个就报会刷屏，这里保证 200ms 内最多一条。
    /// 最后一个文件必须报，否则进度条停在 99%。
    /// </summary>
    private void Report(IProgress<InstallProgress>? progress, InstallProgress payload)
    {
        if (progress is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        var isFinal = payload.Total > 0 && payload.Completed >= payload.Total;

        if (!isFinal && now - Interlocked.Read(ref _lastReportAt) < 200)
        {
            return;
        }

        Interlocked.Exchange(ref _lastReportAt, now);
        progress.Report(payload);
    }

    /// <summary>
    /// 给已存在的版本（例如 Fabric 生成的加载器版本）补齐它自己新增的依赖库。
    /// 加载器版本没有 client jar，jar 来自它继承的原版，所以这里只处理 libraries。
    /// </summary>
    public async Task EnsureLibrariesAsync(
        string versionId,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await new VersionResolver(_paths).ResolveAsync(versionId, cancellationToken);
        var jobs = CollectLibraries(resolved.Json, RuleContext.Current());
        await RunStageAsync(jobs, "下载加载器依赖", progress, cancellationToken);
    }

    private static string Normalize(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>
    /// 网络失败时的可操作提示。裸的 TLS/连接异常用户看不懂，也给不出下一步。
    /// </summary>
    public const string NetworkHint =
        "无法连接 Mojang 服务器。请依次检查：\n" +
        "1) 如果你使用代理软件，打开它的「系统代理」开关后重试（程序默认跟随系统代理）；\n" +
        "2) 或在 data/config.json 中把 \"ProxyMode\" 设为 \"Custom\"，并在 \"HttpProxy\" 填入你使用的代理地址。\n" +
        "游戏文件仅从官方地址下载。";
}
