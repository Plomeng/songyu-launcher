using System.IO;
using System.Text.Json;
using Songyu.Core.Minecraft;

namespace Songyu.Services;

/// <summary>合并后的版本描述，附带"客户端 jar 属于哪个版本"。</summary>
public sealed record ResolvedVersion(VersionJson Json, string RootVersionId);

/// <summary>
/// 把 <c>inheritsFrom</c> 继承链合并成一份可直接启动的版本描述。
/// <para>
/// 合并方向是「父 → 子」：后写的覆盖先写的，于是子版本的 mainClass、资源索引等自然优先，
/// 而父版本独有的 assets / logging / client jar 不会被子版本的缺省值冲掉。
/// </para>
/// </summary>
public sealed class VersionResolver(MinecraftPaths paths)
{
    private const int MaxDepth = 16;

    public async Task<ResolvedVersion> ResolveAsync(string versionId, CancellationToken cancellationToken = default)
    {
        var chain = new List<VersionJson>();
        var currentId = versionId;

        while (!string.IsNullOrEmpty(currentId))
        {
            if (chain.Count >= MaxDepth)
            {
                throw new InvalidOperationException($"版本继承链超过 {MaxDepth} 层，疑似循环引用：{versionId}");
            }

            var jsonPath = paths.VersionJsonPath(currentId);
            if (!File.Exists(jsonPath))
            {
                throw new FileNotFoundException($"缺少版本 JSON：{jsonPath}", jsonPath);
            }

            var json = JsonSerializer.Deserialize<VersionJson>(
                           await File.ReadAllTextAsync(jsonPath, cancellationToken), SongyuJson.Options)
                       ?? throw new InvalidOperationException($"版本 JSON 解析失败：{jsonPath}");

            chain.Add(json);
            currentId = json.InheritsFrom;
        }

        var rootId = chain[^1].Id;
        return new ResolvedVersion(Merge(chain), rootId);
    }

    private static VersionJson Merge(List<VersionJson> chain)
    {
        var child = chain[0];

        var libraries = new List<Library>();
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        var gameArgs = new List<ArgumentElement>();
        var jvmArgs = new List<ArgumentElement>();

        var mainClass = string.Empty;
        AssetIndexInfo? assetIndex = null;
        string? assets = null;
        string? type = null;
        VersionDownloads? downloads = null;
        LoggingJson? logging = null;
        JavaVersionInfo? javaVersion = null;
        string? legacyArguments = null;

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var version = chain[i];

            if (!string.IsNullOrEmpty(version.MainClass))
            {
                mainClass = version.MainClass;
            }

            if (version.AssetIndex is not null)
            {
                assetIndex = version.AssetIndex;
            }

            if (!string.IsNullOrEmpty(version.Assets))
            {
                assets = version.Assets;
            }

            if (!string.IsNullOrEmpty(version.Type))
            {
                type = version.Type;
            }

            if (version.Downloads is not null)
            {
                downloads = version.Downloads;
            }

            if (version.Logging is not null)
            {
                logging = version.Logging;
            }

            if (version.JavaVersion is not null)
            {
                javaVersion = version.JavaVersion;
            }

            if (!string.IsNullOrEmpty(version.MinecraftArguments))
            {
                legacyArguments = version.MinecraftArguments;
            }

            foreach (var library in version.Libraries)
            {
                if (byName.TryGetValue(library.Name, out var slot))
                {
                    libraries[slot] = library; // 子版本覆盖父版本的同名库
                }
                else
                {
                    byName[library.Name] = libraries.Count;
                    libraries.Add(library);
                }
            }

            if (version.Arguments is not null)
            {
                gameArgs.AddRange(version.Arguments.Game);
                jvmArgs.AddRange(version.Arguments.Jvm);
            }
        }

        return new VersionJson
        {
            Id = child.Id,
            MainClass = mainClass,
            Libraries = libraries,
            Arguments = new ArgumentsJson { Game = gameArgs, Jvm = jvmArgs },
            AssetIndex = assetIndex,
            Assets = assets,
            Type = type,
            Downloads = downloads,
            Logging = logging,
            JavaVersion = javaVersion,
            MinecraftArguments = legacyArguments,
            ReleaseTime = child.ReleaseTime,
        };
    }
}
