using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Songyu.Core.Minecraft;

/// <summary>从 Mojang 官方地址获取版本清单与版本 JSON。</summary>
public sealed class VersionManifestClient(HttpClient http)
{
    public static readonly Uri ManifestUri = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");

    public async Task<VersionManifest> GetManifestAsync(CancellationToken cancellationToken = default)
    {
        return await GetManifestOnceAsync(ManifestUri, cancellationToken);
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
        var json = await http.GetStringAsync(version.Url, cancellationToken);

        return JsonSerializer.Deserialize<VersionJson>(json, SongyuJson.Options)
               ?? throw new InvalidOperationException($"版本 {version.Id} 的 JSON 解析失败。");
    }

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
