using System.IO;

namespace Songyu.Services;

/// <summary>一张游戏截图。</summary>
public sealed record Screenshot(string FullPath, string FileName, long Bytes, DateTime Captured)
{
    public string SizeText => Bytes switch
    {
        >= 1024 * 1024 => $"{Bytes / 1024.0 / 1024:F1} MB",
        >= 1024 => $"{Bytes / 1024:F0} KB",
        _ => $"{Bytes} B",
    };

    public string CapturedText => Captured.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// 截图目录的读写。游戏把 F2 截图放在 <c>instances/&lt;id&gt;/screenshots</c>，
/// 这里只管列出来、删掉（进 .trash）、打开目录。
/// </summary>
public sealed class ScreenshotScanner(MinecraftPaths paths)
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg"];

    public string DirectoryFor(string instanceId)
        => Path.Combine(paths.InstanceDir(instanceId), "screenshots");

    /// <summary>按拍摄时间倒序（最新的在前）。</summary>
    public IReadOnlyList<Screenshot> Scan(string instanceId)
    {
        var directory = DirectoryFor(instanceId);

        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory)
            .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path =>
            {
                var info = new FileInfo(path);

                return new Screenshot(path, info.Name, info.Length, info.LastWriteTime);
            })
            .OrderByDescending(s => s.Captured)
            .ToList();
    }

    /// <summary>删除进 mods 同级的 .trash，不做硬删。</summary>
    public void Delete(Screenshot screenshot)
    {
        var trash = Path.Combine(paths.InstanceDir(InstanceIdOf(screenshot.FullPath)), ".trash", "screenshots");
        Directory.CreateDirectory(trash);

        var destination = Path.Combine(trash, screenshot.FileName);

        if (File.Exists(destination))
        {
            destination = Path.Combine(
                trash,
                $"{Path.GetFileNameWithoutExtension(screenshot.FileName)}-{DateTime.Now:HHmmss}{Path.GetExtension(screenshot.FileName)}");
        }

        File.Move(screenshot.FullPath, destination);
    }

    public void OpenFolder(string instanceId)
    {
        var directory = DirectoryFor(instanceId);
        Directory.CreateDirectory(directory);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }

    public static void OpenFile(Screenshot screenshot)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = screenshot.FullPath,
            UseShellExecute = true,
        });
    }

    /// <summary>从截图路径反推实例 ID（路径是 instances/&lt;id&gt;/screenshots/xxx.png）。</summary>
    private static string InstanceIdOf(string fullPath)
        => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(fullPath)) ?? string.Empty);
}
