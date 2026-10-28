using System.IO;
using System.Text.Json;
using Songyu.Core.Minecraft;

namespace Songyu.Services;

/// <summary>本机已安装的一个版本。</summary>
public sealed record InstalledVersion(string Id, string? InheritsFrom, string Type, DateTime LastWrite)
{
    /// <summary>带 inheritsFrom 的是加载器版本，否则是原版。</summary>
    public bool IsLoader => !string.IsNullOrEmpty(InheritsFrom);

    public string DisplayType => IsLoader ? $"加载器 · 基于 {InheritsFrom}" : "原版";
}

/// <summary>
/// 扫描 versions/ 目录得到已安装版本列表。
/// 判定依据是 version json 里的 inheritsFrom——加载器版本一定带它。
/// </summary>
public sealed class LocalVersionService(MinecraftPaths paths)
{
    public IReadOnlyList<InstalledVersion> List()
    {
        var result = new List<InstalledVersion>();

        if (!Directory.Exists(paths.VersionsDir))
        {
            return result;
        }

        foreach (var directory in Directory.GetDirectories(paths.VersionsDir))
        {
            var id = Path.GetFileName(directory);
            var jsonPath = Path.Combine(directory, id + ".json");
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            try
            {
                var json = JsonSerializer.Deserialize<VersionJson>(
                    File.ReadAllText(jsonPath), SongyuJson.Options);

                if (json is null || string.IsNullOrEmpty(json.Id))
                {
                    continue;
                }

                result.Add(new InstalledVersion(
                    json.Id,
                    json.InheritsFrom,
                    json.Type ?? "release",
                    File.GetLastWriteTime(directory)));
            }
            catch
            {
                // 坏掉的 version json 跳过，不影响其它版本
            }
        }

        return result
            .OrderByDescending(v => v.IsLoader)
            .ThenByDescending(v => v.LastWrite)
            .ToList();
    }

    public bool Exists(string versionId)
        => File.Exists(paths.VersionJsonPath(versionId));
}
