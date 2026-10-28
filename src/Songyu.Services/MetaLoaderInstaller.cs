using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Songyu.Core.Minecraft;

namespace Songyu.Services;

/// <summary>可选的 Mod 加载器。</summary>
public enum LoaderKind
{
    Vanilla,
    Fabric,
    Quilt,
    Forge,
    NeoForge,
}

/// <summary>
/// 基于 meta 服务的加载器安装器，Fabric 与 Quilt 共用。
/// <para>
/// 它们的套路一致：拉 loader 版本列表 → 取 profile json（带 inheritsFrom）→ 落盘。
/// 不需要跑任何安装器进程，也不改原版 jar。差异只在 base URL。
/// </para>
/// </summary>
public sealed class MetaLoaderInstaller(
    HttpClient http,
    MinecraftPaths paths,
    string metaBase,
    string displayName)
{
    public string DisplayName => displayName;

    /// <summary>
    /// 返回生成的版本 ID，例如 <c>fabric-loader-0.19.5-1.20.1</c>。
    /// <paramref name="loaderVersion"/> 指定时用指定版本（整合包会精确锁定），
    /// 否则取最新 stable。
    /// </summary>
    public async Task<string> InstallAsync(
        string gameVersion,
        string? loaderVersion = null,
        CancellationToken cancellationToken = default)
    {
        var version = string.IsNullOrWhiteSpace(loaderVersion)
            ? await GetLoaderVersionAsync(gameVersion, cancellationToken)
            : loaderVersion.Trim();

        var profileUrl = $"{metaBase}/versions/loader/{gameVersion}/{version}/profile/json";
        var profileJson = await http.GetStringAsync(profileUrl, cancellationToken);

        var parsed = JsonSerializer.Deserialize<VersionJson>(profileJson, SongyuJson.Options)
                     ?? throw new InvalidOperationException($"{displayName} 返回的 profile JSON 无法解析。");

        if (string.IsNullOrEmpty(parsed.Id))
        {
            throw new InvalidOperationException($"{displayName} 返回的 profile 缺少版本 ID。");
        }

        var directory = paths.VersionDir(parsed.Id);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(paths.VersionJsonPath(parsed.Id), profileJson, cancellationToken);

        return parsed.Id;
    }

    /// <summary>
    /// 列表按版本从新到旧排列：优先取标记为 stable 的，没有就取第一个。
    /// 注意版本号在 <c>loader.version</c>，不是顶层字段。
    /// </summary>
    public async Task<string> GetLoaderVersionAsync(string gameVersion, CancellationToken cancellationToken = default)
    {
        var raw = await http.GetStringAsync(
            $"{metaBase}/versions/loader/{gameVersion}", cancellationToken);

        var entries = JsonSerializer.Deserialize<List<LoaderEntry>>(raw, SongyuJson.Options) ?? [];
        var chosen = entries.FirstOrDefault(e => e.Loader.Stable) ?? entries.FirstOrDefault();

        return chosen?.Loader.Version
               ?? throw new InvalidOperationException($"没有找到 {gameVersion} 可用的 {displayName} 加载器。")
            ;
    }

    private sealed class LoaderEntry
    {
        [JsonPropertyName("loader")]
        public LoaderInfo Loader { get; init; } = new();
    }

    private sealed class LoaderInfo
    {
        [JsonPropertyName("version")]
        public string Version { get; init; } = string.Empty;

        [JsonPropertyName("stable")]
        public bool Stable { get; init; }
    }
}
