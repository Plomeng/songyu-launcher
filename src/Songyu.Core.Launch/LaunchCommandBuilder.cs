using System.Text.RegularExpressions;
using Songyu.Core.Minecraft;

namespace Songyu.Core.Launch;

/// <summary>启动时所需的各个路径，由上层准备好后传入，生成器本身不碰文件系统。</summary>
public sealed class LaunchPaths
{
    public required string ClientJar { get; init; }

    public required string LibrariesDir { get; init; }

    public required string AssetsDir { get; init; }

    public required string NativesDir { get; init; }

    /// <summary>已拼好的 classpath（用当前平台的分隔符连接）。</summary>
    public required string Classpath { get; init; }

    /// <summary>log4j 配置落地路径，没有则不加对应参数。</summary>
    public string? LogConfigPath { get; init; }
}

public sealed class LaunchCommand
{
    public required string JavaExecutable { get; init; }

    public required List<string> Arguments { get; init; }

    public required string WorkingDirectory { get; init; }
}

/// <summary>
/// 把版本 JSON + 启动选项拼成最终命令行。
/// <para>
/// 关键点：1.13+ 的 <c>-cp ${classpath}</c> 本身就写在 <c>arguments.jvm</c> 数组里，
/// 因此这里绝不能"顺手再加一个 -cp"，否则会重复。
/// </para>
/// </summary>
public static class LaunchCommandBuilder
{
    public const string LauncherName = "Songyu";
    public const string LauncherVersion = "0.1.0";

    public static LaunchCommand Build(
        VersionJson version,
        LaunchPaths paths,
        LaunchOptions options,
        RuleContext context)
    {
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = options.PlayerName,
            ["version_name"] = version.Id,
            ["game_directory"] = options.GameDirectory,
            ["assets_root"] = paths.AssetsDir,
            ["assets_index_name"] = version.Assets ?? "legacy",
            ["auth_uuid"] = options.Uuid,
            ["auth_access_token"] = options.AccessToken,
            ["clientid"] = string.Empty,
            ["auth_xuid"] = string.Empty,
            ["user_type"] = options.UserType,
            ["version_type"] = version.Type ?? "release",
            ["classpath"] = paths.Classpath,
            ["natives_directory"] = paths.NativesDir,
            ["launcher_name"] = LauncherName,
            ["launcher_version"] = LauncherVersion,
            ["library_directory"] = paths.LibrariesDir,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
        };

        var args = new List<string>();

        args.Add($"-Xmx{Math.Max(512, options.MaxMemoryMb)}M");
        if (options.MinMemoryMb > 0)
        {
            args.Add($"-Xms{options.MinMemoryMb}M");
        }

        // 临时目录放到游戏目录下：既不污染系统 TEMP，也能绕开某些环境对 %TEMP% 的限制
        // （Fabric 的错误报告窗会用它做 imageio 缓存，写不进去会连带把报错流程也搞崩）。
        var tempDir = Path.Combine(options.GameDirectory, "tmp");
        Directory.CreateDirectory(tempDir);
        args.Add($"-Djava.io.tmpdir={tempDir}");

        if (version.Arguments is { } arguments)
        {
            AppendElements(args, arguments.Jvm, context, placeholders);
        }
        else
        {
            // 1.13 之前没有 arguments 字段，只能手工兜底。
            args.Add("-Djava.library.path=" + paths.NativesDir);
            args.Add("-cp");
            args.Add(paths.Classpath);
        }

        if (!string.IsNullOrEmpty(paths.LogConfigPath))
        {
            args.Add("-Dlog4j.configurationFile=" + paths.LogConfigPath);
        }

        args.AddRange(options.ExtraJvmArgs);

        args.Add(version.MainClass);

        if (version.Arguments is { } withGame)
        {
            AppendElements(args, withGame.Game, context, placeholders);
        }
        else if (version.MinecraftArguments is { Length: > 0 } legacy)
        {
            foreach (var token in legacy.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                args.Add(ReplacePlaceholders(token, placeholders));
            }
        }

        if (options.WindowWidth is { } width && options.WindowHeight is { } height)
        {
            args.Add("--width");
            args.Add(width.ToString());
            args.Add("--height");
            args.Add(height.ToString());
        }

        if (options.Fullscreen)
        {
            args.Add("--fullscreen");
        }

        args.AddRange(options.ExtraGameArgs);

        return new LaunchCommand
        {
            JavaExecutable = options.JavaExecutable,
            Arguments = args,
            WorkingDirectory = options.GameDirectory,
        };
    }

    private static void AppendElements(
        List<string> target,
        IEnumerable<ArgumentElement> elements,
        RuleContext context,
        IReadOnlyDictionary<string, string> placeholders)
    {
        foreach (var element in elements)
        {
            if (!element.IsAllowed(context))
            {
                continue;
            }

            foreach (var value in element.Values)
            {
                target.Add(ReplacePlaceholders(value, placeholders));
            }
        }
    }

    private static string ReplacePlaceholders(string value, IReadOnlyDictionary<string, string> placeholders)
    {
        if (!value.Contains("${", StringComparison.Ordinal))
        {
            return value;
        }

        return Regex.Replace(value, @"\$\{([^}]+)\}", match =>
            placeholders.TryGetValue(match.Groups[1].Value, out var replacement) ? replacement : match.Value);
    }
}
