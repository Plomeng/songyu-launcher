using System.IO;
using System.Windows;
using System.Windows.Controls;
using Songyu.Services;

namespace Songyu.App.Pages;

/// <summary>
/// Mod 管理页。
/// <para>
/// 数据来自 <see cref="ContentIndex"/>（本地索引），而不是每次现扫：
/// 索引里有文件的 sha1、Modrinth 来源（项目/版本）、以及缓存的 jar 元数据，
/// 所以第二次进这个页面几乎是秒开。
/// </para>
/// </summary>
public partial class ModsPage : UserControl
{
    private readonly AppServices _services;
    private readonly ModScanner _scanner;

    private readonly List<ModRow> _allRows = [];

    private string? _instanceId;
    private bool _suppressFilter;
    private bool _busy;

    /// <summary>列表筛选标签。</summary>
    private enum ModFilter
    {
        All,
        HasUpdate,
        LocalSource,
        Disabled,
    }

    private ModFilter _filter = ModFilter.All;

    public ModsPage(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _scanner = new ModScanner(services.Paths);
    }

    /// <summary>界面用的一行。</summary>
    public sealed record ModRow(
        IndexedFile Source,
        string FullPath,
        string FileName,
        string Name,
        string Version,
        string Loader,
        string SourceLabel,
        string SourceDetail,
        bool HasSource,
        bool Enabled,
        bool Missing,
        long Bytes,
        bool HasUpdate,
        string UpdateText,
        string UpdateVersionId)
    {
        public string ToggleLabel => Enabled ? "禁用" : "启用";

        public string SizeText => Bytes switch
        {
            >= 1024 * 1024 => $"{Bytes / 1024.0 / 1024:F1} MB",
            >= 1024 => $"{Bytes / 1024} KB",
            _ => $"{Bytes} B",
        };

        /// <summary>卡片第二行：加载器 · 来源 · 大小 · 状态。</summary>
        public string DetailLine
        {
            get
            {
                var loader = string.IsNullOrWhiteSpace(Loader) ? "未知加载器" : Loader;
                var state = Missing ? "文件缺失" : (Enabled ? "已启用" : "已禁用");
                return $"{loader}   ·   {SourceLabel}   ·   {SizeText}   ·   {state}";
            }
        }
    }

    /// <summary>
    /// 页面切入时调用。实例由调用方决定（首页选中的那个），
    /// 传空就沿用上次的，再没有就取最近玩过的第一个。
    /// </summary>
    public void Load(string? instanceId = null)
    {
        var target = ResolveInstance(instanceId);

        if (target is null)
        {
            ShowNoInstance();
            return;
        }

        _instanceId = target.Id;

        SubtitleText.Text = string.Equals(target.Name, target.VersionId, StringComparison.Ordinal)
            ? target.VersionId
            : $"{target.Name}   ·   {target.VersionId}";
        PathText.Text = _services.Paths.InstanceModsDir(target.Id);

        _suppressFilter = true;
        SearchBox.Text = string.Empty;
        SearchHint.Visibility = Visibility.Visible;
        _suppressFilter = false;

        UpdateFilterButtons();

        _allRows.Clear();
        _ = RefreshAsync();
    }

    private InstanceInfo? ResolveInstance(string? preferId)
    {
        var instances = _services.Instances.List();

        return instances.FirstOrDefault(i => i.Id == preferId)
               ?? instances.FirstOrDefault(i => i.Id == _instanceId)
               ?? instances.FirstOrDefault();
    }

    private void ShowNoInstance()
    {
        _instanceId = null;
        _allRows.Clear();

        SubtitleText.Text = "还没有实例";
        PathText.Text = "先去「下载」页面装一个版本";
        CountText.Text = string.Empty;
        ModList.ItemsSource = null;

        EmptyTitle.Text = "还没有实例";
        EmptySubtitle.Text = "去「下载」页面装一个版本，装完再回来看 Mod";
        EmptyHint.Visibility = Visibility.Visible;
        LoadingText.Visibility = Visibility.Collapsed;
    }

    // ── 加载 ──

