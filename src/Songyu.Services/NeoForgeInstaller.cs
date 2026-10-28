using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;

namespace Songyu.Services;

/// <summary>
/// NeoForge 安装器。
/// <para>
/// 流程和 Forge 一样（下载 installer → --installClient），差别在版本号规则：
/// 1.20.2 是 20.2.x、1.21 是 21.0.x、1.21.1 是 21.1.x。
/// 注意 <b>1.20.1 没有 NeoForge</b>——它从 1.20.2 才开始独立发版。
/// </para>
/// </summary>
public sealed class NeoForgeInstaller(HttpClient http, MinecraftPaths paths, AppConfig config)
{
    private const string MavenBase = "https://maven.neoforged.net/releases/net/neoforged/neoforge";
    private const string MetadataUrl = MavenBase + "/maven-metadata.xml";

    public async Task<string> InstallAsync(
        string mcVersion,
        string javaPath,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var neoVersion = await GetNeoForgeVersionAsync(mcVersion, cancellationToken);

        var tempDir = Path.Combine(paths.Root, "temp");
        Directory.CreateDirectory(tempDir);

        var jarName = $"neoforge-{neoVersion}-installer.jar";
        var jarPath = Path.Combine(tempDir, jarName);

        log?.Report($"下载 NeoForge 安装器 {jarName}");
        await DownloadAsync($"{MavenBase}/{neoVersion}/{jarName}", jarPath, cancellationToken);

        log?.Report("运行 NeoForge 安装器（首次需要几分钟）");
        await RunInstallerAsync(jarPath, javaPath, cancellationToken);

        var versionId = FindInstalledVersion(neoVersion)
                        ?? throw new InvalidOperationException(
                            $"安装器执行完毕，但没有找到 NeoForge 生成的版本目录。\n" +
                            $"请查看 {Path.Combine(paths.Root, "temp", "neoforge-installer.log")}。");

        return versionId;
    }

    /// <summary>
    /// 按游戏版本推出 NeoForge 的版本前缀，再从 maven-metadata 里取最新的。
    /// 1.21.1 → 21.1.*；1.21 → 21.0.*；1.20.1 → 不存在。
    /// </summary>
    public async Task<string> GetNeoForgeVersionAsync(string mcVersion, CancellationToken cancellationToken = default)
    {
        var prefix = BuildVersionPrefix(mcVersion);

        var xml = await http.GetStringAsync(MetadataUrl, cancellationToken);
        var document = XDocument.Parse(xml);

        var versions = document
            .Descendants("version")
            .Select(e => e.Value.Trim())
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();

        if (versions.Count == 0)
        {
            throw new InvalidOperationException(
                $"NeoForge 没有适用于 {mcVersion} 的版本。\n" +
                "它从 Minecraft 1.20.2 才开始独立发版；1.20.1 请改用 Forge。");
        }

        return versions
            .OrderByDescending(v => ParseSortable(v))
            .First();
    }

    private static string BuildVersionPrefix(string mcVersion)
    {
        var parts = mcVersion.Split('.');

        return parts.Length >= 3
            ? $"{parts[1]}.{parts[2]}."   // 1.21.1 → 21.1.
            : parts.Length == 2
                ? $"{parts[1]}.0."        // 1.21 → 21.0.
                : throw new InvalidOperationException($"无法识别的游戏版本：{mcVersion}");
    }

    /// <summary>把 21.1.264 这类版本号转成可比较的数值，避免字符串排序把 21.1.9 排到 21.1.264 后面。</summary>
    private static long ParseSortable(string version)
    {
        var digits = version.Split('.');
        long value = 0;

        foreach (var part in digits)
        {
            if (!int.TryParse(part, out var number))
            {
                return value;
            }

            value = (value * 1000) + number;
        }

        return value;
    }

    private string? FindInstalledVersion(string neoVersion)
    {
        foreach (var id in new[] { $"neoforge-{neoVersion}", $"neo-forge-{neoVersion}" })
        {
            if (File.Exists(paths.VersionJsonPath(id)))
            {
                return id;
            }
        }

        if (!Directory.Exists(paths.VersionsDir))
        {
            return null;
        }

        foreach (var directory in Directory.GetDirectories(paths.VersionsDir))
        {
            var id = Path.GetFileName(directory);
            if (id.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return null;
    }

    private async Task DownloadAsync(string url, string targetPath, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(targetPath);

        await source.CopyToAsync(target, cancellationToken);
    }

    private async Task RunInstallerAsync(string jarPath, string javaPath, CancellationToken cancellationToken)
    {
        EnsureLauncherProfile();

        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = paths.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(jarPath);
        startInfo.ArgumentList.Add("--installClient");
        startInfo.ArgumentList.Add(paths.Root);

        var jvmTemp = Path.Combine(paths.Root, "temp", "jvm");
        Directory.CreateDirectory(jvmTemp);
        startInfo.ArgumentList.Insert(0, $"-Djava.io.tmpdir={jvmTemp}");

        foreach (var arg in BuildProxyArgs())
        {
            startInfo.ArgumentList.Insert(0, arg);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 NeoForge 安装器进程。");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var logPath = Path.Combine(paths.Root, "temp", "neoforge-installer.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await File.WriteAllTextAsync(logPath, stdout + Environment.NewLine + stderr, cancellationToken);

        if (process.ExitCode != 0)
        {
            var tail = string.Join(Environment.NewLine,
                (stderr + Environment.NewLine + stdout)
                .Split('\n')
                .TakeLast(15)
                .Select(l => l.TrimEnd()));

            throw new InvalidOperationException(
                $"NeoForge 安装器退出码 {process.ExitCode}。日志：{logPath}{Environment.NewLine}{tail}");
        }
    }

    private void EnsureLauncherProfile()
    {
        var path = Path.Combine(paths.Root, "launcher_profiles.json");
        if (File.Exists(path))
        {
            return;
        }

        const string content = """
        {
          "profiles": {},
          "settings": {},
          "version": 3
        }
        """;

        File.WriteAllText(path, content);
    }

    private IEnumerable<string> BuildProxyArgs()
    {
        if (config.ProxyMode != ProxyMode.Custom ||
            string.IsNullOrWhiteSpace(config.HttpProxy) ||
            !Uri.TryCreate(config.HttpProxy, UriKind.Absolute, out var proxy))
        {
            yield break;
        }

        yield return $"-Dhttp.proxyHost={proxy.Host}";
        yield return $"-Dhttp.proxyPort={proxy.Port}";
        yield return $"-Dhttps.proxyHost={proxy.Host}";
        yield return $"-Dhttps.proxyPort={proxy.Port}";
    }
}
