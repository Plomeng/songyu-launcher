using System.Text.Json.Serialization;

namespace Songyu.Core.Minecraft;

/// <summary>
/// 单个版本的 JSON（versions/&lt;id&gt;/&lt;id&gt;.json）。
/// 字段名与 Mojang 的原始格式一一对应，不做重命名，避免映射错误难以排查。
/// </summary>
public sealed class VersionJson
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>继承自哪个版本；Forge / OptiFine 这类会用到，原版为 null。</summary>
    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; init; }

    [JsonPropertyName("mainClass")]
    public string MainClass { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// 资源索引名。注意：Fabric 这类加载器会给出空字符串而不是省略字段，
    /// 因此合并时必须用 IsNullOrEmpty 判断，不能当成"有值"。
    /// </summary>
    [JsonPropertyName("assets")]
    public string? Assets { get; init; }

    [JsonPropertyName("assetIndex")]
    public AssetIndexInfo? AssetIndex { get; init; }

    [JsonPropertyName("downloads")]
    public VersionDownloads? Downloads { get; init; }

    [JsonPropertyName("javaVersion")]
    public JavaVersionInfo? JavaVersion { get; init; }

    [JsonPropertyName("libraries")]
    public List<Library> Libraries { get; init; } = [];

    [JsonPropertyName("arguments")]
    public ArgumentsJson? Arguments { get; init; }

    /// <summary>1.13 之前的旧式参数模板（单个字符串）。</summary>
    [JsonPropertyName("minecraftArguments")]
    public string? MinecraftArguments { get; init; }

    [JsonPropertyName("logging")]
    public LoggingJson? Logging { get; init; }

    [JsonPropertyName("minimumLauncherVersion")]
    public int MinimumLauncherVersion { get; init; }

    /// <summary>
    /// 原样保留字符串。Fabric 等第三方会写入空串或非 ISO 的值，
    /// 强类型解析会直接抛异常，而启动流程并不需要这个字段。
    /// </summary>
    [JsonPropertyName("releaseTime")]
    public string? ReleaseTime { get; init; }
}

public sealed class AssetIndexInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("sha1")]
    public string Sha1 { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; init; }

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;
}

public sealed class VersionDownloads
{
    [JsonPropertyName("client")]
    public DownloadInfo? Client { get; init; }

    [JsonPropertyName("server")]
    public DownloadInfo? Server { get; init; }

    [JsonPropertyName("client_mappings")]
    public DownloadInfo? ClientMappings { get; init; }
}

public sealed class DownloadInfo
{
    /// <summary>仅 log4j 配置这类条目带 id，其余为 null。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>相对路径；assets 的 objects 没这个字段。</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("sha1")]
    public string Sha1 { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;
}

public sealed class JavaVersionInfo
{
    [JsonPropertyName("component")]
    public string Component { get; init; } = string.Empty;

    [JsonPropertyName("majorVersion")]
    public int MajorVersion { get; init; } = 8;
}

public sealed class Library
{
    /// <summary>Maven 坐标：group:artifact:version[:classifier]。</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; init; }

    [JsonPropertyName("rules")]
    public List<Rule>? Rules { get; init; }

    /// <summary>例如 { "linux": "natives-linux", "windows": "natives-windows-${arch}" }。</summary>
    [JsonPropertyName("natives")]
    public Dictionary<string, string>? Natives { get; init; }

    /// <summary>老格式的 maven 仓库根地址（没有 downloads 时用）。</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")]
    public DownloadInfo? Artifact { get; init; }

    [JsonPropertyName("classifiers")]
    public Dictionary<string, DownloadInfo>? Classifiers { get; init; }
}

public sealed class LoggingJson
{
    [JsonPropertyName("client")]
    public LoggingClient? Client { get; init; }
}

public sealed class LoggingClient
{
    [JsonPropertyName("argument")]
    public string Argument { get; init; } = string.Empty;

    [JsonPropertyName("file")]
    public DownloadInfo File { get; init; } = new();

    [JsonPropertyName("type")]
    public string Type { get; init; } = "log4j2-xml";
}

/// <summary>assets 索引（assets/indexes/&lt;id&gt;.json）。</summary>
public sealed class AssetIndexJson
{
    [JsonPropertyName("objects")]
    public Dictionary<string, AssetObject> Objects { get; init; } = [];
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")]
    public string Hash { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }
}
