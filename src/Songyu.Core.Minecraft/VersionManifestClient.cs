using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Songyu.Core.Minecraft;

/// <summary>
/// 版本清单与版本 JSON 的获取。
/// <para>
/// 传入 <c>mirrorBaseUrl</c> 时把两个地址都改写到镜像（BMCLAPI 的布局是
/// <c>{root}/mc/game/version_manifest_v2.json</c> 和 <c>{root}/version/{id}/json</c>）；
/// 传 null 就走官方。镜像失败由调用方决定是否回退。
/// </para>
/// </summary>
public sealed class VersionManifestClient(HttpClient http, string? mirrorBaseUrl = null)
{
    public static readonly Uri ManifestUri = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");

    private readonly string? _mirrorRoot = string.IsNullOrWhiteSpace(mirrorBaseUrl)
        ? null
        : mirrorBaseUrl.TrimEnd('/');

    private Uri ManifestUriForCurrentSource => _mirrorRoot is null
        ? ManifestUri
        : new Uri($"{_mirrorRoot}/mc/game/version_manifest_v2.json");

    public async Task<VersionManifest> GetManifestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetManifestOnceAsync(ManifestUriForCurrentSource, cancellationToken);
        }
        catch (Exception ex) when (_mirrorRoot is not null && ex is not OperationCanceledException)
        {
            // 镜像挂了（502/超时）不能让整个启动器不可用，直接回官方
            return await GetManifestOnceAsync(ManifestUri, cancellationToken);
        }
    }

    private async Task<VersionManifest> GetManifestOnceAsync(Uri uri, CancellationToken cancellationToken)
    {
        await using var stream = await http.GetStreamAsync(uri, cancellationToken);
        var dto = await JsonSerializer.DeserializeAsync<ManifestDto>(stream, SongyuJson.Options, cancellationToken)
                  ?? throw new InvalidOperationException("版本清单为空。");

        var versions = (dto.Versions ?? [])
            .Select(v => new MinecraftVersion(
                v.Id ?? string.Empty,
                v.Type ?? "release",
                new Uri(v.Url ?? throw new InvalidOperationException("版本缺少 url。")),
                v.Sha1 ?? string.Empty,
                v.ReleaseTime))
            .ToList();

        return new VersionManifest(
            dto.Latest?.Release ?? string.Empty,
            dto.Latest?.Snapshot ?? string.Empty,
            versions);
    }

    public async Task<VersionJson> GetVersionAsync(MinecraftVersion version, CancellationToken cancellationToken = default)
    {
        string json;

        try
        {
            json = await http.GetStringAsync(VersionUrl(version), cancellationToken);
        }
        catch (Exception ex) when (_mirrorRoot is not null && ex is not OperationCanceledException)
        {
            json = await http.GetStringAsync(version.Url, cancellationToken);
        }

        return JsonSerializer.Deserialize<VersionJson>(json, SongyuJson.Options)
               ?? throw new InvalidOperationException($"版本 {version.Id} 的 JSON 解析失败。");
    }

    /// <summary>清单里的 url 指向 Mojang，启用镜像时换成镜像的同名接口。</summary>
    private Uri VersionUrl(MinecraftVersion version)
        => _mirrorRoot is null
            ? version.Url
            : new Uri($"{_mirrorRoot}/version/{Uri.EscapeDataString(version.Id)}/json");

    private sealed class ManifestDto
    {
        [JsonPropertyName("latest")]
        public LatestDto? Latest { get; init; }

        [JsonPropertyName("versions")]
        public List<VersionDto>? Versions { get; init; }
    }

    private sealed class LatestDto
    {
        [JsonPropertyName("release")]
        public string? Release { get; init; }

        [JsonPropertyName("snapshot")]
        public string? Snapshot { get; init; }
    }

    private sealed class VersionDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("sha1")]
        public string? Sha1 { get; init; }

        [JsonPropertyName("releaseTime")]
        public DateTimeOffset ReleaseTime { get; init; }
    }
}

/// <summary>Maven 坐标与仓库相对路径的互转。</summary>
public static class MavenCoordinate
{
    /// <summary>
    /// <c>group:artifact:version[:classifier]</c> → <c>group/artifact/version/artifact-version[-classifier].jar</c>
    /// </summary>
    public static string ToPath(string coordinate)
    {
        var parts = coordinate.Split(':');
        if (parts.Length < 3)
        {
            throw new FormatException($"非法的 Maven 坐标：{coordinate}");
        }

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 && parts[3].Length > 0 ? "-" + parts[3] : string.Empty;

        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }
}
