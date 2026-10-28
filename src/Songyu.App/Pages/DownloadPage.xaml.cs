using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Songyu.Core.Minecraft;
using Songyu.Services;

namespace Songyu.App.Pages;

public sealed class DownloadEntry
{
    public required string Id { get; init; }

    public required string Note { get; init; }
}

/// <summary>Mod 搜索结果的一行。中文名是后台异步补的，所以需要属性通知。</summary>
public sealed class ModEntry : System.ComponentModel.INotifyPropertyChanged
{
    public required string ProjectId { get; init; }

    public required string Slug { get; init; }

    public required string IconUrl { get; init; }

    public required string EnglishTitle { get; init; }

    public required string Subtitle { get; init; }

    public required string MetaText { get; init; }

    private string _displayTitle = string.Empty;

    public string DisplayTitle
    {
        get => _displayTitle;
        set
        {
            if (_displayTitle == value)
            {
                return;
            }

            _displayTitle = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DisplayTitle)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>详情页里的一个可安装版本。</summary>
public sealed class ModVersionEntry
{
    public required string VersionId { get; init; }

    public required string VersionNumber { get; init; }

    public required string MetaText { get; init; }

    public required string GroupName { get; init; }

    public required string ActionText { get; init; }
}

/// <summary>详情页里的一条前置依赖。</summary>
public sealed class ModDependencyEntry
{
    public required string Name { get; init; }

    public required string TypeText { get; init; }

    public required string Url { get; init; }
}

/// <summary>已导入的整合包。</summary>
public sealed class PackEntry
{
    public required string Name { get; init; }

    public required string Meta { get; init; }

    public required string VersionId { get; init; }
}

public partial class DownloadPage : UserControl
{
    private static readonly Brush ActiveTab = new SolidColorBrush(Color.FromArgb(0x1A, 0xF2, 0xC1, 0x4E));
    private static readonly Brush SelectedBg = new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xDF));

    private readonly AppServices _services;
    private readonly ModrinthClient _modrinth;
    private readonly ModInstaller _modInstaller;
    private readonly McmodLocalizer _mcmod;
    private readonly Dictionary<LoaderKind, Button> _cards = new();
    private readonly Dictionary<string, Button> _modeTabs = new();
    private readonly Dictionary<string, string> _projectNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _projectSlugCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModProject?> _projectCache = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<MinecraftVersion> _versions = [];
    private IReadOnlyList<ModVersion> _currentModVersions = [];
    private bool _loaded;
    private string _mode = "game";
    private string? _selectedVersion;
    private LoaderKind _loader = LoaderKind.Vanilla;

    public DownloadPage(AppServices services)
    {
        InitializeComponent();

        _services = services;
        _modrinth = services.CreateModrinth();
        _modInstaller = services.CreateModInstaller();
        _mcmod = services.CreateMcmodLocalizer();

        _cards[LoaderKind.Vanilla] = VanillaCard;
        _cards[LoaderKind.Fabric] = FabricCard;
        _cards[LoaderKind.Quilt] = QuiltCard;
        _cards[LoaderKind.Forge] = ForgeCard;
        _cards[LoaderKind.NeoForge] = NeoForgeCard;

        _modeTabs["game"] = GameTab;
        _modeTabs["mod"] = ModTab;
        _modeTabs["pack"] = PackTab;

        UpdateModeTabs();
        Loaded += OnLoadedAsync;
    }

    // ── 顶部主分类 ──

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        _mode = tag;
        UpdateModeTabs();

        GameRoot.Visibility = _mode == "game" ? Visibility.Visible : Visibility.Collapsed;
        ModRoot.Visibility = _mode == "mod" ? Visibility.Visible : Visibility.Collapsed;
        PackRoot.Visibility = _mode == "pack" ? Visibility.Visible : Visibility.Collapsed;

