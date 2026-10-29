namespace Songyu.Core.Launch;

/// <summary>
/// 一次启动请求的全部输入。
/// 刻意做成不可变数据对象：启动参数生成是纯函数，输入确定则输出确定，便于测试与复现。
/// </summary>
public sealed class LaunchOptions
{
    /// <summary>要启动的版本 ID（对应 versions/&lt;id&gt;/）。</summary>
    public required string VersionId { get; init; }

    /// <summary>游戏工作目录（实例的 minecraft 文件夹）。</summary>
    public required string GameDirectory { get; init; }

    /// <summary>java.exe 绝对路径。</summary>
    public required string JavaExecutable { get; init; }

    public string PlayerName { get; init; } = "Player";

    public string Uuid { get; init; } = string.Empty;

    public string AccessToken { get; init; } = string.Empty;

    /// <summary>当前只使用 msa（微软账号）。</summary>
    public string UserType { get; init; } = "msa";

    public int MaxMemoryMb { get; init; } = 2048;

    public int MinMemoryMb { get; init; }

    public int? WindowWidth { get; init; }

    public int? WindowHeight { get; init; }

    public bool Fullscreen { get; init; }

    public IReadOnlyList<string> ExtraJvmArgs { get; init; } = [];

    public IReadOnlyList<string> ExtraGameArgs { get; init; } = [];
}
