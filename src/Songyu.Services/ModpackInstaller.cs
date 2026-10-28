using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Songyu.Core.Download;

namespace Songyu.Services;

public enum ModpackFormat
{
    /// <summary>Modrinth 的 .mrpack，内含 modrinth.index.json。</summary>
    Modrinth,

    /// <summary>CurseForge 导出，内含 manifest.json（需要 API Key 才能拿下载地址）。</summary>
    CurseForge,

    /// <summary>普通压缩包，直接当 overrides 解压。</summary>
    PlainZip,
}

public sealed record ModpackInfo(
    string Name,
    string Version,
    string McVersion,
    string? LoaderKind,
    string? LoaderVersion,
    int FileCount,
    ModpackFormat Format)
{
    public string FormatText => Format switch
    {
        ModpackFormat.Modrinth => "Modrinth (.mrpack)",
        ModpackFormat.CurseForge => "CurseForge (manifest.json)",
        _ => "普通压缩包",
    };
}

/// <summary>落盘在 <c>instances/&lt;包名&gt;/modpack.json</c>，记录这个实例来自哪个整合包。</summary>
public sealed record ModpackRecord(
    string Name,
    string Version,
    string McVersion,
    string? LoaderKind,
    string? LoaderVersion,
    int FileCount,
    ModpackFormat Format,
    string VersionId)
{
    public string FormatText => Format switch
    {
        ModpackFormat.Modrinth => "Modrinth (.mrpack)",
        ModpackFormat.CurseForge => "CurseForge",
        _ => "普通压缩包",
    };
}