    private async Task RefreshAsync()
    {
        if (_instanceId is not { Length: > 0 } instanceId || _busy)
        {
            return;
        }

        _busy = true;
        LoadingText.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Collapsed;

        try
        {
            // 1. 同步索引：扫盘 + 算 sha1（没变过的文件用缓存，不会重算）
            LoadingText.Text = "正在扫描文件…";
            var files = await Task.Run(() => _services.Content.Sync(instanceId));

            // 2. 补读 jar 元数据（只对没读过的文件，读一次就缓存进索引）
            var pending = _services.Content.GetFilesNeedingJarMetadata(instanceId);
            if (pending.Count > 0)
            {
                LoadingText.Text = $"正在读取 {pending.Count} 个 Mod 的元数据…";

                var metadata = await Task.Run(() =>
                {
                    var map = new Dictionary<string, JarMetadata>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (relative, fullPath) in pending)
                    {
                        try
                        {
                            if (File.Exists(fullPath))
                            {
                                map[relative] = ModScanner.ReadJarMetadata(fullPath);
                            }
                        }
                        catch
                        {
                            // 坏 jar 跳过，不影响其它
                        }
                    }

                    return (IReadOnlyDictionary<string, JarMetadata>)map;
                });

                _services.Content.ApplyJarMetadata(instanceId, metadata);
            }

            // 3. 首次自动识别来源（一次批量 API 调用，之后全部命中缓存）
            var unresolved = _services.Content.GetUnresolvedSha1s(instanceId);
            if (unresolved.Count > 0)
            {
                LoadingText.Text = $"正在识别 {unresolved.Count} 个文件的来源…";
                await IdentifySourcesCoreAsync(instanceId, unresolved);
            }

            // 4. 读最终索引并显示
            files = _services.Content.GetFiles(instanceId);
            BuildRows(files);
        }
        catch (Exception ex)
        {
            CountText.Text = "读取失败：" + ex.Message;
            EmptyTitle.Text = "读取失败";
            EmptySubtitle.Text = ex.Message;
            EmptyHint.Visibility = Visibility.Visible;
        }
        finally
        {
            LoadingText.Visibility = Visibility.Collapsed;
            _busy = false;
        }
    }

    private void BuildRows(IReadOnlyList<IndexedFile> files)
    {
        _allRows.Clear();

        foreach (var file in files.Where(f => f.Kind == ContentKind.Mod))
        {
            var fullPath = Path.Combine(
                _services.Instances.GameDirectory(file.InstanceId),
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            _allRows.Add(new ModRow(
                file,
                fullPath,
                file.FileName,
                file.Name,
                file.VersionText,
                file.ModLoader ?? string.Empty,
                file.HasSource ? "Modrinth" : "本地",
                file.SourceText,
                file.HasSource,
                file.Enabled,
                file.Missing,
                file.Size,
                file.HasUpdate && !file.Missing,
                file.Missing ? string.Empty : file.UpdateText,
                file.Missing ? string.Empty : file.UpdateVersionId ?? string.Empty));
        }

        ApplyFilter();
    }

    private async Task IdentifySourcesCoreAsync(string instanceId, IReadOnlyList<string> sha1s)
    {
        var modrinth = _services.CreateModrinth();

        var sources = await modrinth.GetFilesByHashesAsync(sha1s, "mod");
        _services.Content.ApplySources(sources);
    }

    // ── 搜索与筛选 ──

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressFilter)
        {
            return;
        }

        ApplyFilter();
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } ||
            !Enum.TryParse<ModFilter>(tag, out var filter))
        {
            return;
        }

        _filter = filter;
        UpdateFilterButtons();
        ApplyFilter();
    }

    private void UpdateFilterButtons()
    {
        Highlight(FilterAll, _filter == ModFilter.All);
        Highlight(FilterUpdate, _filter == ModFilter.HasUpdate);
        Highlight(FilterLocal, _filter == ModFilter.LocalSource);
        Highlight(FilterDisabled, _filter == ModFilter.Disabled);
    }

    private static void Highlight(Button button, bool active)
    {
        button.Background = active
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xC1, 0x4E))
            : System.Windows.Media.Brushes.Transparent;

        button.Foreground = active
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2E, 0x14))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x81, 0x75));

        button.FontWeight = active
            ? System.Windows.FontWeights.SemiBold
            : System.Windows.FontWeights.Normal;
    }

    /// <summary>
    /// 在已加载的结果里按筛选标签 + 搜索词过滤。不碰磁盘也不发请求。
    /// </summary>
    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        SearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        IEnumerable<ModRow> source = _allRows;

        source = _filter switch
        {
            ModFilter.HasUpdate => source.Where(m => m.HasUpdate),
            ModFilter.LocalSource => source.Where(m => !m.HasSource),
            ModFilter.Disabled => source.Where(m => !m.Enabled),
            _ => source,
        };

        if (query.Length > 0)
        {
            source = source.Where(m =>
                m.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                m.FileName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                m.SourceDetail.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        var list = source.ToList();
        ModList.ItemsSource = list;

        var filterText = _filter switch
        {
            ModFilter.HasUpdate => "有更新 ",
            ModFilter.LocalSource => "本地来源 ",
            ModFilter.Disabled => "已禁用 ",
            _ => string.Empty,
        };

        if (query.Length > 0 || _filter != ModFilter.All)
        {
            CountText.Text = $"{filterText}筛选出 {list.Count} 个 / 共 {_allRows.Count} 个";
        }
        else
        {
            var enabled = _allRows.Count(m => m.Enabled && !m.Missing);
            var known = _allRows.Count(m => m.HasSource);
            var missing = _allRows.Count(m => m.Missing);
            var updates = _allRows.Count(m => m.HasUpdate);

            var parts = new List<string> { $"共 {_allRows.Count} 个", $"Modrinth 来源 {known}" };
            if (updates > 0) parts.Add($"有更新 {updates}");
            if (enabled != _allRows.Count) parts.Add($"启用 {enabled}");
            if (missing > 0) parts.Add($"缺失 {missing}");

            CountText.Text = string.Join("，", parts);
        }

        if (list.Count == 0)
        {
            var hasFilter = query.Length > 0 || _filter != ModFilter.All;

            EmptyTitle.Text = hasFilter ? "没有匹配的 Mod" : "这个实例还没有 Mod";
            EmptySubtitle.Text = hasFilter
                ? (_filter == ModFilter.HasUpdate
                    ? "没有 Mod 有新版本。点「检查更新」重新查一次？"
                    : "换个筛选条件或搜索词试试")
                : "可以去「下载」页面装，或者点右上角「导入 jar」";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    // ── 操作 ──

    private async void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModRow row })
        {
            return;
        }

        try
        {
            ModScanner.SetEnabled(row.FullPath, !row.Enabled);
        }
        catch (Exception ex)
        {
            ShowDialog("操作失败", ex.Message);
        }

        await RefreshAsync();
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModRow row } ||
            Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        var confirmed = await main.ShowConfirmAsync(
            "删除 Mod",
            $"要删除「{row.Name}」吗？\n\n" +
            $"文件：{row.FileName}\n\n" +
            "文件不会被真正删除，会挪到 mods\\.trash 里，需要时可以翻回来。",
            "删除",
            "取消");

        if (!confirmed)
        {
            return;
        }

        try
        {
            ModScanner.Delete(row.FullPath);
        }
        catch (Exception ex)
        {
            ShowDialog("删除失败", ex.Message);
        }

        await RefreshAsync();
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_instanceId is not { Length: > 0 } instanceId)
        {
            ShowDialog("还没有实例", "先去「下载」页面装一个版本再来。");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要导入的 Mod",
            Filter = "Mod 文件 (*.jar)|*.jar",
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        var imported = new List<string>();
        var failed = new List<string>();

        foreach (var file in dialog.FileNames)
        {
            try
            {
                var mod = _scanner.Import(instanceId, file);
                imported.Add(mod.Name);
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(file)}：{ex.Message}");
            }
        }

        await RefreshAsync();

        var message = imported.Count > 0
            ? $"已导入 {imported.Count} 个：\n" + string.Join("\n", imported.Take(10))
            : "没有导入任何文件。";

        if (failed.Count > 0)
        {
            message += "\n\n失败：\n" + string.Join("\n", failed.Take(10));
        }

        ShowDialog("导入完成", message, allowCopy: failed.Count > 0);
    }

    private void OnOpenDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (_instanceId is not { Length: > 0 } instanceId)
        {
            ShowDialog("还没有实例", "先去「下载」页面装一个版本再来。");
            return;
        }

        try
        {
            _scanner.OpenDirectory(instanceId);
        }
        catch (Exception ex)
        {
            ShowDialog("打不开目录", $"{ex.Message}\n\n路径：\n{_services.Paths.InstanceModsDir(instanceId)}",
                allowCopy: true);
        }
    }

    /// <summary>手动重新识别来源（比如刚装完新 mod，或之前网络失败）。</summary>
    private async void OnIdentifyClick(object sender, RoutedEventArgs e)
    {
        if (_instanceId is not { Length: > 0 } instanceId || _busy)
        {
            return;
        }

        try
        {
            var unresolved = _services.Content.GetUnresolvedSha1s(instanceId);

            if (unresolved.Count == 0)
            {
                ShowDialog("都识别过了", "这个实例里所有文件都已经查过 Modrinth 来源。");
                return;
            }

            LoadingText.Text = $"正在识别 {unresolved.Count} 个文件的来源…";
            LoadingText.Visibility = Visibility.Visible;

            await IdentifySourcesCoreAsync(instanceId, unresolved);
            BuildRows(_services.Content.GetFiles(instanceId));
        }
        catch (Exception ex)
        {
            ShowDialog("识别失败", ex.Message, allowCopy: true);
        }
        finally
        {
            LoadingText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>检查更新：一次批量 API 调用，结果落库后重画列表。</summary>
    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_instanceId is not { Length: > 0 } instanceId || _busy)
        {
            return;
        }

        var instance = _services.Instances.Get(instanceId);
        if (instance is null)
        {
            return;
        }

        _busy = true;
        LoadingText.Text = "正在检查更新…";
        LoadingText.Visibility = Visibility.Visible;

        try
        {
            var updater = _services.CreateModUpdater();
            var summary = await updater.CheckAsync(instanceId, instance.VersionId);

            BuildRows(_services.Content.GetFiles(instanceId));

            if (summary.Total == 0)
            {
                ShowDialog("没有 Mod", "这个实例里还没有可检查的 Mod。");
            }
            else if (summary.Updatable == 0)
            {
                ShowDialog(
                    "都是最新的",
                    $"检查了 {summary.Total} 个 Mod（{summary.Context.Describe()}），没有发现新版本。");
            }
            else
            {
                ShowDialog(
                    "发现更新",
                    $"检查了 {summary.Total} 个 Mod（{summary.Context.Describe()}），" +
                    $"{summary.Updatable} 个有新版本。\n\n点行上的「更新」逐个更新。");
            }
        }
        catch (Exception ex)
        {
            ShowDialog("检查更新失败", ex.Message, allowCopy: true);
        }
        finally
        {
            LoadingText.Visibility = Visibility.Collapsed;
            _busy = false;
        }
    }

    /// <summary>更新单个 Mod：下载新版 → 校验 → 旧文件进 .trash。</summary>
    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModRow row } ||
            _instanceId is not { Length: > 0 } instanceId ||
            string.IsNullOrEmpty(row.UpdateVersionId))
        {
            return;
        }

        _busy = true;
        LoadingText.Text = $"正在更新 {row.Name}…";
        LoadingText.Visibility = Visibility.Visible;

        try
        {
            var updater = _services.CreateModUpdater();
            await updater.ApplyAsync(row.Source, row.UpdateVersionId);
        }
        catch (Exception ex)
        {
            ShowDialog("更新失败", $"{row.Name}：\n\n{ex.Message}", allowCopy: true);
        }
        finally
        {
            LoadingText.Visibility = Visibility.Collapsed;
            _busy = false;
        }

        await RefreshAsync();
    }

    /// <summary>诊断冲突：依赖缺失 / 重复安装 / 声明不兼容。</summary>
    private async void OnDiagnoseClick(object sender, RoutedEventArgs e)
    {
        if (_instanceId is not { Length: > 0 } instanceId || _busy)
        {
            return;
        }

        _busy = true;
        LoadingText.Text = "正在读取依赖声明…";
        LoadingText.Visibility = Visibility.Visible;

        try
        {
            var instance = _services.Instances.Get(instanceId);
            var diagnostics = _services.CreateDiagnostics();
            var progress = new Progress<string>(text => LoadingText.Text = text);

            var report = await diagnostics.AnalyzeAsync(
                instanceId,
                instance?.VersionId ?? instanceId,
                progress);

            ShowDialog(
                report.IsClean ? "没有发现问题" : "诊断结果",
                report.Describe(),
                allowCopy: report.TotalIssues > 0);
        }
        catch (Exception ex)
        {
            ShowDialog("诊断失败", ex.Message, allowCopy: true);
        }
        finally
        {
            LoadingText.Visibility = Visibility.Collapsed;
            _busy = false;
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void ShowDialog(string title, string message, bool allowCopy = false)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.ShowDialog(title, message, allowCopy);
        }
    }
}
