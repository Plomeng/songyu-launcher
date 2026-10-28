using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Songyu.App.Pages;
using Songyu.Core.Launch;
using Songyu.Services;

namespace Songyu.App;

public partial class MainWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static readonly Brush NavActive = new SolidColorBrush(Color.FromArgb(0x22, 0xF2, 0xC1, 0x4E));

    private readonly AppServices _services;
    private readonly HomePage _homePage;
    private readonly DownloadPage _downloadPage;
    private readonly SettingsPage _settingsPage;
    private readonly VersionSettingsPage _versionSettingsPage;
    private readonly ModsPage _modsPage;
    private readonly ScreenshotsPage _screenshotsPage;
    private readonly ServersPage _serversPage;
    private readonly SkinPage _skinPage;

    /// <summary>从首页进 Mod 页时带的实例，Navigate 里消费掉。</summary>
    private string? _pendingModsInstanceId;
    private bool _showSelectedSkin;

    private CancellationTokenSource? _operationCts;

    public MainWindow()
    {
        InitializeComponent();

        _services = CreateServices();
        _homePage = new HomePage(_services);
        _downloadPage = new DownloadPage(_services);
        _settingsPage = new SettingsPage(_services);
        _versionSettingsPage = new VersionSettingsPage(_services);
        _modsPage = new ModsPage(_services);
        _screenshotsPage = new ScreenshotsPage(_services);
        _serversPage = new ServersPage(_services);

        _skinPage = new SkinPage(_services);

        Navigate(0, animate: false);
        Loaded += OnLoadedAsync;
    }

    private static AppServices CreateServices()
    {
        var paths = new MinecraftPaths(ResolveDataRoot());

        var configPath = Path.Combine(paths.Root, "config.json");
        if (!File.Exists(configPath))
        {
            // 首次运行落一份默认配置（ProxyMode=System），不写死任何代理地址。
            new AppConfig().Save(configPath);
        }

        var config = AppConfig.Load(configPath);

        var services = new AppServices
        {
            Paths = paths,
            Config = config,
            Http = config.CreateHttpClient(),
            JavaService = new JavaService(),
            LocalVersions = new LocalVersionService(paths),
        };

        // 实例模型迁移：把"版本即实例"的老数据补成正式实例文件。
        // 幂等且很便宜（只做 File.Exists），放在这里保证任何入口都先迁好。
        try
        {
            services.Instances.EnsureMigrated();
        }
        catch
        {
            // 迁移失败不能挡住启动器：老路径仍然可用
        }

        return services;
    }

    /// <summary>
    /// 数据目录优先级：仓库根的 data → exe 旁的 data → 用户本地应用数据。
    /// 最后一级是必要兜底：某些受限目录带「拒绝删除子项」规则，
    /// 而 Fabric 的 jar 重映射必须能写临时文件再重命名。
    /// </summary>
    private static string ResolveDataRoot()
    {
        var candidate = FindDevelopmentRoot() is { } repo
            ? Path.Combine(repo, "data")
            : Path.Combine(AppContext.BaseDirectory, "data");

        if (IsWritableDirectory(candidate))
        {
            return candidate;
        }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Songyu");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static string? FindDevelopmentRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Songyu.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static bool IsWritableDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".songyu-write-probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        ShowToast("正在探测本机 Java…");
        try
        {
            _services.JavaInstallations = await _services.JavaService.ScanAsync();
        }
        catch
        {
            // 探测失败不阻断，RefreshStatus 会显示"未找到 Java"
        }

        RefreshStatus();
        _settingsPage.Refresh();

        // 首页在构造时就渲染过一遍，那时 Java 还没扫完。
        // 不刷这一下，界面会一直挂着"没有检测到 Java"的警告。
        _homePage.Refresh();
    }

    /// <summary>
    /// 底部状态栏平时留空，只在实际操作时显示进度（探测 Java、安装、启动）。
    /// 之前常驻「Java 21 · 7 个版本 · 代理 · 镜像」这类汇总信息，占着位置但没什么用。
    /// </summary>
    private void RefreshStatus()
    {
        ClearToast();
    }

    // ── 导航 ──

    private void Navigate(int index, bool animate = true)
    {
        FrameworkElement page = index switch
        {
            0 => _homePage,
            1 => _downloadPage,
            2 => _modsPage,
            4 => _screenshotsPage,
            5 => _serversPage,
            6 => _skinPage,
            _ => _settingsPage,
        };

        if (!ReferenceEquals(ContentHost.Content, page))
        {
            ContentHost.Content = page;

            if (animate)
            {
                AnimateIn(page);
            }
        }

        HomeNav.Background = index == 0 ? NavActive : Brushes.Transparent;
        DownloadNav.Background = index == 1 ? NavActive : Brushes.Transparent;
        ModsNav.Background = index == 2 ? NavActive : Brushes.Transparent;
        ShotsNav.Background = index == 4 ? NavActive : Brushes.Transparent;
        ServersNav.Background = index == 5 ? NavActive : Brushes.Transparent;
        SkinNav.Background = index == 6 ? NavActive : Brushes.Transparent;
        SettingsNav.Background = index == 3 ? NavActive : Brushes.Transparent;

        switch (index)
        {
            case 0:
                _homePage.Refresh();
                break;
            case 2:
                // 从首页点「Mod 管理」带过来的实例优先；侧边导航进来则用首页当前选中的
                _modsPage.Load(_pendingModsInstanceId ?? _homePage.SelectedInstanceId);
                _pendingModsInstanceId = null;
                break;
            case 3:
                _settingsPage.Refresh();
                break;
            case 4:
                _screenshotsPage.Load(_homePage.SelectedInstanceId);
                break;
            case 5:
                _serversPage.Load(_homePage.SelectedInstanceId);
                break;
            case 6:
                _skinPage.Load(_showSelectedSkin);
                _showSelectedSkin = false;
                break;
        }
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index))
        {
            Navigate(index);
        }
    }

    /// <summary>从启动页进入某个实例的设置页（参数是实例 ID）。</summary>
    public void ShowVersionSettings(string instanceId)
    {
        _versionSettingsPage.Load(instanceId);

        ContentHost.Content = _versionSettingsPage;
        AnimateIn(_versionSettingsPage);

        // 设置页不属于三个主入口，导航高亮全部取消
        ClearNavHighlight();
    }

    public void NavigateHome() => Navigate(0);

    public void NavigateToDownload() => Navigate(1);

    public void NavigateToSettings() => Navigate(3);

    public void NavigateToSkin()
    {
        _showSelectedSkin = true;
        Navigate(6);
    }

    /// <summary>进 Mod 管理页；给了实例就直接切到那个实例（由 Navigate 统一加载，避免扫两遍）。</summary>
    public void NavigateToMods(string? instanceId = null)
    {
        _pendingModsInstanceId = instanceId;
        Navigate(2);
    }

    /// <summary>刷新所有依赖账号/配置状态的页面。</summary>
    public void RefreshAllPages()
    {
        RefreshStatus();
        _homePage.Refresh();
        _settingsPage.Refresh();
    }

    // ── 账号 ──

    /// <summary>点击首页头像时调用：已登录问是否退出，未登录问是否登录。</summary>
    public async void ShowAccountPanel()
    {
        var store = _services.CreateAccountStore();
        var account = store.Load();

        if (account?.Name is { Length: > 0 } name)
        {
            var confirmed = await ShowConfirmAsync(
                "退出登录",
                $"当前登录：{name}\n\n退出后改用离线账号，进不了正版服务器。",
                "退出登录",
                "取消");

            if (confirmed)
            {
                store.Clear();
                RefreshAllPages();
                ShowDialog("已退出", "现在使用离线账号。");
            }

            return;
        }

        var login = await ShowConfirmAsync(
            "账号",
            "现在用的是离线账号：进不了正版服务器，也显示不了你的正版皮肤和披风。\n\n" +
            "登录微软账号后，启动时会以正版身份进入游戏。\n\n" +
            "支持正版，建议用正版账号游玩。",
            "登录正版",
            "暂时离线");

        if (login)
        {
            await LoginMicrosoftAsync();
        }
    }

    /// <summary>把设备码塞进剪贴板。失败不阻断登录，界面会改成让用户手动输入。</summary>
    private static bool TryCopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch
        {
            // 剪贴板被别的程序占用时会抛异常，这时退回手动输入
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr ShellExecuteW(
        IntPtr hwnd, string? operation, string file, string? parameters, string? directory, int showCommand);

    /// <summary>日志路径，调试浏览器启动问题时用。</summary>
    private static string BrowserLogPath => Path.Combine(AppContext.BaseDirectory, "browser-error.log");

    private static void LogBrowser(string message)
    {
        try
        {
            File.AppendAllText(BrowserLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不进去不影响主流程
        }
    }

    /// <summary>
    /// 打开默认浏览器。
    /// <para>
    /// 实测这台机器上 <c>Process.Start</c>（不管是 exe 直启还是 ShellExecute）都会
    /// 返回成功却不真正创建进程，所以这里三层依次尝试，并且**每一层都记日志**：
    /// ① 从注册表拿到浏览器 exe 的绝对路径直接启动；
    /// ② ShellExecuteW（Win32 API，比 .NET 封装更底层）；
    /// ③ 最后才用 Process.Start(url) 走协议。
    /// 每次都轮询浏览器进程数确认真的出现，避免"假成功"骗到用户。
    /// </para>
    /// </summary>
    private static async Task<bool> TryOpenBrowserAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var before = CountBrowserProcesses();
        LogBrowser($"开始打开: {url}  当前浏览器进程 {before} 个");

        // ① 注册表里的浏览器 exe
        var exe = GetDefaultBrowserExecutable();
        LogBrowser($"① 注册表解析 exe = {exe ?? "(null)"}");

        if (exe is not null)
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo(exe, $"\"{url}\"") { UseShellExecute = false });
                LogBrowser($"① Process.Start 返回 pid={proc?.Id.ToString() ?? "null"}");

                if (await WaitForBrowserAsync(before))
                {
                    LogBrowser("① 成功：浏览器进程已出现");
                    return true;
                }

                LogBrowser("① 进程未出现");
            }
            catch (Exception ex)
            {
                LogBrowser($"① 抛异常 {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ② ShellExecuteW
        try
        {
            var result = ShellExecuteW(IntPtr.Zero, "open", url, null, null, 1);
            var code = result.ToInt64();
            LogBrowser($"② ShellExecuteW 返回 {code}（>32 为成功）");

            if (await WaitForBrowserAsync(before))
            {
                LogBrowser("② 成功：浏览器进程已出现");
                return true;
            }

            LogBrowser("② 进程未出现");
        }
        catch (Exception ex)
        {
            LogBrowser($"② 抛异常 {ex.GetType().Name}: {ex.Message}");
        }

        // ③ 最后一次：.NET 的协议启动
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            LogBrowser("③ Process.Start(url) 已调用");

            if (await WaitForBrowserAsync(before))
            {
                LogBrowser("③ 成功：浏览器进程已出现");
                return true;
            }

            LogBrowser("③ 进程未出现");
        }
        catch (Exception ex)
        {
            LogBrowser($"③ 抛异常 {ex.GetType().Name}: {ex.Message}");
        }

        LogBrowser("全部方式失败");
        return false;
    }

    /// <summary>最多等 3 秒，浏览器进程数一旦超过基准就认为打开了。</summary>
    private static async Task<bool> WaitForBrowserAsync(int baseline)
    {
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(100);

            if (CountBrowserProcesses() > baseline)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>统计常见浏览器的进程数，用来判断「到底有没有真的打开」。</summary>
    private static int CountBrowserProcesses()
    {
        var count = 0;

        foreach (var name in new[]
                 {
                     "msedge", "chrome", "firefox", "360se", "360ChromeX",
                     "QQBrowser", "opera", "brave", "vivaldi", "iexplore",
                 })
        {
            try
            {
                count += Process.GetProcessesByName(name).Length;
            }
            catch
            {
                // 某个名字查不到就算了
            }
        }

        return count;
    }

    /// <summary>从注册表读出 https 协议关联的浏览器可执行文件路径。</summary>
    private static string? GetDefaultBrowserExecutable()
    {
        try
        {
            using var userChoice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");

            if (userChoice?.GetValue("ProgId") is not string progId || string.IsNullOrWhiteSpace(progId))
            {
                return null;
            }

            using var commandKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            if (commandKey?.GetValue(null) is not string command || string.IsNullOrWhiteSpace(command))
            {
                return null;
            }

            // 形如 "C:\...\msedge.exe" --single-argument %1
            if (command.StartsWith('"'))
            {
                var closing = command.IndexOf('"', 1);
                if (closing > 1)
                {
                    var quoted = command.Substring(1, closing - 1);
                    if (File.Exists(quoted))
                    {
                        return quoted;
                    }
                }
            }

            // 没有引号：截到 .exe 为止
            var exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeEnd > 0)
            {
                var bare = command[..(exeEnd + 4)].Trim();
                if (File.Exists(bare))
                {
                    return bare;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>微软登录全流程：设备码 → 轮询 → 换 Minecraft 令牌 → 落盘。</summary>
    public async Task LoginMicrosoftAsync()
    {
        var auth = _services.CreateMicrosoftAuth();

        if (!auth.IsConfigured)
        {
            var openSettings = await ShowConfirmAsync(
                "还没有配置 Client ID",
                "登录需要至少一个 Azure 应用的 client id。\n\n" +
                "注册步骤（免费）：\n" +
                "1. portal.azure.com → 应用注册 → 新注册\n" +
                "2. 账户类型选「任何组织目录中的账户和个人账户」\n" +
                "3. 复制「应用程序(客户端) ID」\n" +
                "4. 「身份验证」→ 允许公共客户端流 → 是\n" +
                "5. 填到 设置 → 正版账号 → Client ID\n\n" +
                "可以填多行，第一个失效会自动换下一个。\n\n" +
                "要现在去填写吗？",
                "去填写",
                "先跳过");

            if (openSettings)
            {
                NavigateToSettings();
            }

            return;
        }

        try
        {
            ShowToast("正在申请登录码…");
            var deviceCode = await auth.StartDeviceCodeAsync();

            // 自动打开是尽力而为：这台机器上进程会被创建又立刻退出，
            // 所以弹窗里始终给出可复制的地址和验证码。
            await TryOpenBrowserAsync(deviceCode.VerificationUri);

            TryCopyToClipboard(deviceCode.UserCode);
            ShowLoginDialog(deviceCode.VerificationUri, deviceCode.UserCode);

            ShowToast("等待网页授权…");
            var token = await auth.PollForTokenAsync(deviceCode);

            ShowToast("正在获取 Minecraft 档案…");
            var profile = await auth.LoginToMinecraftAsync(token.AccessToken);

            _services.CreateAccountStore().Save(new AccountStore.AccountData
            {
                RefreshToken = token.RefreshToken,
                MinecraftAccessToken = profile.AccessToken,
                Uuid = profile.Uuid,
                Name = profile.Name,
                MinecraftExpiresAt = profile.ExpiresAt,
                SkinUrl = profile.SkinUrl,
                CapeUrl = profile.CapeUrl,
            });

            RefreshAllPages();
            CloseLoginDialog();

            var poolNote = auth.Total > 1
                ? $"\n\n（用的是第 {auth.CurrentIndex}/{auth.Total} 个 client id）"
                : string.Empty;

            ShowDialog("登录成功", $"欢迎，{profile.Name}。\n\n之后启动会用这个正版账号。{poolNote}");
        }
        catch (OperationCanceledException)
        {
            CloseLoginDialog();
            ShowToast("已取消登录");
        }
        catch (Exception ex)
        {
            CloseLoginDialog();
            ShowToast("登录失败：" + ex.Message);
            ShowDialog("登录失败", ex.Message, allowCopy: true);
        }
    }

    public void RefreshAfterChange()
    {
        RefreshStatus();
        _homePage.Refresh();
    }

    /// <summary>配置改动后刷新所有依赖配置的显示。</summary>
    public void RefreshConfigDependentState()
    {
        RefreshStatus();
        _homePage.Refresh();
    }

    private void ClearNavHighlight()
    {
        HomeNav.Background = Brushes.Transparent;
        DownloadNav.Background = Brushes.Transparent;
        ModsNav.Background = Brushes.Transparent;
        SettingsNav.Background = Brushes.Transparent;
    }

    /// <summary>页面淡入 + 轻微上移，220ms ease-out。时长压得短，切页才不觉得拖。</summary>
    private static void AnimateIn(UIElement view)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(220);

        var translate = new TranslateTransform(0, 10);
        view.RenderTransform = translate;
        view.Opacity = 0;

        view.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, duration) { EasingFunction = ease });

        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, duration) { EasingFunction = ease });
    }

    // ── 启动与安装 ──

    /// <summary>
    /// 按版本启动：自动映射到该版本的默认实例。
    /// 老调用点（下载页装完直接启动、首页按版本点启动）都走这里，行为不变。
    /// </summary>
    public Task RunVersionAsync(string versionId)
        => RunInstanceAsync(_services.Instances.EnsureForVersion(versionId).Id);

    /// <summary>
    /// 按实例启动。游戏目录用的是实例自己的目录，
    /// 所以同一个版本开两个实例，mods 和存档互不干扰。
    /// </summary>
    public async Task RunInstanceAsync(string instanceId, IReadOnlyList<string>? extraGameArgs = null)
    {
        var instance = _services.Instances.Get(instanceId);
        if (instance is null)
        {
            ShowDialog("无法启动", $"实例「{instanceId}」不存在。", allowCopy: true);
            return;
        }

        var java = _services.BestJava;
        if (java is null)
        {
            ShowDialog("无法启动", "没有可用的 Java，请先安装 JDK 17 或更高版本。", allowCopy: true);
            return;
        }

        try
        {
            DownloadBar.Value = 0;
            ShowToast($"正在启动 {instance.Name}…");

            var settings = instance.Settings;
            var gameDirectory = _services.Instances.EnsureGameDirectory(instanceId);

            // 设置里指定了 Java 就用它；文件已不存在时回退到自动选择
            var javaPath = !string.IsNullOrWhiteSpace(settings.JavaPath) && File.Exists(settings.JavaPath)
                ? settings.JavaPath
                : java.JavaExePath;

            var offlineName = string.IsNullOrWhiteSpace(_services.Config.PlayerName)
                ? "Player"
                : _services.Config.PlayerName;

            var account = await ResolveAccountAsync(offlineName);

            var options = new LaunchOptions
            {
                VersionId = instance.VersionId,
                GameDirectory = gameDirectory,
                JavaExecutable = javaPath,
                PlayerName = account.Name,
                Uuid = account.Uuid,
                AccessToken = account.Token,
                UserType = account.UserType,
                MaxMemoryMb = settings.MaxMemoryMb,
                MinMemoryMb = settings.MinMemoryMb,
                WindowWidth = settings.WindowWidth,
                WindowHeight = settings.WindowHeight,
                Fullscreen = settings.Fullscreen,
                ExtraJvmArgs = settings.ParseExtraJvmArgs(),
                ExtraGameArgs = extraGameArgs ?? [],
            };

            var launcher = _services.CreateLauncher();
            var process = await launcher.LaunchAsync(instance.VersionId, options);

            instance.LastPlayed = DateTimeOffset.Now;
            _services.Instances.Save(instance);

            DownloadBar.Value = 1;
            ShowToast($"已启动 {instance.Name}（PID {process.Id}）");
            ShowDialog(
                "已启动",
                $"Minecraft 正在运行（PID {process.Id}）。\n\n" +
                $"实例：{instance.Name}\n版本：{instance.VersionId}\n" +
                $"游戏目录：{gameDirectory}\n日志：{_services.Paths.LogsDir}",
                allowCopy: true);
        }
        catch (Exception ex)
        {
            ShowToast("启动失败：" + ex.Message);
            ShowDialog("启动失败", ex.Message, allowCopy: true);
        }
    }

    /// <summary>
    /// 一次装完：先保证原版文件齐全，再叠加所选加载器。
    /// Fabric / Quilt 是纯元数据，装完只需补库；Forge / NeoForge 要走官方安装器，暂未支持。
    /// </summary>
    public Task InstallVersionAsync(string versionId, LoaderKind loader)
    {
        if (!AppServices.IsSupported(loader))
        {
            ShowDialog("暂不支持", $"{loader} 需要运行官方安装器，会在后续版本加入。");
            return Task.CompletedTask;
        }

        return EnqueueAsync($"安装 {versionId}", token => InstallVersionCoreAsync(versionId, loader, token));
    }

    private async Task InstallVersionCoreAsync(string versionId, LoaderKind loader, CancellationToken token)
    {
        DownloadBar.Value = 0;
        var installer = _services.CreateInstaller();

        ShowToast($"正在安装 {versionId}…");
        await installer.InstallAsync(versionId, CreateProgress(), token);

        if (loader == LoaderKind.Vanilla)
        {
            RefreshStatus();
            ShowDialog("安装完成", $"{versionId} 已就绪，可在启动页启动。");
            return;
        }

        if (loader == LoaderKind.Forge)
        {
            var java = _services.BestJava
                       ?? throw new InvalidOperationException("安装 Forge 需要 Java，但没有检测到可用版本。");

            var forge = _services.CreateForgeInstaller();
            var forgeLog = new Progress<string>(s => ShowToast(s));
            var forgeId = await forge.InstallAsync(versionId, java.JavaExePath, forgeLog, token);

            ShowToast($"正在补齐 {forgeId} 的依赖…");
            await installer.EnsureLibrariesAsync(forgeId, CreateProgress(), token);

            RefreshStatus();
            ShowDialog("安装完成", $"已安装 {forgeId}，可在启动页启动。");
            return;
        }

        if (loader == LoaderKind.NeoForge)
        {
            var java = _services.BestJava
                       ?? throw new InvalidOperationException("安装 NeoForge 需要 Java，但没有检测到可用版本。");

            var neo = _services.CreateNeoForgeInstaller();
            var neoLog = new Progress<string>(s => ShowToast(s));
            var neoId = await neo.InstallAsync(versionId, java.JavaExePath, neoLog, token);

            ShowToast($"正在补齐 {neoId} 的依赖…");
            await installer.EnsureLibrariesAsync(neoId, CreateProgress(), token);

            RefreshStatus();
            ShowDialog("安装完成", $"已安装 {neoId}，可在启动页启动。");
            return;
        }

        var meta = _services.CreateMetaLoader(loader)
                   ?? throw new InvalidOperationException($"{loader} 暂不受支持。");

        ShowToast($"正在获取 {meta.DisplayName} 加载器…");
        var loaderVersionId = await meta.InstallAsync(versionId, null, token);

        ShowToast($"正在补齐 {loaderVersionId} 的依赖…");
        await installer.EnsureLibrariesAsync(loaderVersionId, CreateProgress(), token);

        RefreshStatus();
        ShowDialog("安装完成", $"已安装 {loaderVersionId}，可在启动页启动。");
    }

    private IProgress<InstallProgress> CreateProgress()
        => new Progress<InstallProgress>(p =>
        {
            DownloadBar.Value = p.Percent;
            DownloadPercentText.Text = $"{p.Percent:P0}";
            DownloadFileText.Text = p.Describe();
        });

    /// <summary>
    /// 让 Windows 11 用系统圆角渲染窗口（DWM 会把整窗裁圆，包括自定义标题栏）。
    /// 老系统没有这个属性，调用失败就当没发生——外层的 Border 仍然有圆角边框。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // 非 Win11 环境，忽略
        }
    }

    // ── 长操作：队列 + 取消 ──

    private readonly Queue<(string Name, Func<CancellationToken, Task> Work)> _operationQueue = new();
    private bool _workerRunning;

    /// <summary>
    /// 把操作排进队列。同一时刻只跑一个，后来的排队而不是互相打断。
    /// <para>
    /// 必须串行：两个任务同时写同一个 versions/ 或 mods/ 目录会互相踩。
    /// 用户连点几个 Mod 时看到的是「当前 xxx，队列 N」。
    /// </para>
    /// </summary>
    public async Task EnqueueAsync(string name, Func<CancellationToken, Task> work)
    {
        _operationQueue.Enqueue((name, work));
        UpdateQueueStatus();

        if (_workerRunning)
        {
            return;
        }

        _workerRunning = true;

        try
        {
            while (_operationQueue.Count > 0)
            {
                var (taskName, task) = _operationQueue.Dequeue();
                await RunOneAsync(taskName, task);
            }
        }
        finally
        {
            _workerRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
            HideDownloadCard();
            UpdateQueueStatus();
        }
    }

    private async Task RunOneAsync(string name, Func<CancellationToken, Task> work)
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();

        ShowDownloadCard(name);
        ShowToast($"正在执行：{name}");

        try
        {
            await work(_operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            ShowToast($"已取消：{name}");
        }
        catch (Exception ex)
        {
            ShowToast($"{name} 失败：{ex.Message}");
            ShowDialog($"{name} 失败", ex.Message);
        }
        finally
        {
            UpdateQueueStatus();
        }
    }

    // ── 下载浮窗 ──

    private bool _downloadCardCollapsed;

    private void ShowDownloadCard(string title)
    {
        DownloadCardTitle.Text = title;
        DownloadPercentText.Text = string.Empty;
        DownloadFileText.Text = string.Empty;
        DownloadSpeedText.Text = string.Empty;
        DownloadBar.Value = 0;

        DownloadCardBody.Visibility = _downloadCardCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.Content = _downloadCardCollapsed ? "⌃" : "⌄";

        if (DownloadCard.Visibility == Visibility.Visible)
        {
            return;
        }

        DownloadCard.Visibility = Visibility.Visible;

        // 从下方浮上来，180ms
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);
        var translate = new TranslateTransform(0, 16);

        DownloadCard.RenderTransform = translate;
        DownloadCard.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        translate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(16, 0, duration) { EasingFunction = ease });
    }

    private void HideDownloadCard()
    {
        DownloadCard.Visibility = Visibility.Collapsed;
        DownloadBar.Value = 0;
    }

    private void OnCollapseDownloadClick(object sender, RoutedEventArgs e)
    {
        _downloadCardCollapsed = !_downloadCardCollapsed;

        DownloadCardBody.Visibility = _downloadCardCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseButton.Content = _downloadCardCollapsed ? "⌃" : "⌄";
    }

    private void UpdateQueueStatus()
    {
        if (_workerRunning && _operationQueue.Count > 0)
        {
            ShowToast($"（队列 {_operationQueue.Count}）");
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        // 取消当前任务，并把排队的也一起清掉——用户点取消通常是「全都停下」
        _operationQueue.Clear();
        _operationCts?.Cancel();
        ShowToast("正在取消…");
    }

    private DispatcherTimer? _toastTimer;

    /// <summary>
    /// 底部浮出一条提示，几秒后自动消失。
    /// 替代原来的常驻状态栏——那行即使空着也占一块地方。
    /// </summary>
    public void ShowToast(string message, int milliseconds = 2600)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        ToastText.Text = message;
        ToastHost.BeginAnimation(OpacityProperty, null);
        ToastHost.Opacity = 1;

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            ToastHost.Opacity = 0;
        };
        _toastTimer.Start();
    }

    /// <summary>立刻收掉提示（切页面或开始新操作时不让上一条残留）。</summary>
    public void ClearToast()
    {
        _toastTimer?.Stop();
        ToastHost.Opacity = 0;
    }
    /// <summary>把下载进度推到浮窗（文件名 + 字节 + 速度）。</summary>
    public IProgress<Core.Download.DownloadProgress> CreateDownloadProgress()        => new Progress<Core.Download.DownloadProgress>(p =>
        {
            if (p.Total is > 0)
            {
                DownloadBar.Value = (double)p.Received / p.Total.Value;
                DownloadPercentText.Text = $"{DownloadBar.Value:P0}";
            }

            var speed = p.BytesPerSecond >= 1024 * 1024
                ? $"{p.BytesPerSecond / 1024 / 1024:F1} MB/s"
                : $"{p.BytesPerSecond / 1024:F0} KB/s";

            var size = p.Total is > 0
                ? $"{p.Received / 1024d / 1024:F1} / {p.Total.Value / 1024d / 1024:F1} MB"
                : $"{p.Received / 1024d / 1024:F1} MB";

            DownloadFileText.Text = p.CurrentFile ?? string.Empty;
            DownloadSpeedText.Text = $"{size}   {speed}";
        });

    // ── 窗口外壳 ──

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed && WindowState != WindowState.Maximized)
        {
            DragMove();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ── 应用内弹窗 ──

    private TaskCompletionSource<bool>? _dialogResult;
    private TaskCompletionSource<string?>? _inputResult;

    public void ShowDialog(string title, string message, bool allowCopy = false)
    {
        ShowDialogCore(title, message, "知道了", null, allowCopy);
    }

    /// <summary>
    /// 应用内二选一弹窗。系统 MessageBox 和这套界面对不上，账号切换、删除确认都用这个。
    /// </summary>
    public Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string confirmText,
        string cancelText,
        bool allowCopy = false)
    {
        ShowDialogCore(title, message, confirmText, cancelText, allowCopy);
        _dialogResult = new TaskCompletionSource<bool>();
        return _dialogResult.Task;
    }

    private void ShowDialogCore(
        string title,
        string message,
        string primaryText,
        string? secondaryText,
        bool allowCopy)
    {
        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogPrimary.Content = primaryText;

        // 普通提示 / 确认弹窗不带输入框
        DialogInput.Visibility = Visibility.Collapsed;
        DialogInput.Text = string.Empty;

        // 只有地址、路径、错误码这类需要转发给别人的内容才给复制按钮；
        // 纯提示和确认弹窗不需要，多一个图标反而碍眼。
        DialogCopyButton.Visibility = allowCopy ? Visibility.Visible : Visibility.Collapsed;

        if (secondaryText is null)
        {
            DialogSecondary.Visibility = Visibility.Collapsed;
        }
        else
        {
            DialogSecondary.Content = secondaryText;
            DialogSecondary.Visibility = Visibility.Visible;
        }

        DialogLayer.Visibility = Visibility.Visible;

        // 弹窗从 94% 放大到 100%，180ms。比直接出现更像"弹"出来。
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);

        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
    }

    /// <summary>
    /// 要用户输入一行文字的弹窗（新建实例、重命名）。取消返回 null。
    /// 点「确定」时若输入为空，也按取消处理。
    /// </summary>
    public Task<string?> ShowInputAsync(
        string title,
        string message,
        string? defaultValue = null,
        string? hint = null)
    {
        ShowDialogCore(title, message, "确定", "取消", allowCopy: false);

        DialogInput.Visibility = Visibility.Visible;
        DialogInput.Text = defaultValue ?? string.Empty;
        DialogInput.ToolTip = hint;

        _inputResult = new TaskCompletionSource<string?>();

        // 弹窗一出来就选中默认文字，直接打字即可覆盖
        DialogInput.Focus();
        DialogInput.SelectAll();

        return _inputResult.Task;
    }

    /// <summary>输入框里按回车等于点「确定」。</summary>
    private void OnDialogInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnDialogPrimaryClick(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnDialogSecondaryClick(sender, e);
        }
    }

    /// <summary>显示登录专用弹窗：地址和验证码分开显示，各自带复制按钮。</summary>
    private void ShowLoginDialog(string url, string code)
    {
        LoginDialogUrl.Text = url;
        LoginDialogCode.Text = code;
        LoginDialogLayer.Visibility = Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);

        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
    }

    private void CloseLoginDialog()
    {
        LoginDialogLayer.Visibility = Visibility.Collapsed;
    }

    private void OnCloseLoginDialogClick(object sender, RoutedEventArgs e) => CloseLoginDialog();

    private void OnCopyLoginUrlClick(object sender, RoutedEventArgs e)
        => CopyWithFeedback(LoginDialogUrl.Text, LoginCopyUrlButton);

    private void OnCopyLoginCodeClick(object sender, RoutedEventArgs e)
        => CopyWithFeedback(LoginDialogCode.Text, LoginCopyCodeButton);

    /// <summary>复制并让图标短暂变成对勾，给一个"确实复制到了"的反馈。</summary>
    private static void CopyWithFeedback(string text, Button button)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            return;
        }

        button.Content = "\uE73E";

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            button.Content = "\uE8C8";
        };
        timer.Start();
    }

    private void OnDialogPrimaryClick(object sender, RoutedEventArgs e)
    {
        var confirmed = _dialogResult;
        var input = _inputResult;

        var text = DialogInput.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(DialogInput.Text)
            ? DialogInput.Text.Trim()
            : null;

        DialogLayer.Visibility = Visibility.Collapsed;
        DialogInput.Visibility = Visibility.Collapsed;
        DialogInput.Text = string.Empty;
        _dialogResult = null;
        _inputResult = null;

        confirmed?.TrySetResult(true);
        input?.TrySetResult(text);
    }

    /// <summary>
    /// 复制弹窗正文。点一下图标短暂变成对勾，让用户知道复制成功了；
    /// 图标来自 Segoe MDL2 Assets（E8C8 复制 / E73E 对勾）。
    /// </summary>
    private void OnDialogCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DialogMessage.Text);
        }
        catch
        {
            // 剪贴板被别的程序占着，静默失败即可
            return;
        }

        DialogCopyButton.Content = "\uE73E";

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DialogCopyButton.Content = "\uE8C8";
        };
        timer.Start();
    }

    private void OnDialogSecondaryClick(object sender, RoutedEventArgs e)
    {
        var confirmed = _dialogResult;
        var input = _inputResult;

        DialogLayer.Visibility = Visibility.Collapsed;
        DialogInput.Visibility = Visibility.Collapsed;
        DialogInput.Text = string.Empty;
        _dialogResult = null;
        _inputResult = null;

        confirmed?.TrySetResult(false);
        input?.TrySetResult(null);
    }

    /// <summary>点遮罩不关闭——避免误触把关键错误信息关掉。</summary>
    private void OnDialogLayerMouseDown(object sender, MouseButtonEventArgs e)
    {
    }

    /// <summary>
    /// 决定这次用哪个身份启动。
    /// 有正版令牌且没过期就直接用；过期就用刷新令牌换新的；
    /// 刷新失败一律退回离线，不能因为登录问题让游戏启动不了。
    /// </summary>
    private async Task<(string Name, string Uuid, string Token, string UserType)> ResolveAccountAsync(
        string offlineName)
    {
        var store = _services.CreateAccountStore();
        var account = store.Load();

        if (account is null || string.IsNullOrEmpty(account.RefreshToken))
        {
            return (offlineName, OfflineUuid(offlineName), "0", "legacy");
        }

        if (!string.IsNullOrEmpty(account.MinecraftAccessToken) &&
            !string.IsNullOrEmpty(account.Uuid) &&
            account.MinecraftExpiresAt is { } expires &&
            expires > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            ShowToast($"使用正版账号 {account.Name}");
            return (account.Name ?? offlineName, account.Uuid, account.MinecraftAccessToken, "msa");
        }

        try
        {
            ShowToast("正版令牌已过期，正在刷新…");

            var auth = _services.CreateMicrosoftAuth();
            var microsoftToken = await auth.RefreshAsync(account.RefreshToken);
            var profile = await auth.LoginToMinecraftAsync(microsoftToken.AccessToken);

            store.Save(new AccountStore.AccountData
            {
                RefreshToken = microsoftToken.RefreshToken,
                MinecraftAccessToken = profile.AccessToken,
                Uuid = profile.Uuid,
                Name = profile.Name,
                MinecraftExpiresAt = profile.ExpiresAt,
            });

            ShowToast($"使用正版账号 {profile.Name}");
            return (profile.Name, profile.Uuid, profile.AccessToken, "msa");
        }
        catch
        {
            ShowToast("正版令牌刷新失败，本次以离线身份启动");
            return (offlineName, OfflineUuid(offlineName), "0", "legacy");
        }
    }

    private static string OfflineUuid(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return Convert.ToHexStringLower(hash);
    }

    // ── 拖放安装 ──

    /// <summary>拖进来的东西分成哪几类。</summary>
    private enum DropKind
    {
        None,
        Modpack,
        ModJar,
        ResourcePack,
        ShaderPack,
    }

    private DropKind _dragKind = DropKind.None;

    private void OnWindowDragEnter(object sender, DragEventArgs e)
    {
        _dragKind = DropKind.None;
        UpdateDropFeedback(e);
    }

    private void OnWindowDragOver(object sender, DragEventArgs e) => UpdateDropFeedback(e);

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        _dragKind = DropKind.None;
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void UpdateDropFeedback(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);

        // 拖放事件触发很频繁，分类只做一次（zip 会真的去开文件）
        if (_dragKind == DropKind.None)
        {
            _dragKind = ClassifyDrop(files);
        }

        if (_dragKind == DropKind.None)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var instance = _homePage.SelectedInstanceId ?? "（还没选实例）";

        (DropTitleText.Text, DropHintText.Text) = _dragKind switch
        {
            DropKind.Modpack => (
                "松手安装整合包",
                $"{Path.GetFileName(files[0])}\n会新建一个实例；本地已有的文件直接复用，不重复下载"),

            DropKind.ModJar => (
                $"松手导入 {files.Length} 个 Mod",
                $"装到当前实例：{instance}"),

            DropKind.ResourcePack => (
                $"松手导入 {files.Length} 个资源包",
                $"装到当前实例：{instance}"),

            DropKind.ShaderPack => (
                $"松手导入 {files.Length} 个光影包",
                $"装到当前实例：{instance}"),

            _ => (string.Empty, string.Empty),
        };

        DropOverlay.Visibility = Visibility.Visible;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private static DropKind ClassifyDrop(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return DropKind.None;
        }

        var first = files[0];

        if (!File.Exists(first))
        {
            return DropKind.None;
        }

        var extension = Path.GetExtension(first).ToLowerInvariant();

        return extension switch
        {
            ".mrpack" => DropKind.Modpack,
            ".jar" => DropKind.ModJar,
            ".zip" => InspectZip(first),
            _ => DropKind.None,
        };
    }

    /// <summary>
    /// 压缩包要看内容才知道是什么：整合包有两种索引文件，
    /// 资源包有 pack.mcmeta，光影包是 shaders/ 目录。
    /// </summary>
    private static DropKind InspectZip(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);

            if (archive.GetEntry("modrinth.index.json") is not null ||
                archive.GetEntry("manifest.json") is not null)
            {
                return DropKind.Modpack;
            }

            if (archive.Entries.Any(e => e.FullName.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase)))
            {
                return DropKind.ShaderPack;
            }

            if (archive.GetEntry("pack.mcmeta") is not null)
            {
                return DropKind.ResourcePack;
            }

            return DropKind.None;
        }
        catch
        {
            return DropKind.None;
        }
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        var kind = _dragKind;
        _dragKind = DropKind.None;
        e.Handled = true;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var files = ((string[])e.Data.GetData(DataFormats.FileDrop))
            .Where(File.Exists)
            .ToArray();

        if (files.Length == 0)
        {
            return;
        }

        switch (kind)
        {
            case DropKind.Modpack:
                Navigate(1);
                await _downloadPage.InstallPackFromPathAsync(files[0]);
                break;

            case DropKind.ModJar:
            case DropKind.ResourcePack:
            case DropKind.ShaderPack:
                await ImportDroppedContentAsync(kind, files);
                break;
        }
    }

    private async Task ImportDroppedContentAsync(DropKind kind, string[] files)
    {
        var instanceId = _homePage.SelectedInstanceId;

        if (string.IsNullOrEmpty(instanceId))
        {
            ShowDialog(
                "还没有实例",
                "拖进来的文件要装到某个实例里。先去「下载」页面装一个版本，或者在首页选一个已有实例。");
            return;
        }

        var scanner = new ModScanner(_services.Paths);
        var imported = 0;
        var failures = new List<string>();

        await EnqueueAsync($"导入 {files.Length} 个文件", _ =>
        {
            foreach (var file in files)
            {
                try
                {
                    if (kind == DropKind.ModJar)
                    {
                        scanner.Import(instanceId, file);
                    }
                    else
                    {
                        CopyIntoInstance(instanceId, kind, file);
                    }

                    imported++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(file)}：{ex.Message}");
                }
            }

            return Task.CompletedTask;
        });

        var what = kind switch
        {
            DropKind.ModJar => "Mod",
            DropKind.ResourcePack => "资源包",
            DropKind.ShaderPack => "光影包",
            _ => "文件",
        };

        var message = imported > 0
            ? $"已导入 {imported} 个{what}。"
            : $"没有导入任何{what}。";

        if (failures.Count > 0)
        {
            message += Environment.NewLine + Environment.NewLine + "失败：" + Environment.NewLine +
                       string.Join(Environment.NewLine, failures.Take(8));
        }

        ShowDialog(imported > 0 ? "导入完成" : "导入失败", message, allowCopy: failures.Count > 0);
    }

    private void CopyIntoInstance(string instanceId, DropKind kind, string source)
    {
        var subdirectory = kind switch
        {
            DropKind.ResourcePack => "resourcepacks",
            DropKind.ShaderPack => "shaderpacks",
            _ => "mods",
        };

        var directory = Path.Combine(_services.Paths.InstanceDir(instanceId), subdirectory);
        Directory.CreateDirectory(directory);

        File.Copy(source, Path.Combine(directory, Path.GetFileName(source)), overwrite: true);
    }

}
