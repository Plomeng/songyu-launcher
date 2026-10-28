using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Songyu.Services;

/// <summary>
/// 单个版本的启动设置，落在 <c>versions/&lt;id&gt;/instance.json</c>。
/// 不设值时全部走默认，所以老版本目录没有这个文件也能正常启动。
/// </summary>
public sealed class InstanceSettings
{
    public int MaxMemoryMb { get; set; } = 2048;

    public int MinMemoryMb { get; set; }

    /// <summary>为空表示自动选择（取探测到的最高版本 Java）。</summary>
    public string? JavaPath { get; set; }

    public string ExtraJvmArgs { get; set; } = string.Empty;

    public int WindowWidth { get; set; } = 1280;

    public int WindowHeight { get; set; } = 720;

    public bool Fullscreen { get; set; }

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string PathFor(MinecraftPaths paths, string versionId)
        => Path.Combine(paths.VersionDir(versionId), "instance.json");

    public static InstanceSettings Load(MinecraftPaths paths, string versionId)
    {
        var path = PathFor(paths, versionId);

        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<InstanceSettings>(File.ReadAllText(path), FileOptions)
                       ?? new InstanceSettings();
            }
        }
        catch
        {
            // 配置坏了就用默认值，不让启动失败
        }

        return new InstanceSettings();
    }

    public void Save(MinecraftPaths paths, string versionId)
    {
        var path = PathFor(paths, versionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, FileOptions));
    }

    /// <summary>把 "a b c" 拆成参数列表，支持引号包裹。</summary>
    public IReadOnlyList<string> ParseExtraJvmArgs()
        => string.IsNullOrWhiteSpace(ExtraJvmArgs)
            ? []
            : ExtraJvmArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(a => a.Trim('"'))
                .ToList();
}
