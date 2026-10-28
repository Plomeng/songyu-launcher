using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using Songyu.Core.Launch;
using Songyu.Services;

// M1 端到端验证：安装原版 1.20.1 → 拼参 → 启动 → 观察 45 秒是否存活。
// 用法：M1Probe.exe [数据目录] [观察秒数]

Console.OutputEncoding = Encoding.UTF8;

var useFabric = args.Contains("--fabric", StringComparer.OrdinalIgnoreCase);
var useForge = args.Contains("--forge", StringComparer.OrdinalIgnoreCase);
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var root = positional.Length > 0 ? Path.GetFullPath(positional[0]) : @"F:\凇语MCloader\data";
var observeSeconds = positional.Length > 1 && int.TryParse(positional[1], out var s) ? s : 45;

var paths = new MinecraftPaths(root);
Console.WriteLine($"数据目录: {paths.Root}");

var javaService = new JavaService();
var javas = await javaService.ScanAsync();
if (javas.Count == 0)
{
    Console.WriteLine("没有找到 Java，终止。");
    return 2;
}

var java = javas[0];
Console.WriteLine($"使用 Java: {java}");

var config = AppConfig.Load(Path.Combine(paths.Root, "config.json"));
Console.WriteLine($"网络: {config.DescribeProxy()}");
using var http = config.CreateHttpClient();
var installer = new MinecraftInstaller(http, paths, config, trace: m => Console.WriteLine($"[网络] {m}"));

var stopwatch = Stopwatch.StartNew();
var lastLine = string.Empty;
var progress = new Progress<InstallProgress>(p =>
{
    var line = p.Total > 0
        ? $"{p.Stage}  {p.Completed}/{p.Total}  {p.Percent:P1}"
        : p.Stage;
    if (line != lastLine)
    {
        lastLine = line;
        Console.WriteLine($"[{stopwatch.Elapsed:mm\\:ss}] {line}");
    }
});

var launchOnly = args
    .FirstOrDefault(a => a.StartsWith("--launch=", StringComparison.OrdinalIgnoreCase))
    ?.Substring("--launch=".Length);

var mcmodTerm = args
    .FirstOrDefault(a => a.StartsWith("--mcmod=", StringComparison.OrdinalIgnoreCase))
    ?.Substring("--mcmod=".Length);

if (!string.IsNullOrEmpty(mcmodTerm))
{
    var localizer = new McmodLocalizer(http, paths);
    var chinese = await localizer.LookupAsync(mcmodTerm, mcmodTerm.ToLowerInvariant().Replace(' ', '-'));
    Console.WriteLine($"mcmod[{mcmodTerm}] = {(chinese ?? "(null，将显示英文原名)")}");
    return 0;
}

string versionId;

if (!string.IsNullOrEmpty(launchOnly))
{
    versionId = launchOnly;
    Console.WriteLine($"跳过安装，直接启动 {versionId}");
}
else
{
    Console.WriteLine("开始安装 1.20.1 …");
    await installer.InstallAsync("1.20.1", progress);
    Console.WriteLine($"安装完成，用时 {stopwatch.Elapsed:mm\\:ss}");

    versionId = "1.20.1";

    if (useFabric)
    {
        Console.WriteLine("获取 Fabric 加载器 …");
        var fabric = new MetaLoaderInstaller(http, paths, "https://meta.fabricmc.net/v2", "Fabric");
        versionId = await fabric.InstallAsync("1.20.1");
        Console.WriteLine($"Fabric 版本：{versionId}");

        await installer.EnsureLibrariesAsync(versionId, progress);
        Console.WriteLine("加载器依赖已就绪");
    }
    else if (useForge)
    {
        Console.WriteLine("开始安装 Forge（会运行官方安装器，可能几分钟）…");
        var forge = new ForgeInstaller(http, paths, config);
        versionId = await forge.InstallAsync(
            "1.20.1",
            javas[0].JavaExePath,
            new Progress<string>(s => Console.WriteLine($"  [forge] {s}")));
        Console.WriteLine($"Forge 版本：{versionId}");

        await installer.EnsureLibrariesAsync(versionId, progress);
        Console.WriteLine("加载器依赖已就绪");
    }
}

var gameDirectory = Path.Combine(paths.InstancesDir, "1.20.1");
Directory.CreateDirectory(gameDirectory);

var options = new LaunchOptions
{
    VersionId = versionId,
    GameDirectory = gameDirectory,
    JavaExecutable = java.JavaExePath,
    PlayerName = "SongyuTest",
    Uuid = OfflineUuid("SongyuTest"),
    AccessToken = "0",
    UserType = "legacy",
    MaxMemoryMb = 2048,
    WindowWidth = 1280,
    WindowHeight = 720,
};

var launcher = new LaunchService(paths);
Console.WriteLine("启动中 …");
var process = await launcher.LaunchAsync(versionId, options);
Console.WriteLine($"已启动，PID = {process.Id}，观察 {observeSeconds} 秒 …");

await Task.Delay(TimeSpan.FromSeconds(observeSeconds));

if (process.HasExited)
{
    Console.WriteLine($"[结果] 进程已退出，退出码 = {process.ExitCode}");
}
else
{
    Console.WriteLine("[结果] 进程存活 —— 启动成功");
    try
    {
        process.Kill(entireProcessTree: true);
    }
    catch
    {
        // 忽略
    }
}

var logFile = Directory.Exists(paths.LogsDir)
    ? new DirectoryInfo(paths.LogsDir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
    : null;

if (logFile is not null)
{
    Console.WriteLine($"\n===== 游戏日志尾部（{logFile.Name}）=====");
    // 游戏进程可能仍持有日志句柄，必须以 ReadWrite 共享方式打开。
    using var stream = new FileStream(logFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream);
    var text = await reader.ReadToEndAsync();
    foreach (var line in text.Split('\n').TakeLast(30))
    {
        Console.WriteLine(line.TrimEnd());
    }
}

return process.HasExited && process.ExitCode != 0 ? 1 : 0;

static string OfflineUuid(string name)
{
    var hash = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
    hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
    hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
    return Convert.ToHexStringLower(hash);
}