        if (_mode == "mod")
        {
            InitModView();
        }
        else if (_mode == "pack")
        {
            RefreshPackList();
        }
    }

    private void UpdateModeTabs()
    {
        foreach (var (key, button) in _modeTabs)
        {
            var active = key == _mode;
            button.Background = active ? ActiveTab : Brushes.Transparent;
            button.Foreground = active ? (Brush)FindResource("Ink") : (Brush)FindResource("InkSoft");
        }
    }

    // ── 游戏版本 ──

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            Render();
            return;
        }

        HintText.Text = "正在获取版本列表…";

        try
        {
            var client = new VersionManifestClient(_services.Http, _services.Config.MirrorBaseUrl);
            var manifest = await client.GetManifestAsync();
            _versions = manifest.Versions;
            _loaded = true;
            Render();
        }
        catch (Exception ex)
        {
            HintText.Text = "获取版本列表失败：" + ex.Message;
        }
    }

    private void Render()
    {
        HintText.Text = "选一个游戏版本，再挑要叠加的加载器。";

        var installed = _services.LocalVersions.List();

        var entries = _versions
            .Where(v => v.IsRelease || v.IsSnapshot)
            .Select(v =>
            {
                var hasVanilla = installed.Any(i => !i.IsLoader && i.Id == v.Id);
                var kindText = v.IsSnapshot ? "快照" : "正式版";
                var note = hasVanilla ? $"已安装 · {kindText}" : kindText;
                return new DownloadEntry { Id = v.Id, Note = note };
            })
            .ToList();

        VersionList.ItemsSource = entries;
    }

    private void OnVersionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not DownloadEntry entry)
        {
            return;
        }

        _selectedVersion = entry.Id;
        DetailTitle.Text = entry.Id;

        GameListView.Visibility = Visibility.Collapsed;
        GameDetailView.Visibility = Visibility.Visible;

        UpdateCards();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        VersionList.SelectedItem = null;
        GameDetailView.Visibility = Visibility.Collapsed;
        GameListView.Visibility = Visibility.Visible;
    }

    private void OnLoaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        if (tag == "OptiFine")
        {
            if (Window.GetWindow(this) is MainWindow owner)
            {
                owner.ShowDialog(
                    "OptiFine 需要手动导入",
                    "受 Mojang 与 OptiFine 的授权限制，启动器不能代为分发 OptiFine。\n\n" +
                    "请到 optifine.net 下载对应游戏版本的安装器，运行后点「Extract」，" +
                    "把生成的 jar 放进实例目录即可。");
            }

            return;
        }

        if (!Enum.TryParse<LoaderKind>(tag, out var kind))
        {
            return;
        }

        _loader = kind;
        UpdateCards();
    }

    private void UpdateCards()
    {
        foreach (var (kind, card) in _cards)
        {
            var selected = kind == _loader;
            card.BorderBrush = selected ? (Brush)FindResource("EggYolk") : (Brush)FindResource("Line");
            card.Background = selected ? SelectedBg : (Brush)FindResource("CardBg");
        }

        DetailHint.Text = _loader switch
        {
            LoaderKind.Vanilla => $"将安装原版 {_selectedVersion}",
            LoaderKind.Fabric => $"将安装原版 {_selectedVersion} + 最新 stable Fabric",
            LoaderKind.Quilt => $"将安装原版 {_selectedVersion} + 最新 stable Quilt",
            LoaderKind.Forge => $"将安装原版 {_selectedVersion} + Forge（需运行官方安装器）",
            LoaderKind.NeoForge => $"将安装原版 {_selectedVersion} + NeoForge（需 1.20.2 及以上）",
            _ => $"{_loader} 暂未支持",
        };
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (_selectedVersion is null || Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        InstallButton.IsEnabled = false;

        try
        {
            await main.InstallVersionAsync(_selectedVersion, _loader);
            Render();
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }

    // ── Mod：搜索结果 ──

    private async void InitModView()
    {
        // 版本下拉：Modrinth 支持的全部游戏版本（正式版在前），不只已装的那些
        if (VersionCombo.ItemsSource is null)
        {
            try
            {
                var versions = await _modrinth.GetGameVersionsAsync();
                VersionCombo.ItemsSource = versions;

                // 默认选中第一个已安装的版本；一个都没装就选最新的正式版
                var installedBases = _services.LocalVersions.List()
                    .Select(v => ResolveContext(v).McVersion)
                    .Where(mc => !string.IsNullOrEmpty(mc))
                    .Distinct()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // 默认选最近玩过的那个实例的版本；没有实例就选最新的正式版
                var recent = _services.Instances.List()
                    .OrderByDescending(i => i.LastPlayed ?? i.Created)
                    .Select(i => GameContextResolver.Resolve(_services.Paths, i.VersionId).McVersion)
                    .FirstOrDefault(mc => !string.IsNullOrEmpty(mc) && versions.Contains(mc));

                var preferred = recent
                                ?? versions.FirstOrDefault(v => installedBases.Contains(v))
                                ?? versions.FirstOrDefault();

                if (preferred is not null)
                {
                    VersionCombo.SelectedItem = preferred;
                }
            }
            catch (Exception ex)
            {
                ModHint.Text = "获取版本列表失败：" + ex.Message;
                return;
            }
        }

        // 加载器下拉：不限 + 四种常见加载器
        if (LoaderCombo.ItemsSource is null)
        {
            LoaderCombo.ItemsSource = new[] { "不限", "fabric", "forge", "neoforge", "quilt" };
            LoaderCombo.SelectedIndex = 0;
        }

        await LoadRecommendedAsync();
    }

    /// <summary>按下载量列出该版本 / 加载器可用的热门 Mod（Modrinth 的 index=downloads）。</summary>
    private async Task LoadRecommendedAsync()
    {
        var (mcVersion, loader, _) = CurrentContext();
        if (mcVersion is null)
        {
            return;
        }

        ModHint.Text = "加载热门 Mod…";

        try
        {
            var hits = await _modrinth.SearchAsync(
                string.Empty, mcVersion, loader, limit: 24, index: "downloads");

            ModList.ItemsSource = BuildEntries(hits);
            ModHint.Text = $"{mcVersion} · {loader ?? "不限加载器"} 的热门 Mod（按下载量）";
        }
        catch (Exception ex)
        {
            ModHint.Text = "加载推荐失败：" + ex.Message;
        }
    }

    /// <summary>换版本或加载器就重新拉推荐——不同组合的热门 Mod 完全不同。</summary>
    private async void OnContextChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mode != "mod" || ModSearchView.Visibility != Visibility.Visible)
        {
            return;
        }

        await LoadRecommendedAsync();
    }

    private List<ModEntry> BuildEntries(IReadOnlyList<ModSearchHit> hits)
    {
        var entries = hits.Select(h => new ModEntry
        {
            ProjectId = h.ProjectId,
            Slug = h.Slug,
            IconUrl = h.IconUrl,
            EnglishTitle = h.Title,
            DisplayTitle = h.Title,   // 先显示原名，mcmod 查到中文后覆盖
            Subtitle = h.Description,
            MetaText = $"{h.Downloads:N0} 次下载"
                       + (string.IsNullOrEmpty(h.Author) ? string.Empty : $" · {h.Author}"),
        }).ToList();

        // 后台查 mcmod；查不到就保持英文原名，不做任何猜测式翻译
        _ = FillChineseNamesAsync(entries);

        return entries;
    }

    private async Task FillChineseNamesAsync(List<ModEntry> entries)
    {
        try
        {
            await Task.WhenAll(entries.Select(async entry =>
            {
                try
                {
                    var name = await _mcmod.LookupAsync(entry.EnglishTitle, entry.Slug);

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        entry.DisplayTitle = name;
                    }
                }
                catch
                {
                    // 单个失败不影响其它行
                }
            }));
        }
        catch
        {
            // 整批失败也只是保持英文名，不该让列表崩
        }
    }

    private static (string? McVersion, string? Loader) ResolveContext(InstalledVersion version)
    {
        var loader = version.Id.StartsWith("fabric-loader", StringComparison.OrdinalIgnoreCase)
            ? "fabric"
            : version.Id.StartsWith("quilt-loader", StringComparison.OrdinalIgnoreCase)
                ? "quilt"
                : version.Id.Contains("neoforge", StringComparison.OrdinalIgnoreCase)
                    ? "neoforge"
                    : version.Id.Contains("forge", StringComparison.OrdinalIgnoreCase)
                        ? "forge"
                        : null;

        return (version.InheritsFrom ?? version.Id, loader);
    }

    /// <summary>
    /// 当前的筛选上下文：版本来自下拉框（可以是任意 MC 版本，不必先装实例），
    /// 加载器选「不限」时为 null。第三个返回值是"装到哪个实例"——
    /// 只有当已装版本里正好有同版本 + 同加载器的，才拿得到。
    /// </summary>
    private (string? McVersion, string? Loader, InstalledVersion? Instance) CurrentContext()
    {
        var mcVersion = VersionCombo.SelectedItem as string;

        if (string.IsNullOrEmpty(mcVersion))
        {
            return (null, null, null);
        }

        var loader = LoaderCombo.SelectedItem as string;
        if (string.Equals(loader, "不限", StringComparison.Ordinal))
        {
            loader = null;
        }

        // 找同版本 + 同加载器的已装版本当安装目标；加载器不限时只按版本匹配原版
        var match = _services.LocalVersions.List()
            .Select(v => (Version: v, Context: ResolveContext(v)))
            .FirstOrDefault(pair =>
                string.Equals(pair.Context.McVersion, mcVersion, StringComparison.OrdinalIgnoreCase) &&
                (loader is null
                    ? !pair.Version.IsLoader
                    : string.Equals(pair.Context.Loader, loader, StringComparison.OrdinalIgnoreCase)));

        return (mcVersion, loader, match.Version);
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSearchClick(sender, e);
        }
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        var (mcVersion, loader, _) = CurrentContext();
        if (mcVersion is null)
        {
            ModHint.Text = "请先选择要筛选的版本";
            return;
        }

        ModHint.Text = "搜索中…";

        try
        {
            var hits = await _modrinth.SearchAsync(SearchBox.Text.Trim(), mcVersion, loader);

            ModList.ItemsSource = BuildEntries(hits);

            ModHint.Text = hits.Count == 0
                ? $"没有找到适配 {mcVersion} / {loader} 的 Mod"
                : $"{hits.Count} 个结果（{mcVersion} · {loader}）";
        }
        catch (Exception ex)
        {
            ModHint.Text = "搜索失败：" + ex.Message;
        }
    }

    private async void OnModSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ModEntry entry)
        {
            return;
        }

        await LoadModDetailAsync(entry);
    }

    private async Task LoadModDetailAsync(ModEntry entry)
    {
        var (mcVersion, loader, instance) = CurrentContext();
        if (mcVersion is null || instance is null)
        {
            return;
        }

        ModInstallHint.Text = "加载详情…";

        try
        {
            var project = await _modrinth.GetProjectAsync(entry.ProjectId);

            // 和列表走同一条查询链（同一个缓存键），保证两处名字一致。
            // 查不到就显示英文原名——不编中文名。
            var chinese = await _mcmod.LookupAsync(project.Title, project.Slug);

            ModDetailTitle.Text = string.IsNullOrEmpty(chinese) ? project.Title : chinese;
            ModDetailSubtitle.Text = string.IsNullOrEmpty(chinese) ? project.Slug : project.Title;
            ModDetailMeta.Text = $"{project.Downloads:N0} 次下载"
                                 + (project.Categories.Count > 0
                                     ? " · " + string.Join(" · ", project.Categories.Take(3))
                                     : string.Empty);
            ModDetailDesc.Text = project.Description;
            ModIcon.Source = TryLoadImage(project.IconUrl);

            // 拉全部版本（不按游戏版本过滤），本地再分「当前适配」和「其他」。
            // 用户要能看到所有版本、能选任意一个，而不是只给适配当前实例的那几个。
            var all = await _modrinth.GetVersionsAsync(project.Id, null, null);

            var ordered = all
                .OrderByDescending(v => v, Comparer<ModVersion>.Create((a, b) =>
                    CompareVersionNumbers(a.VersionNumber, b.VersionNumber)))
                .ToList();

            var compatible = ordered
                .Where(v => v.GameVersions.Contains(mcVersion, StringComparer.OrdinalIgnoreCase)
                            && (loader is null || v.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase)))
                .ToList();

            var compatibleIds = compatible.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
            var others = ordered.Where(v => !compatibleIds.Contains(v.Id)).ToList();

            _currentModVersions = ordered;

            var entries = new List<ModVersionEntry>();
            entries.AddRange(compatible.Select(v =>
                BuildVersionEntry(v, $"当前版本（适配 {mcVersion} · {loader}）")));
            entries.AddRange(others.Select(v => BuildVersionEntry(v, "其他版本")));

            var view = new ListCollectionView(entries);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModVersionEntry.GroupName)));
            ModVersionList.ItemsSource = view;

            await RenderDependenciesAsync(compatible.FirstOrDefault() ?? ordered.FirstOrDefault(), instance.Id);

            ModInstallHint.Text = $"将安装到 {instance.Id}（{mcVersion} · {loader}）";

            ModSearchView.Visibility = Visibility.Collapsed;
            ModDetailView.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ModInstallHint.Text = "加载失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 只列「必需」且「还没装」的前置。
    /// 可选依赖不提示；已经在 mods 里的（比如 Fabric API）也不再提示，
    /// 否则每个 Mod 都顶着两条"请安装 Fabric API"，等于噪音。
    /// </summary>
    private async Task RenderDependenciesAsync(ModVersion? version, string instanceId)
    {
        var required = version?.Dependencies
            .Where(d => d.DependencyType == "required" && !string.IsNullOrEmpty(d.ProjectId))
            .ToList() ?? [];

        if (required.Count == 0)
        {
            DependencySection.Visibility = Visibility.Collapsed;
            return;
        }

        var modsDirectory = Path.Combine(_services.Paths.InstancesDir, instanceId, "mods");
        var installedFiles = Directory.Exists(modsDirectory)
            ? Directory.GetFiles(modsDirectory, "*.jar")
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => n!.ToLowerInvariant())
                .ToList()
            : [];

        var entries = new List<ModDependencyEntry>();

        foreach (var dependency in required)
        {
            var slug = await ResolveProjectSlugAsync(dependency.ProjectId!);

            if (slug is not null && installedFiles.Any(f => f.Contains(slug, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            entries.Add(new ModDependencyEntry
            {
                Name = await ResolveProjectNameAsync(dependency.ProjectId),
                TypeText = "必需 · 安装时会自动一起下载",
                Url = $"https://modrinth.com/mod/{dependency.ProjectId}",
            });
        }

        if (entries.Count == 0)
        {
            DependencySection.Visibility = Visibility.Collapsed;
            return;
        }

        DependencySection.Visibility = Visibility.Visible;
        ModDependencyList.ItemsSource = entries;
    }

    private static ModVersionEntry BuildVersionEntry(ModVersion version, string groupName)
    {
        var meta = string.Join(", ", version.GameVersions.Take(3))
                   + " · " + string.Join(", ", version.Loaders);

        if (version.Dependencies.Any(d => d.DependencyType == "required"))
        {
            meta += " · 有前置";
        }

        return new ModVersionEntry
        {
            VersionId = version.Id,
            VersionNumber = version.VersionNumber,
            MetaText = meta,
            GroupName = groupName,
            ActionText = "安装",
        };
    }

    /// <summary>版本号比较：拆成数字段逐个比，非数字段按 0 处理。</summary>
    private static int CompareVersionNumbers(string a, string b)
    {
        var left = TokenizeVersion(a);
        var right = TokenizeVersion(b);

        for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            var x = i < left.Count ? left[i] : 0;
            var y = i < right.Count ? right[i] : 0;

            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    private static List<int> TokenizeVersion(string version)
    {
        var parts = version.Split(['.', '-', '+', '_', ' '], StringSplitOptions.RemoveEmptyEntries);
        return parts.Select(p => int.TryParse(p, out var n) ? n : 0).ToList();
    }

    private async Task<string?> ResolveProjectSlugAsync(string projectId)
    {
        if (_projectSlugCache.TryGetValue(projectId, out var cached))
        {
            return cached;
        }

        var project = await GetProjectCachedAsync(projectId);
        _projectSlugCache[projectId] = project?.Slug;
        return project?.Slug;
    }

    private async Task<string> ResolveProjectNameAsync(string? projectId)
    {
        if (string.IsNullOrEmpty(projectId))
        {
            return "未知依赖";
        }

        if (_projectNameCache.TryGetValue(projectId, out var cached))
        {
            return cached;
        }

        var project = await GetProjectCachedAsync(projectId);
        if (project is null)
        {
            return projectId;
        }

        var chinese = await _mcmod.LookupAsync(project.Title, project.Slug);
        var name = string.IsNullOrEmpty(chinese)
            ? project.Title
            : $"{chinese}（{project.Title}）";

        _projectNameCache[projectId] = name;
        return name;
    }

    /// <summary>详情按 projectId 缓存，避免同一个依赖被反复请求。</summary>
    private async Task<ModProject?> GetProjectCachedAsync(string projectId)
    {
        if (_projectCache.TryGetValue(projectId, out var cached))
        {
            return cached;
        }

        try
        {
            var project = await _modrinth.GetProjectAsync(projectId);
            _projectCache[projectId] = project;
            return project;
        }
        catch
        {
            _projectCache[projectId] = null;
            return null;
        }
    }

    private async void OnInstallModVersionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string versionId })
        {
            return;
        }

        var (mcVersion, loader, instance) = CurrentContext();

        if (mcVersion is null || Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        if (instance is null)
        {
            main.ShowDialog(
                "这个版本还没装",
                $"「{mcVersion} · {loader ?? "原版"}」目前没有对应的已安装版本，Mod 没有地方可装。\n\n" +
                "可以先去「游戏版本」把这个组合装上，再回来安装 Mod。");
            return;
        }

        var version = _currentModVersions.FirstOrDefault(v => v.Id == versionId);
        if (version is null)
        {
            return;
        }

        // 先把必需前置列出来，让用户知道要连带装什么
        var required = version.Dependencies
            .Where(d => d.DependencyType == "required" && !string.IsNullOrEmpty(d.ProjectId))
            .ToList();

        if (required.Count > 0)
        {
            var names = new List<string>();
            foreach (var dependency in required)
            {
                names.Add("· " + await ResolveProjectNameAsync(dependency.ProjectId));
            }

            var confirmed = await main.ShowConfirmAsync(
                "前置依赖",
                $"这个 Mod 需要以下前置，将一并安装：\n\n{string.Join("\n", names)}",
                "一起安装",
                "取消");

            if (!confirmed)
            {
                return;
            }
        }

        var button = (Button)sender;
        button.IsEnabled = false;

        try
        {
            await main.EnqueueAsync($"安装 {version.VersionNumber}", async token =>
            {
                var progress = new Progress<string>(s => ModInstallHint.Text = s);
                var result = await _modInstaller.InstallAsync(
                    instance.Id, version, progress, main.CreateDownloadProgress(), token);

                ModInstallHint.Text = result.InstalledFiles.Count == 0
                    ? "已存在，无需重复安装"
                    : $"已安装 {result.InstalledFiles.Count} 个文件到 {instance.Id}/mods";

                main.ShowDialog(
                    "安装完成",
                    result.InstalledFiles.Count == 0
                        ? "mods 目录里已经有同名文件，跳过。"
                        : $"安装到：{instance.Id}/mods\n\n" + string.Join("\n", result.InstalledFiles));
            });
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void OnModBackClick(object sender, RoutedEventArgs e)
    {
        ModList.SelectedItem = null;
        ModDetailView.Visibility = Visibility.Collapsed;
        ModSearchView.Visibility = Visibility.Visible;
    }

    private void OnOpenUrlClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url } || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器就算了
        }
    }

    // ── 整合包 ──

    private async void OnImportPackClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择整合包",
            Filter = "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await InstallPackFromPathAsync(dialog.FileName);
    }

    /// <summary>
    /// 从指定路径安装整合包。文件选择与窗口拖放共走这一条路径。
    /// </summary>
    public async Task InstallPackFromPathAsync(string archivePath)
    {
        if (Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        var java = _services.BestJava;
        if (java is null)
        {
            main.ShowDialog("需要 Java", "安装整合包需要先检测到 Java。");
            return;
        }

        PackHint.Text = "正在解析整合包…";

        try
        {
            var installer = _services.CreateModpackInstaller();
            var info = await installer.InspectAsync(archivePath);

            await main.EnqueueAsync($"导入 {info.Name}", async token =>
            {
                PackHint.Text = $"正在安装 {info.Name}（{info.FormatText}）…";

                var log = new Progress<string>(s => PackHint.Text = s);
                var progress = new Progress<InstallProgress>(p => PackHint.Text = p.Describe());

                var versionId = await installer.InstallAsync(
                    archivePath, java.JavaExePath, log, progress, main.CreateDownloadProgress(),
                    cancellationToken: token);

                PackHint.Text = $"安装完成：{versionId}";
                RefreshPackList();
                main.RefreshAfterChange();
                main.ShowDialog("整合包安装完成", $"{info.Name}\n版本：{versionId}\n\n可以去启动页启动。");
            });
        }
        catch (Exception ex)
        {
            PackHint.Text = "解析失败：" + ex.Message;
            main.ShowDialog("整合包解析失败", ex.Message, allowCopy: true);
        }
    }

    private void RefreshPackList()
    {
        var entries = new List<PackEntry>();

        if (Directory.Exists(_services.Paths.InstancesDir))
        {
            foreach (var directory in Directory.GetDirectories(_services.Paths.InstancesDir))
            {
                var metaPath = Path.Combine(directory, "modpack.json");
                if (!File.Exists(metaPath))
                {
                    continue;
                }

                try
                {
                    var record = System.Text.Json.JsonSerializer.Deserialize<ModpackRecord>(File.ReadAllText(metaPath));
                    if (record is null || string.IsNullOrEmpty(record.VersionId))
                    {
                        continue;
                    }

                    var loader = string.IsNullOrEmpty(record.LoaderKind)
                        ? "原版"
                        : $"{record.LoaderKind} {record.LoaderVersion}";

                    entries.Add(new PackEntry
                    {
                        Name = record.Name,
                        Meta = $"{record.FormatText} · {record.McVersion} · {loader} · {record.FileCount} 个文件",
                        VersionId = record.VersionId,
                    });
                }
                catch
                {
                    // 坏元数据跳过
                }
            }
        }

        PackList.ItemsSource = entries;
        PackHint.Text = entries.Count == 0 ? "还没有导入整合包。" : $"已导入 {entries.Count} 个整合包";
    }

    private async void OnLaunchPackClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string versionId } && Window.GetWindow(this) is MainWindow main)
        {
            await main.RunVersionAsync(versionId);
        }
    }

    /// <summary>
    /// 加载远程图标。
    /// 这里刻意不设 CacheOption.OnLoad——那会在 UI 线程同步下载，
    /// 一页 20 个 Mod 会把界面卡住。默认的 OnDemand 是异步的。
    /// </summary>
    private static ImageSource? TryLoadImage(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            return new BitmapImage(new Uri(url));
        }
        catch
        {
            return null;
        }
    }
}
