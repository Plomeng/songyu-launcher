using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Songyu.Services;

public sealed record JavaInstallation(string JavaExePath, int MajorVersion, string Brand, string Version)
{
    public override string ToString() => $"Java {Version} · {Brand} · {JavaExePath}";
}

public interface IJavaService
{
    Task<IReadOnlyList<JavaInstallation>> ScanAsync(CancellationToken cancellationToken = default);
}

/// <summary>收集环境变量、注册表及常见目录中的 Java，通过运行时自身报告确认版本。</summary>
public sealed class JavaService : IJavaService
{
    public async Task<IReadOnlyList<JavaInstallation>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await Task.Run(FindCandidates, cancellationToken);
        var results = new List<JavaInstallation>();
        await Parallel.ForEachAsync(candidates,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (path, ct) =>
            {
                if (await InspectAsync(path, ct) is { } installation)
                {
                    lock (results) { results.Add(installation); }
                }
            });
        return results.OrderByDescending(j => j.MajorVersion)
            .ThenBy(j => j.JavaExePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static HashSet<string> FindCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) { return; }
            try
            {
                directory = Environment.ExpandEnvironmentVariables(directory.Trim('"'));
                foreach (var relative in new[] { "java.exe", "bin/java.exe", "jre/bin/java.exe" })
                {
                    var path = Path.GetFullPath(Path.Combine(directory, relative));
                    if (File.Exists(path)) { candidates.Add(path); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // 单个目录不可访问不影响其余候选。
            }
        }

        Add(Environment.GetEnvironmentVariable("JAVA_HOME"));
        Add(Environment.GetEnvironmentVariable("JDK_HOME"));
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) { Add(directory); }
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                foreach (var family in new[] { "Java Runtime Environment", "Java Development Kit", "JRE", "JDK" })
                {
                    using var key = root.OpenSubKey(@"SOFTWARE\JavaSoft\" + family);
                    if (key is null) { continue; }
                    foreach (var version in key.GetSubKeyNames())
                    {
                        using var entry = key.OpenSubKey(version);
                        Add(entry?.GetValue("JavaHome") as string);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // 注册表项可能受权限限制。
            }
        }

        var roots = new List<string>();
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var programFiles = Environment.GetFolderPath(folder);
            foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto", "Semeru" })
            {
                roots.Add(Path.Combine(programFiles, vendor));
            }
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Eclipse Adoptium"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"));
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "tools"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Java"));
        }
        foreach (var root in roots)
        {
            Add(root);
            try
            {
                if (!Directory.Exists(root)) { continue; }
                foreach (var directory in Directory.EnumerateDirectories(root)) { Add(directory); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 不递归扫描整块磁盘，避免启动时长不可控。
            }
        }
        return candidates;
    }

    private static async Task<JavaInstallation?> InspectAsync(string path, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-XshowSettings:properties", "-version" },
            },
        };
        try
        {
            if (!process.Start()) { return null; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await stdout + "\n" + await stderr;
            if (process.ExitCode != 0) { return null; }
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in text.Split('\n'))
            {
                var separator = line.IndexOf('=');
                if (separator > 0) { properties[line[..separator].Trim()] = line[(separator + 1)..].Trim(); }
            }
            if (!properties.TryGetValue("java.version", out var version)) { return null; }
            var parts = version.Split('.', '-', '+', '_');
            var majorText = parts[0] == "1" && parts.Length > 1 ? parts[1] : parts[0];
            if (!int.TryParse(majorText, out var major)) { return null; }
            return new JavaInstallation(path, major, properties.GetValueOrDefault("java.vendor", "Java"), version);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        finally
        {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
