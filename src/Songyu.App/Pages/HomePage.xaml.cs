using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Songyu.Services;

namespace Songyu.App.Pages;

public partial class HomePage : UserControl
{
    private readonly AppServices _services;
    private readonly SkinService _skins;
    private string? _selectedInstanceId;
    private CancellationTokenSource? _previewCts;
    private string? _cachedSkinUrl;
    private SkinInfo? _cachedSkin;
    private string? _cachedCapeUrl;
    private byte[]? _cachedCape;
    private SkinInfo? _currentSkin;
    private byte[]? _currentCape;
    private bool _viewerReady;
    private bool _initializingViewer;
    private bool _previewRendered;
    private int _renderRequestId;
    private TaskCompletionSource? _bridgeReady;
    private FrameworkElement? _dialogLayer;

    public HomePage(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _skins = new SkinService(services.Paths, services.Http);
        Loaded += OnLoadedAsync;
        Unloaded += (_, _) =>
        {
            _previewCts?.Cancel();
            HomeSkinView.Visibility = Visibility.Hidden;
            _ = SetRenderingPausedAsync(true);
        };
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        Refresh();
        if (_dialogLayer is null && Window.GetWindow(this)?.FindName("DialogLayer") is FrameworkElement layer)
        {
            _dialogLayer = layer;
            layer.IsVisibleChanged += (_, _) => UpdateViewerVisibility();
        }
        if (_viewerReady || _initializingViewer)
        {
            UpdateViewerVisibility();
            _ = SetRenderingPausedAsync(false);
            return;
        }

        _initializingViewer = true;
        try
        {
            var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "skinview");
            if (!Directory.Exists(assets))
            {
                throw new DirectoryNotFoundException("找不到 3D 预览组件");
            }

            var userData = Path.Combine(_services.Paths.Root, "webview2");
            Directory.CreateDirectory(userData);
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userData,
                options: new CoreWebView2EnvironmentOptions("--ignore-gpu-blocklist"));
            await HomeSkinView.EnsureCoreWebView2Async(environment);
            HomeSkinView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "songyu.skin", assets, CoreWebView2HostResourceAccessKind.Allow);
            HomeSkinView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            HomeSkinView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            HomeSkinView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            HomeSkinView.CoreWebView2.WebMessageReceived += OnViewerMessage;

            var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridgeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavigationCompleted(object? _, CoreWebView2NavigationCompletedEventArgs args)
            {
                if (args.IsSuccess) { navigation.TrySetResult(); }
                else { navigation.TrySetException(new InvalidOperationException($"3D 预览加载失败：{args.WebErrorStatus}")); }
            }

            HomeSkinView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            HomeSkinView.Source = new Uri("https://songyu.skin/skinview.html?home=1");
            try
            {
                await navigation.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await _bridgeReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally { HomeSkinView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted; }

            _viewerReady = true;
            await SetRenderingPausedAsync(!IsLoaded);
            if (_currentSkin is not null) { await PushSkinAsync(); }
        }
        catch (Exception)
        {
            SkinPreviewEmpty.Text = "3D 预览暂不可用";
            SkinPreviewEmpty.Visibility = Visibility.Visible;
            _previewRendered = false;
            UpdateViewerVisibility();
        }
        finally { _initializingViewer = false; }
    }

    private void OnViewerMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var message = JsonDocument.Parse(args.TryGetWebMessageAsString());
            var root = message.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "ready") { _bridgeReady?.TrySetResult(); }
            else if (type == "loaded" &&
                     root.TryGetProperty("requestId", out var request) &&
                     request.TryGetInt32(out var requestId) && requestId == _renderRequestId)
            {
                _previewRendered = true;
                SkinPreviewEmpty.Visibility = Visibility.Collapsed;
                UpdateViewerVisibility();
                if (_currentCape is not null) { _ = PushCapeAsync(); }
                _ = HomeSkinView.ExecuteScriptAsync("window.skinBridge.refresh()");
            }
            else if (type == "error" && _currentSkin is not null)
            {
                SkinPreviewEmpty.Text = "3D 预览暂不可用";
                SkinPreviewEmpty.Visibility = Visibility.Visible;
                _previewRendered = false;
                UpdateViewerVisibility();
            }
        }
        catch (Exception) { /* 非预期消息不影响首页 */ }
    }

    private void UpdateViewerVisibility()
    {
        HomeSkinView.Visibility = _previewRendered && IsLoaded && _dialogLayer?.IsVisible != true
            ? Visibility.Visible : Visibility.Hidden;
    }

    private async Task PushSkinAsync()
    {
        if (!_viewerReady || !IsLoaded || _currentSkin is not { } skin) { return; }
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(skin.Png);
        var model = skin.IsSlim ? "slim" : "classic";
        var script = $"window.skinBridge.reset(); window.skinBridge.setSkin({JsonSerializer.Serialize(dataUrl)}, {JsonSerializer.Serialize(model)}, {_renderRequestId})";
        await HomeSkinView.ExecuteScriptAsync(script);
    }

    private async Task PushCapeAsync()
    {
        if (!_viewerReady || !_previewRendered || _currentCape is not { } cape) { return; }
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(cape);
        try { await HomeSkinView.ExecuteScriptAsync($"window.skinBridge.setCape({JsonSerializer.Serialize(dataUrl)})"); }
        catch (Exception) { /* 披风不影响角色预览 */ }
    }

    private async Task SetRenderingPausedAsync(bool paused)
    {
        if (!_viewerReady) { return; }
        try { await HomeSkinView.ExecuteScriptAsync($"window.skinBridge.setRenderingPaused({(paused ? "true" : "false")})"); }
        catch (Exception) { /* 页面切换期间 WebView 可能已卸载 */ }
    }

    /// <summary>首页当前选中的实例，别的页面（Mod 管理）据此定位。</summary>
    public string? SelectedInstanceId => _selectedInstanceId;

    /// <summary>首页列表的一行：显示的是"实例"，不是"版本"。</summary>
    public sealed record InstanceRow(string Id, string Name, string VersionId, string DisplayType);

    public void Refresh()
    {
        var versions = _services.LocalVersions.List()
            .ToDictionary(v => v.Id, v => v.DisplayType, StringComparer.OrdinalIgnoreCase);

        var rows = _services.Instances.List()
            .Select(i => new InstanceRow(
                i.Id,
                string.IsNullOrWhiteSpace(i.Name) ? i.Id : i.Name,
                i.VersionId,
                versions.TryGetValue(i.VersionId, out var kind) ? kind : $"版本未安装（{i.VersionId}）"))
            .ToList();

        // 刷新列表时尽量保住当前选中的实例
        var previous = _selectedInstanceId;
        VersionList.ItemsSource = rows;

        var restored = rows.FirstOrDefault(r => r.Id == previous) ?? rows.FirstOrDefault();
        if (restored is not null)
        {
            VersionList.SelectedItem = restored;
            _selectedInstanceId = restored.Id;
        }
        else
        {
            _selectedInstanceId = null;
        }

        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        JavaWarning.Visibility = _services.BestJava is null ? Visibility.Visible : Visibility.Collapsed;

        UpdateLaunchState();
        RefreshAccount();
    }

    private void UpdateLaunchState()
    {
        var row = _selectedInstanceId is null
            ? null
            : (VersionList.SelectedItem as InstanceRow);

        var account = _services.CreateAccountStore().Load();
        var auth = _services.CreateMicrosoftAuth();
        var canLaunch = account is { Name.Length: > 0, RefreshToken.Length: > 0 } &&
            auth.IsConfigured &&
            string.Equals(account.ClientId, auth.ClientId,
                StringComparison.OrdinalIgnoreCase);
        SelectedVersionText.Text = row is null ? "还没有选择实例"
            : canLaunch ? row.Name : "请先使用获批的 Client ID 登录";
        LaunchButton.IsEnabled = row is not null && canLaunch;
    }

    private void OnVersionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is InstanceRow row)
        {
            _selectedInstanceId = row.Id;
            UpdateLaunchState();
        }
    }

    private void RefreshAccount()
    {
        var account = _services.CreateAccountStore().Load();
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        _renderRequestId++;
        _currentSkin = null;
        _currentCape = null;
        _previewRendered = false;
        UpdateViewerVisibility();
        AvatarImage.Source = null;
        SkinPreviewEmpty.Text = "暂无皮肤";
        SkinPreviewEmpty.Visibility = Visibility.Visible;
        SkinPreviewLabel.Text = "未选择皮肤";
        var auth = _services.CreateMicrosoftAuth();
        var signedIn = account?.Name is { Length: > 0 } &&
            !string.IsNullOrWhiteSpace(account.RefreshToken) && auth.IsConfigured &&
            string.Equals(account.ClientId, auth.ClientId, StringComparison.OrdinalIgnoreCase);
        OfflineSkinActions.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        OfflineSkinHintText.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        OfflineSkinStatusText.Text = _services.SelectedOfflineSkinPath is { } selected
            ? "当前选择：" + (_services.Config.OfflineSkinDisplayName ?? Path.GetFileNameWithoutExtension(selected))
            : "当前选择：未选择皮肤";
        OfflineSkinStatusText.ToolTip = OfflineSkinStatusText.Text;

        if (account?.Name is { Length: > 0 } name)
        {
            AccountNameText.Text = name;
            AccountTypeText.Text = signedIn ? "正版账号" : "登录已失效 · 请重新登录";
        }
        else
        {
            AccountNameText.Text = "未登录";
            AccountTypeText.Text = "游戏启动需要正版账号";
        }

        UpdateLaunchState();

        _ = RefreshPreviewAsync(signedIn ? account?.SkinUrl : null,
            signedIn ? account?.CapeUrl : null, _previewCts.Token);
    }

    private async Task RefreshPreviewAsync(string? skinUrl, string? capeUrl, CancellationToken token)
    {
        try
        {
            SkinInfo? skin = null;
            if (!string.IsNullOrWhiteSpace(skinUrl))
            {
                try
                {
                    if (_cachedSkinUrl == skinUrl) { skin = _cachedSkin; }
                    else
                    {
                        skin = await _skins.DownloadAsync(skinUrl, token);
                        _cachedSkinUrl = skinUrl;
                        _cachedSkin = skin;
                    }
                }
                catch (Exception) when (!token.IsCancellationRequested) { /* 本地皮肤兜底 */ }
            }

            if (skin is null)
            {
                var path = _services.SelectedOfflineSkinPath;
                if (path is not null)
                {
                    try { skin = await _skins.LoadFileAsync(path, token); }
                    catch (Exception) when (!token.IsCancellationRequested) { /* 显示空状态 */ }
                }
            }

            token.ThrowIfCancellationRequested();
            if (skin is not null)
            {
                _currentSkin = skin;
                AvatarImage.Source = SkinHelper.CropHead(skin.Png);
                SkinPreviewEmpty.Text = "正在加载 3D 角色…";
                SkinPreviewLabel.Text = skin.Source == skinUrl
                    ? "账号皮肤"
                    : _services.Config.OfflineSkinDisplayName ?? Path.GetFileNameWithoutExtension(skin.Source);
                SkinPreviewLabel.ToolTip = skin.Source;
                await PushSkinAsync();
            }

            if (skin is null || string.IsNullOrWhiteSpace(capeUrl)) { return; }
            try
            {
                byte[] cape;
                if (_cachedCapeUrl == capeUrl && _cachedCape is not null) { cape = _cachedCape; }
                else
                {
                    cape = await _services.Http.GetByteArrayAsync(capeUrl, token);
                    _cachedCapeUrl = capeUrl;
                    _cachedCape = cape;
                }
                token.ThrowIfCancellationRequested();
                if (SkinHelper.RenderCape(cape) is not null)
                {
                    _currentCape = cape;
                    await PushCapeAsync();
                }
            }
            catch (Exception) when (!token.IsCancellationRequested) { /* 披风下载失败不影响皮肤 */ }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 首页预览失败不影响启动 */ }
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main) { main.NavigateToSkin(); }
    }

    private async void OnChooseOfflineSkinClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择本地预览皮肤",
            Filter = "皮肤图 (*.png)|*.png",
            InitialDirectory = _skins.SkinsDirectory,
        };
        if (dialog.ShowDialog() != true || Window.GetWindow(this) is not MainWindow main) { return; }
        try
        {
            var skin = await _skins.LoadFileAsync(dialog.FileName);
            await _services.SelectOfflineSkinAsync(skin);
            Refresh();
            main.ShowToast("已设置本地预览皮肤");
        }
        catch (Exception ex) { main.ShowDialog("选择皮肤失败", ex.Message, allowCopy: true); }
    }

    private async void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        if (_selectedInstanceId is null || Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        await main.RunInstanceAsync(_selectedInstanceId);
        Refresh();
    }

    private void OnChooseVersionClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.NavigateToDownload();
        }
    }

    private void OnVersionSettingsClick(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is not InstanceRow row ||
            Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        // 传实例 ID：同版本的多个实例各有各的设置
        main.ShowVersionSettings(row.Id);
    }

    private void OnModsClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.NavigateToMods(_selectedInstanceId);
        }
    }

    // ── 实例操作（右键菜单）──

    private async void OnNewInstanceClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        if (VersionList.SelectedItem is not InstanceRow basis)
        {
            main.ShowDialog("先选一个实例", "新建的实例会沿用选中实例的版本，先在列表里点一个。");
            return;
        }

        var name = await main.ShowInputAsync(
            "新建实例",
            $"新实例会沿用版本「{basis.VersionId}」，mods 和存档各自独立。\n\n给它起个名字：",
            "新实例");

        if (name is null)
        {
            return;
        }

        try
        {
            var info = _services.Instances.Create(name, basis.VersionId);
            _selectedInstanceId = info.Id;
            Refresh();
        }
        catch (Exception ex)
        {
            main.ShowDialog("新建失败", ex.Message, allowCopy: true);
        }
    }

    private async void OnRenameInstanceClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main ||
            VersionList.SelectedItem is not InstanceRow row)
        {
            return;
        }

        var name = await main.ShowInputAsync(
            "重命名实例",
            $"只改显示名，目录名（{row.Id}）不变，所以不会影响已有存档和 mods。",
            row.Name);

        if (name is null)
        {
            return;
        }

        _services.Instances.Rename(row.Id, name);
        Refresh();
    }

    private async void OnDeleteInstanceClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main ||
            VersionList.SelectedItem is not InstanceRow row)
        {
            return;
        }

        var confirmed = await main.ShowConfirmAsync(
            "删除实例",
            $"要删除「{row.Name}」吗？\n\n" +
            $"目录：{_services.Instances.GameDirectory(row.Id)}\n\n" +
            "存档和 mods 都在这个目录里。删除只是把它挪到 instances\\.trash\\，" +
            "不会真的抹掉，需要时可以翻回来。",
            "删除",
            "取消");

        if (!confirmed)
        {
            return;
        }

        var trash = _services.Instances.Delete(row.Id);
        _selectedInstanceId = null;

        if (trash is null)
        {
            main.ShowDialog("删除失败", "这个实例的目录不存在。", allowCopy: true);
        }

        Refresh();
    }

    private void OnOpenInstanceDirClick(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is not InstanceRow row ||
            Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        var directory = _services.Instances.EnsureGameDirectory(row.Id);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            main.ShowDialog("打不开目录", $"{ex.Message}\n\n路径：\n{directory}", allowCopy: true);
        }
    }

    private void OnAccountClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.ShowAccountPanel();
        }
    }

    private void OnJavaHelpClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        main.ShowDialog(
            "怎么安装 Java",
            "1. 打开 https://adoptium.net/temurin/releases/\n" +
            "2. 选 Windows / x64 / JDK 21（或 17），下载 .msi\n" +
            "3. 双击安装，全部下一步即可\n" +
            "4. 装完重启「凇语 MCloader」\n\n" +
            "提示：Minecraft 1.17 之后需要 Java 17+，1.20.5 之后建议 Java 21。\n" +
            "已经装了 Java 却仍提示找不到，多半是装的是 JRE 而不是 JDK，或者装在非常规目录——" +
            "可以在「设置」页看启动器扫到了哪些 Java。");
    }
}
