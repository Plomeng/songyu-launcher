namespace Songyu.Core.Download;

/// <summary>下载物料的类别，决定它走哪个源、落到哪个目录。</summary>
public enum DownloadKind
{
    VersionManifest,
    VersionJson,
    ClientJar,
    Library,
    AssetIndex,
    Asset,
    LogConfig,
    JavaRuntime,
    ModFile,
    ModpackFile,
}

/// <summary>
/// 一个待下载资源。带上 <see cref="OfficialUrl"/> 作为最终兜底，
/// 这样即使所有源都解析不出地址，也还能直连官方。
/// </summary>
public sealed record ResourceSpec(
    DownloadKind Kind,
    string RelativePath,
    string OfficialUrl,
    string? Sha1,
    string TargetPath,
    long ExpectedSize = 0)
{
    public string DisplayName => Path.GetFileName(TargetPath);
}

/// <summary>
/// 下载源。程序只内置官方源；镜像必须由用户在配置里显式给出根地址，
/// 且路径约定是「<c>{root}/libraries/…</c>」「<c>{root}/assets/…</c>」——
/// 这也是社区镜像（如 BMCLAPI）通行的布局。
/// </summary>
public interface IDownloadSource
{
    string Name { get; }

    /// <summary>数值小的先试。</summary>
    int Priority { get; }

    /// <summary>解析不出就返回 null，调用方继续尝试下一个源。</summary>
    Uri? Resolve(DownloadKind kind, string relativePath);
}

/// <summary>官方源：不做任何改写，直接放行原始地址。</summary>
public sealed class OfficialSource : IDownloadSource
{
    public string Name => "Mojang 官方";

    public int Priority => 100;

    public Uri? Resolve(DownloadKind kind, string relativePath) => null;
}

/// <summary>
/// 镜像源。路径约定与社区镜像（BMCLAPI 等）一致：
/// <c>{root}/libraries/…</c>、<c>{root}/assets/…</c>、<c>{root}/version/{id}/client</c>。
/// </summary>
public sealed class MirrorSource(string rootUrl) : IDownloadSource
{
    public string Name => $"镜像 {rootUrl}";

    public int Priority => 10;

    public Uri? Resolve(DownloadKind kind, string relativePath)
    {
        var root = rootUrl.TrimEnd('/');

        var path = kind switch
        {
            DownloadKind.Library => "libraries/" + relativePath,
            DownloadKind.Asset => "assets/" + relativePath,
            DownloadKind.AssetIndex => "assets/indexes/" + relativePath,
            DownloadKind.VersionManifest => "mc/game/version_manifest_v2.json",
            DownloadKind.LogConfig => "assets/log_configs/" + relativePath,

            // 版本本体在镜像上是独立的接口，不是 Mojang 那种同路径结构
            DownloadKind.ClientJar => "version/" + VersionId(relativePath) + "/client",
            DownloadKind.VersionJson => "version/" + VersionId(relativePath) + "/json",

            _ => null,
        };

        return path is null ? null : new Uri($"{root}/{path}");
    }

    /// <summary>从 "1.20.1.jar" / "1.20.1.json" 这类文件名里取出纯版本号。</summary>
    private static string VersionId(string relativePath)
        => Path.GetFileNameWithoutExtension(relativePath.Replace('\\', '/'));
}
