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
/// 一个待下载资源。下载地址来自官方元数据。
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
/// 下载源。当前只提供官方地址。
/// </summary>
public interface IDownloadSource
{
    string Name { get; }

    /// <summary>数值小的先试。</summary>
    int Priority { get; }

    /// <summary>解析不出就返回 null，调用方继续尝试下一个源。</summary>
    Uri? Resolve(DownloadKind kind, string relativePath);
}

/// <summary>官方源：不改写元数据中的地址。</summary>
public sealed class OfficialSource : IDownloadSource
{
    public string Name => "Mojang 官方";

    public int Priority => 100;

    public Uri? Resolve(DownloadKind kind, string relativePath) => null;
}
