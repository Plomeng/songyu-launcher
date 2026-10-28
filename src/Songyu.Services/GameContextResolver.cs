using System.IO;
using System.Text.Json;
using Songyu.Core.Minecraft;

namespace Songyu.Services;

/// <summary>一个实例对应的"游戏上下文"：MC 版本 + 加载器。</summary>
public sealed record GameContext(string? McVersion, string? Loader)
{
    /// <summary>Modrinth 查询用的加载器数组（原版就是空）。</summary>
    public IReadOnlyList<string> Loaders => Loader is { Length: > 0 } ? [Loader] : [];

    public bool IsModded => Loader is { Length: > 0 };

    public string Describe() => Loader is { Length: > 0 }
        ? $"{Loader} · {McVersion ?? "未知版本"}"
        : $"原版 · {McVersion ?? "未知版本"}";
}

/// <summary>
/// 从版本 json 推出实例的 MC 版本与加载器。
/// <para>
/// 判定依据是版本 ID 前缀/后缀 + <c>inheritsFrom</c>：
/// 加载器版本的 <c>inheritsFrom</c> 指向原版，原版自己就是游戏版本。
/// </para>
/// </summary>
public static class GameContextResolver
{
    /// <summary>加载器版本 ID 的前后缀标记（Modrinth 用的 loader 名）。</summary>
    private static readonly (string Marker, string Loader)[] LoaderMarkers =
    [
        ("fabric-loader", "fabric"),
        ("quilt-loader", "quilt"),
        ("neoforge", "neoforge"),
        ("forge", "forge"),
    ];

    public static GameContext Resolve(MinecraftPaths paths, string versionId)
    {
        var jsonPath = paths.VersionJsonPath(versionId);

        if (!File.Exists(jsonPath))
        {
            // 版本没装：至少能从 ID 猜出个大概，别让调用方直接失败
            return new GameContext(GuessMcVersion(versionId), DetectLoader(versionId));
        }

        try
        {
            var json = JsonSerializer.Deserialize<VersionJson>(
                File.ReadAllText(jsonPath), SongyuJson.Options);

            if (json is null)
            {
                return new GameContext(GuessMcVersion(versionId), DetectLoader(versionId));
            }

            var mcVersion = string.IsNullOrEmpty(json.InheritsFrom) ? json.Id : json.InheritsFrom;
            var loader = DetectLoader(json.Id) ?? DetectLoader(json.InheritsFrom ?? string.Empty);

            return new GameContext(mcVersion, loader);
        }
        catch
        {
            return new GameContext(GuessMcVersion(versionId), DetectLoader(versionId));
        }
    }

    public static string? DetectLoader(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return null;
        }

        foreach (var (marker, loader) in LoaderMarkers)
        {
            if (versionId.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return loader;
            }
        }

        return null;
    }

    /// <summary>
    /// 带兜底的解析：版本 json 拿不到加载器/MC 版本时，从已装 Mod 推断。
    /// <para>
    /// 用于"整合包导入后版本 json 丢失"这类历史实例 —— 光看版本 ID 啥也推不出来，
    /// 但 jar 里的 fabric.mod.json / mods.toml 和文件名里全是线索。
    /// </para>
    /// </summary>
    public static GameContext ResolveForInstance(
        MinecraftPaths paths,
        string versionId,
        IReadOnlyList<IndexedFile> modFiles)
    {
        var context = Resolve(paths, versionId);

        if (context.Loader is { Length: > 0 } && context.McVersion is { Length: > 0 })
        {
            return context;
        }

        var loader = context.Loader is { Length: > 0 }
            ? context.Loader
            : InferLoader(modFiles);

        var mcVersion = context.McVersion is { Length: > 0 }
            ? context.McVersion
            : InferMcVersion(modFiles);

        return new GameContext(mcVersion, loader);
    }

    /// <summary>从已装 Mod 的元数据里取出现次数最多的加载器。</summary>
    public static string? InferLoader(IReadOnlyList<IndexedFile> mods)
        => mods.Select(m => m.ModLoader)
            .Where(l => !string.IsNullOrWhiteSpace(l) && l != "unknown")
            .GroupBy(l => l!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

    /// <summary>
    /// 从文件名里猜 MC 版本（形如 1.20.1 / 1.19 / 1.21.4），取出现最多的那个。
    /// 只在版本 json 缺失时兜底用。
    /// </summary>
    public static string? InferMcVersion(IReadOnlyList<IndexedFile> mods)
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            @"(?<!\d)(1\.\d{1,2}(?:\.\d{1,2})?)(?!\d)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        return mods
            .Select(m => pattern.Match(m.FileName))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
    }

    /// <summary>从 ID 里抠一个像 MC 版本的片段（仅兜底用）。</summary>
    private static string? GuessMcVersion(string versionId)
    {
        var parts = versionId.Split('-', StringSplitOptions.RemoveEmptyEntries);

        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var part = parts[i];
            if (part.Length >= 3 && char.IsDigit(part[0]) && part.Contains('.'))
            {
                return part;
            }
        }

        return null;
    }
}
