using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Songyu.Core.Launch;
using Songyu.Core.Minecraft;

namespace Songyu.Services;

/// <summary>
/// 启动编排：准备 natives、拼 classpath、生成命令行、拉起进程。
/// </summary>
public sealed class LaunchService(MinecraftPaths paths)
{
    public async Task<Process> LaunchAsync(
        string versionId,
        LaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(options.UserType, "msa", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(options.AccessToken) || options.AccessToken == "0" ||
            string.IsNullOrWhiteSpace(options.Uuid))
        {
            throw new InvalidOperationException("游戏启动需要有效的正版账号身份；离线身份启动已暂停。");
        }

        var jsonPath = paths.VersionJsonPath(versionId);
        if (!File.Exists(jsonPath))
        {
            throw new FileNotFoundException($"版本 {versionId} 尚未安装。", jsonPath);
        }

        var resolved = await new VersionResolver(paths).ResolveAsync(versionId, cancellationToken);
        var json = resolved.Json;

        var context = RuleContext.Current();

        // natives 每次重建：旧文件残留会导致奇怪的 UnsatisfiedLinkError。
        var nativesDir = paths.NativesDir(versionId);
        if (Directory.Exists(nativesDir))
        {
            Directory.Delete(nativesDir, recursive: true);
        }

        await NativesExtractor.ExtractAsync(
            json.Libraries, context, paths.LibrariesDir, nativesDir, cancellationToken);

        var logConfigFile = json.Logging?.Client?.File;
        var logConfigPath = logConfigFile is { Id.Length: > 0 }
            ? Path.Combine(paths.AssetsDir, "log_configs", logConfigFile.Id + ".xml")
            : null;

        var launchPaths = new LaunchPaths
        {
            ClientJar = paths.VersionJarPath(versionId),
            LibrariesDir = paths.LibrariesDir,
            AssetsDir = paths.AssetsDir,
            NativesDir = nativesDir,
            Classpath = BuildClasspath(json, context, resolved.RootVersionId),
            LogConfigPath = logConfigPath is not null && File.Exists(logConfigPath) ? logConfigPath : null,
        };

        var command = LaunchCommandBuilder.Build(json, launchPaths, options, context);

        Directory.CreateDirectory(paths.LogsDir);
        var logPath = Path.Combine(paths.LogsDir, $"{versionId}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        return GameLauncher.Start(command, logPath);
    }

    private string BuildClasspath(VersionJson json, RuleContext context, string rootVersionId)
    {
        var parts = new List<string>();

        foreach (var library in json.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, context))
            {
                continue;
            }

            if (LibraryResolver.Resolve(library) is { } artifact)
            {
                parts.Add(Path.Combine(
                    paths.LibrariesDir,
                    artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        // 原版：用自己的 jar。
        // 加载器版本分两种：
        //   Fabric / Quilt → 需要根版本的 client jar（Knot 从 classpath 加载 MC）
        //   Forge / NeoForge → 不能加。BootstrapLauncher 通过 module path 自行定位 patched client
        //                      （模块名 minecraft），再塞一份原版 jar 会撞 "package net.minecraft.data" 之类冲突。
        // 注意不能用 inheritsFrom 判断：VersionResolver 合并后的对象不带这个字段，只看 mainClass。
        var needsRootJar = !IsBootstrapLauncher(json.MainClass);

        if (needsRootJar)
        {
            var ownJar = paths.VersionJarPath(json.Id);
            parts.Add(File.Exists(ownJar) ? ownJar : paths.VersionJarPath(rootVersionId));
        }

        return string.Join(Path.PathSeparator, parts);
    }

    private static bool IsBootstrapLauncher(string? mainClass)
        => mainClass?.Contains("bootstraplauncher", StringComparison.OrdinalIgnoreCase) == true;
}
