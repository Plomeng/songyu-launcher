using System.IO;
using Songyu.Core.Download;

namespace Songyu.Services;

/// <summary>
/// 数据目录布局。绿色版：全部落在程序旁的 data 下，不碰用户目录。
/// </summary>
public sealed class MinecraftPaths
{
    public MinecraftPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string VersionsDir => Path.Combine(Root, "versions");

    public string LibrariesDir => Path.Combine(Root, "libraries");

    public string AssetsDir => Path.Combine(Root, "assets");

    public string InstancesDir => Path.Combine(Root, "instances");

    public string LogsDir => Path.Combine(Root, "logs");

    public string VersionDir(string versionId) => Path.Combine(VersionsDir, versionId);

    // ── 实例 ──
    // 版本是共享的程序文件（jar / libraries / assets），实例是各自独立的游戏目录
    // （mods / saves / config / 启动设置）。同一个版本可以开多个实例。

    public string InstanceDir(string instanceId) => Path.Combine(InstancesDir, instanceId);

    public string InstanceJsonPath(string instanceId) => Path.Combine(InstanceDir(instanceId), "instance.json");

    public string InstanceModsDir(string instanceId) => Path.Combine(InstanceDir(instanceId), "mods");

    public string InstanceSavesDir(string instanceId) => Path.Combine(InstanceDir(instanceId), "saves");

    public string VersionJsonPath(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".json");

    public string VersionJarPath(string versionId) => Path.Combine(VersionDir(versionId), versionId + ".jar");

    public string NativesDir(string versionId) => Path.Combine(VersionDir(versionId), "natives");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(VersionsDir);
        Directory.CreateDirectory(LibrariesDir);
        Directory.CreateDirectory(AssetsDir);
        Directory.CreateDirectory(InstancesDir);
        Directory.CreateDirectory(LogsDir);
    }
}

/// <summary>安装进度。Total 为 0 表示该阶段总量未知。</summary>
public sealed record InstallProgress(
    string Stage,
    int Completed,
    int Total,
    string? Current,
    double BytesPerSecond = 0)
{
    public double Percent => Total <= 0 ? 0 : (double)Completed / Total;

    public string SpeedText => SpeedMeter.FormatSpeed(BytesPerSecond);

    /// <summary>给界面用的一行文案。</summary>
    public string Describe()
    {
        if (Total <= 0)
        {
            return Stage;
        }

        var speed = BytesPerSecond > 1 ? $"  {SpeedText}" : string.Empty;
        var current = string.IsNullOrEmpty(Current) ? string.Empty : $"  {Current}";
        return $"{Stage}  {Completed}/{Total}  ({Percent:P0}){speed}{current}";
    }
}
