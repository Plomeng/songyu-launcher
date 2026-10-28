using System.Diagnostics;
using System.Text;

namespace Songyu.Core.Launch;

/// <summary>
/// 启动 java 进程并把输出引到日志文件。
/// <para>
/// 用 <see cref="ProcessStartInfo.ArgumentList"/> 而不是手工拼字符串：
/// 游戏参数里会出现带空格或引号的路径，手工拼必然在某台机器上炸。
/// </para>
/// </summary>
public sealed class GameLauncher
{
    public static Process Start(LaunchCommand command, string? logFilePath = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.JavaExecutable,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        StreamWriter? logWriter = null;
        if (!string.IsNullOrEmpty(logFilePath))
        {
            var directory = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            logWriter = new StreamWriter(logFilePath, append: false, Encoding.UTF8) { AutoFlush = true };
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                logWriter?.WriteLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                logWriter?.WriteLine("[stderr] " + e.Data);
            }
        };

        process.Exited += (_, _) =>
        {
            logWriter?.WriteLine($"[exit] code={process.ExitCode} at {DateTimeOffset.Now:O}");
            logWriter?.Dispose();
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }
}
