namespace Songyu.Core.Minecraft;

/// <summary>
/// Mojang 版本清单（version_manifest_v2.json）中的一条记录。
/// 这里只保留启动器真正用得上的字段。
/// </summary>
public sealed record MinecraftVersion(
    string Id,
    string Type,
    Uri Url,
    string Sha1,
    DateTimeOffset ReleaseTime)
{
    public bool IsRelease => string.Equals(Type, "release", StringComparison.OrdinalIgnoreCase);

    public bool IsSnapshot => string.Equals(Type, "snapshot", StringComparison.OrdinalIgnoreCase);

    /// <summary>既非正式版也非快照（old_alpha / old_beta / 特殊版本）。</summary>
    public bool IsLegacy => !IsRelease && !IsSnapshot;
}

/// <summary>版本清单的顶层结构。</summary>
public sealed record VersionManifest(
    string LatestRelease,
    string LatestSnapshot,
    IReadOnlyList<MinecraftVersion> Versions);