/// <summary>
/// 整合包解析与安装。
/// <para>
/// 支持三种：
/// <list type="bullet">
/// <item>Modrinth <c>.mrpack</c>：index 里带每个文件的直链，可以完整安装</item>
/// <item>CurseForge <c>.zip</c>：manifest 只给 projectID/fileID，拿下载地址要 API Key，
///       这里解析出元信息并安装 overrides，Mod 本体提示需要 Key</item>
/// <item>普通压缩包：直接解压到实例目录</item>
/// </list>
/// </para>
/// </summary>
public sealed class ModpackInstaller(
    HttpClient http,
    MinecraftPaths paths,
    AppConfig config,
    ContentIndex? contentIndex = null)
{
    /// <summary>
    /// 文件下载统一走核心下载器：它自带 .part 续传、SHA1 校验、失败重试与多源候选，
    /// 整合包这边不需要再自己写一份（自写的既没有续传也不校验）。
    /// </summary>
    private readonly FileDownloader _downloader = new(http);

    /// <summary>同时下载的文件数。整合包文件通常几十个，串行会明显拖慢。</summary>
    private readonly int _concurrency = Math.Max(1, config?.Concurrency ?? 8);

    private long _lastReportAt;

    public async Task<ModpackInfo> InspectAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        using var archive = ZipFile.OpenRead(archivePath);

        if (archive.GetEntry("modrinth.index.json") is { } modrinthIndex)
        {
            return ParseModrinth(modrinthIndex);
        }

        if (archive.GetEntry("manifest.json") is { } curseManifest)
        {
            return ParseCurseForge(curseManifest, archivePath);
        }

        return new ModpackInfo(
            Path.GetFileNameWithoutExtension(archivePath),
            string.Empty,
            string.Empty,
            null,
            null,
            0,
            ModpackFormat.PlainZip);
    }

    /// <summary>
    /// 安装整合包，返回最终的版本 ID（用于启动页展示）。
    /// <para>调用方负责先确保 Java 可用、并把进度接出去。</para>
    /// </summary>
    public async Task<string> InstallAsync(
        string archivePath,
        string javaPath,
        IProgress<string>? log = null,
        IProgress<InstallProgress>? progress = null,
        IProgress<DownloadProgress>? downloadProgress = null,
        string? instanceId = null,
        CancellationToken cancellationToken = default)
    {
        var info = await InspectAsync(archivePath, cancellationToken);
        var id = SanitizeInstanceId(string.IsNullOrWhiteSpace(instanceId) ? info.Name : instanceId, archivePath);
        var instanceDirectory = Path.Combine(paths.InstancesDir, id);

        Directory.CreateDirectory(instanceDirectory);

        log?.Report($"整合包：{info.Name}（{info.FormatText}）");

        // 1. 装游戏本体与加载器
        var versionId = await EnsureLoaderAsync(info, javaPath, log, progress, cancellationToken);

        // 2. 按格式处理文件
        using var archive = ZipFile.OpenRead(archivePath);

        switch (info.Format)
        {
            case ModpackFormat.Modrinth:
                await InstallModrinthFilesAsync(archive, instanceDirectory, log, progress, downloadProgress, cancellationToken);
                break;

            case ModpackFormat.CurseForge:
                log?.Report("CurseForge 整合包：Mod 本体需要 API Key，先安装配置与资源");
                break;
        }

        // 3. overrides 解压（两种格式都有）
        ExtractOverrides(archive, info.Format, instanceDirectory, log);

        // 记录元信息，供「整合包」页展示与再次启动
        var record = new ModpackRecord(
            info.Name, info.Version, info.McVersion, info.LoaderKind, info.LoaderVersion,
            info.FileCount, info.Format, versionId);

        await File.WriteAllTextAsync(
            Path.Combine(instanceDirectory, "modpack.json"),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        log?.Report($"整合包已就绪：{versionId}");
        return versionId;
    }

    // ── 解析 ──

    private static ModpackInfo ParseModrinth(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var name = ReadString(root, "name");
        var version = ReadString(root, "versionId");
        var (mc, loaderKind, loaderVersion) = ReadDependencies(root);

        var fileCount = root.TryGetProperty("files", out var files) ? files.GetArrayLength() : 0;

        return new ModpackInfo(name, version, mc, loaderKind, loaderVersion, fileCount, ModpackFormat.Modrinth);
    }

    private static ModpackInfo ParseCurseForge(ZipArchiveEntry entry, string archivePath)
    {
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var name = ReadString(root, "name");
        var version = ReadString(root, "version");

        var mc = string.Empty;
        string? loaderKind = null;
        string? loaderVersion = null;

        if (root.TryGetProperty("minecraft", out var minecraft))
        {
            mc = ReadString(minecraft, "version");

            if (minecraft.TryGetProperty("modLoaders", out var loaders))
            {
                foreach (var loader in loaders.EnumerateArray())
                {
                    // 形如 "forge-47.2.0"、"fabric-0.15.0"
                    var id = ReadString(loader, "id");
                    var dash = id.IndexOf('-');

                    if (dash > 0)
                    {
                        loaderKind = id[..dash];
                        loaderVersion = id[(dash + 1)..];
                        break;
                    }
                }
            }
        }

        var fileCount = root.TryGetProperty("files", out var files) ? files.GetArrayLength() : 0;

        return new ModpackInfo(
            string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(archivePath) : name,
            version,
            mc,
            loaderKind,
            loaderVersion,
            fileCount,
            ModpackFormat.CurseForge);
    }

    private static (string Mc, string? Kind, string? Version) ReadDependencies(JsonElement root)
    {
        if (!root.TryGetProperty("dependencies", out var dependencies))
        {
            return (string.Empty, null, null);
        }

        var mc = ReadString(dependencies, "minecraft");

        foreach (var (key, kind) in new[]
                 {
                     ("fabric-loader", "fabric"),
                     ("quilt-loader", "quilt"),
                     ("neoforge", "neoforge"),
                     ("forge", "forge"),
                 })
        {
            if (dependencies.TryGetProperty(key, out var value) && value.GetString() is { Length: > 0 } version)
            {
                return (mc, kind, version);
            }
        }

        return (mc, null, null);
    }

    private static string ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    // ── 安装 ──

    private async Task<string> EnsureLoaderAsync(
        ModpackInfo info,
        string javaPath,
        IProgress<string>? log,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var installer = new MinecraftInstaller(http, paths, config);

        if (string.IsNullOrEmpty(info.McVersion))
        {
            throw new InvalidOperationException("整合包没有写明 Minecraft 版本，无法安装。");
        }

        log?.Report($"安装原版 {info.McVersion}…");
        await installer.InstallAsync(info.McVersion, progress, cancellationToken);

        if (string.IsNullOrEmpty(info.LoaderKind))
        {
            return info.McVersion;
        }

        switch (info.LoaderKind)
        {
            case "fabric":
            case "quilt":
            {
                var meta = new MetaLoaderInstaller(
                    http,
                    paths,
                    info.LoaderKind == "fabric" ? "https://meta.fabricmc.net/v2" : "https://meta.quiltmc.org/v3",
                    info.LoaderKind == "fabric" ? "Fabric" : "Quilt");

                log?.Report($"安装 {meta.DisplayName} {info.LoaderVersion}…");
                var versionId = await meta.InstallAsync(info.McVersion, info.LoaderVersion, cancellationToken);

                await installer.EnsureLibrariesAsync(versionId, progress, cancellationToken);
                return versionId;
            }

            case "forge":
            {
                log?.Report("安装 Forge（会运行官方安装器）…");
                var forge = new ForgeInstaller(http, paths, config);
                var versionId = await forge.InstallAsync(info.McVersion, javaPath, log, cancellationToken);

                await installer.EnsureLibrariesAsync(versionId, progress, cancellationToken);
                return versionId;
            }

            case "neoforge":
            {
                log?.Report("安装 NeoForge（会运行官方安装器）…");
                var neo = new NeoForgeInstaller(http, paths, config);
                var versionId = await neo.InstallAsync(info.McVersion, javaPath, log, cancellationToken);

                await installer.EnsureLibrariesAsync(versionId, progress, cancellationToken);
                return versionId;
            }

            default:
                throw new InvalidOperationException($"整合包要求不支持的加载器：{info.LoaderKind}");
        }
    }

    private async Task InstallModrinthFilesAsync(
        ZipArchive archive,
        string instanceDirectory,
        IProgress<string>? log,
        IProgress<InstallProgress>? progress,
        IProgress<DownloadProgress>? downloadProgress,
        CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry("modrinth.index.json")
                    ?? throw new InvalidOperationException("整合包缺少 modrinth.index.json。");

        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);

        if (!document.RootElement.TryGetProperty("files", out var files))
        {
            return;
        }

        var targets = files.EnumerateArray()
            .Where(f => !IsServerOnly(f))
            .Select(f => (
                Path: ReadString(f, "path"),
                Url: f.TryGetProperty("downloads", out var downloads) && downloads.GetArrayLength() > 0
                    ? downloads[0].GetString()
                    : null,
                Size: f.TryGetProperty("fileSize", out var size) ? size.GetInt64() : 0,
                Sha1: f.TryGetProperty("hashes", out var hashes) ? ReadString(hashes, "sha1") : null))
            .Where(f => !string.IsNullOrEmpty(f.Path) && !string.IsNullOrEmpty(f.Url))
            .ToList();

        var completed = 0;
        var reused = 0;

        // 第一遍串行：已存在的校验、能复用的复制。这些是本地操作，没必要并发。
        var pending = new List<(string Path, string Url, string? Sha1, long Size, string Destination)>();

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = ResolveSafePath(instanceDirectory, target.Path!);

            // 目标位置已经有文件：sha1 对得上才算数，对不上说明是残的/旧版本，删掉重来
            if (File.Exists(destination))
            {
                if (string.IsNullOrEmpty(target.Sha1) || Sha1Matches(destination, target.Sha1))
                {
                    completed++;
                    Report(progress, new InstallProgress("整合包文件", completed, targets.Count, target.Path!));
                    continue;
                }

                File.Delete(destination);
            }

            // 别的实例里有同一个文件：直接复制，省一次下载
            if (!string.IsNullOrEmpty(target.Sha1) &&
                contentIndex?.FindExistingFile(target.Sha1) is { } existing)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(existing, destination, overwrite: true);
                reused++;
                completed++;
                Report(progress, new InstallProgress("整合包文件", completed, targets.Count, target.Path!));
                continue;
            }

            pending.Add((target.Path!, target.Url!, target.Sha1, target.Size, destination));
        }

        if (reused > 0)
        {
            log?.Report($"{reused} 个文件从本地已有实例复用，没有重复下载。");
        }

        if (pending.Count == 0)
        {
            return;
        }

        // 第二遍并发：真正要走网络的那些
        log?.Report($"需要下载 {pending.Count} 个文件（{_concurrency} 并发）…");

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = _concurrency, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                try
                {
                    await _downloader.DownloadAsync(
                        new ResourceSpec(
                            DownloadKind.ModpackFile,
                            item.Path,
                            item.Url,
                            string.IsNullOrEmpty(item.Sha1) ? null : item.Sha1,
                            item.Destination,
                            item.Size),
                        meter: null,
                        progress: downloadProgress,
                        cancellationToken: token);

                    var done = Interlocked.Increment(ref completed);
                    Report(progress, new InstallProgress("整合包文件", done, targets.Count, item.Path));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单个文件失败不打断其它，最后统一报出来
                    failures.Add($"{item.Path}：{ex.Message}");
                }
            });

        if (!failures.IsEmpty)
        {
            var detail = string.Join(Environment.NewLine, failures.Take(10));
            var more = failures.Count > 10 ? $"{Environment.NewLine}…还有 {failures.Count - 10} 个" : string.Empty;

            throw new InvalidOperationException(
                $"{failures.Count} 个文件下载失败：{Environment.NewLine}{detail}{more}");
        }
    }

    /// <summary>
    /// 进度节流：并发下载时每个文件完成都报会刷屏，这里保证 200ms 内最多一条；
    /// 最后一个必须报，否则进度条停在 99%。
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

    /// <summary>文件内容是否与给定 sha1 一致（读流算，不整文件载入内存）。</summary>
    private static bool Sha1Matches(string path, string expected)
    {
        try
        {
            return string.Equals(
                ContentIndex.ComputeSha1(path), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsServerOnly(JsonElement file)
    {
        if (!file.TryGetProperty("env", out var env))
        {
            return false;
        }

        return env.TryGetProperty("client", out var client) && client.GetString() == "unsupported";
    }

    private static void ExtractOverrides(
        ZipArchive archive,
        ModpackFormat format,
        string instanceDirectory,
        IProgress<string>? log)
    {
        // Modrinth 用 overrides/，CurseForge 用 manifest.overrides 指定的目录（默认 overrides/），
        // 普通压缩包则是整包内容。
        var prefix = format == ModpackFormat.PlainZip ? string.Empty : "overrides/";
        var count = 0;

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.Length == 0)
            {
                continue;
            }

            if (prefix.Length > 0 && !entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = entry.FullName[prefix.Length..];

            if (format == ModpackFormat.PlainZip &&
                (relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                 relative.Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            var destination = ResolveSafePath(instanceDirectory, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // 不覆盖已经下载的文件（Mod 优先用 index 里的直链版本）
            if (File.Exists(destination))
            {
                continue;
            }

            entry.ExtractToFile(destination);
            count++;
        }

        log?.Report($"解压配置与资源 {count} 个文件");
    }

    /// <summary>把 zip 内的相对路径限定在实例目录里，挡掉 ../ 之类。</summary>
    private static string ResolveSafePath(string root, string relative)
    {
        var combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"整合包含有越界路径：{relative}");
        }

        return combined;
    }

    private static string SanitizeInstanceId(string name, string archivePath)
    {
        var candidate = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(archivePath)
            : name;

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(candidate.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        return string.IsNullOrEmpty(cleaned) ? "modpack" : cleaned;
    }


    private static string SourceHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "未知来源";
}
