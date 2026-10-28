using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Songyu.Services;

/// <summary>
/// Forge 安装器。
/// <para>
/// 和 Fabric / Quilt 完全不同：Forge 必须下载它的 installer.jar 并执行
/// <c>--installClient</c>，由安装器自己完成 MCP 映射、二进制补丁、处理器（processors）
/// 那一整套流程。我们只负责准备好目录、把代理参数传进去、检查产出。
/// </para>
/// </summary>
public sealed class ForgeInstaller(HttpClient http, MinecraftPaths paths, AppConfig config)
{
    private const string PromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    private const string MavenBase = "https://maven.minecraftforge.net/net/minecraftforge/forge";

    /// <summary>返回生成的版本 ID，形如 <c>forge-1.20.1-47.2.0</c>。</summary>
    public async Task<string> InstallAsync(
        string mcVersion,
        string javaPath,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var forgeVersion = await GetForgeVersionAsync(mcVersion, cancellationToken);
        var pair = $"{mcVersion}-{forgeVersion}";

        var tempDir = Path.Combine(paths.Root, "temp");
        Directory.CreateDirectory(tempDir);

        var jarName = $"forge-{pair}-installer.jar";
        var jarPath = Path.Combine(tempDir, jarName);

        log?.Report($"下载 Forge 安装器 {jarName}");
        await DownloadAsync($"{MavenBase}/{pair}/{jarName}", jarPath, cancellationToken);

        log?.Report("运行 Forge 安装器（首次需要几分钟，会联网下载 MCP 数据）");
        await RunInstallerAsync(jarPath, javaPath, cancellationToken);
        var versionId = FindInstalledVersion(mcVersion, forgeVersion)
                        ?? throw new InvalidOperationException(
                            $"安装器执行完毕，但没有找到 Forge 生成的版本目录。\n" +
                            $"请查看 {Path.Combine(paths.Root, "temp", "forge-installer.log")}。");

        return versionId;
    }

    /// <summary>
    /// Forge 1.17 之后生成的目录叫 <c>1.20.1-forge-47.4.10</c>，
    /// 更早的叫 <c>forge-1.20.1-47.4.10</c>。两种都认，最后再兜底扫目录。
    /// </summary>
    private string? FindInstalledVersion(string mcVersion, string forgeVersion)
    {
        foreach (var id in new[]
                 {
                     $"{mcVersion}-forge-{forgeVersion}",
                     $"forge-{mcVersion}-{forgeVersion}",
                 })
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
            if (!id.Contains("forge", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var jsonPath = Path.Combine(directory, id + ".json");
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
                if (document.RootElement.TryGetProperty("inheritsFrom", out var parent) &&
                    parent.GetString() == mcVersion)
                {
                    return id;
                }
            }
            catch
            {
                // 坏文件跳过
            }
        }

        return null;
    }

    /// <summary>
    /// 取对应游戏版本的 Forge 版本号：优先 recommended，其次 latest。
    /// 数据来自 promotions_slim.json，例如 {"1.20.1-recommended":"47.2.0"}。
    /// </summary>
    public async Task<string> GetForgeVersionAsync(string mcVersion, CancellationToken cancellationToken = default)
    {
        var raw = await http.GetStringAsync(PromotionsUrl, cancellationToken);

        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("promos", out var promos))
        {
            throw new InvalidOperationException("Forge 版本索引格式无法识别。");
        }

        if (promos.TryGetProperty($"{mcVersion}-recommended", out var recommended) &&
            recommended.GetString() is { Length: > 0 } rec)
        {
            return rec;
        }

        if (promos.TryGetProperty($"{mcVersion}-latest", out var latest) &&
            latest.GetString() is { Length: > 0 } lat)
        {
            return lat;
        }

        throw new InvalidOperationException($"没有找到 {mcVersion} 对应的 Forge 版本。");
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
        // Forge 安装器会先检查目标目录像不像 .minecraft：
        // 没有 launcher_profiles.json 就直接报 "you need to run the launcher first"。
        // 这个文件本身只是个空壳，官方启动器第一次运行时也会生成同样的结构。
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

        // 安装器的 processors 会往 %TEMP% 解压 MCP 数据；某些环境对系统临时目录有限制，
        // 统一挪到数据目录下，行为和游戏启动时保持一致。
        var jvmTemp = Path.Combine(paths.Root, "temp", "jvm");
        Directory.CreateDirectory(jvmTemp);
        startInfo.ArgumentList.Insert(0, $"-Djava.io.tmpdir={jvmTemp}");

        // 安装器跑在独立 JVM 里，不会读我们的 HttpClient 配置，
        // 代理必须通过 JVM 参数传进去，否则在需要代理的网络下它会卡死。
        foreach (var arg in BuildProxyArgs())
        {
            startInfo.ArgumentList.Insert(0, arg);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 Forge 安装器进程。");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var logPath = Path.Combine(paths.Root, "temp", "forge-installer.log");
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
                $"Forge 安装器退出码 {process.ExitCode}。日志：{logPath}{Environment.NewLine}{tail}");
        }
    }

    /// <summary>
    /// Forge 安装器会检查目标目录像不像 .minecraft，
    /// 缺 launcher_profiles.json 就直接拒绝。这里补一个最小空壳。
    /// </summary>
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

    /// <summary>把当前代理配置翻译成 JVM 的 http.proxyHost 参数。</summary>
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
