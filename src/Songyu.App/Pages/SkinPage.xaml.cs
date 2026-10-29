using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Songyu.Services;

namespace Songyu.App.Pages;

/// <summary>
/// 皮肤页：用 WebView2 承载 skinview3d（MIT）渲染 3D 角色模型。
/// <para>
/// 前端的 HTML 和 JS 打包在 <c>Assets/skinview/</c> 里随程序发布，用虚拟主机
/// <c>https://songyu.skin/</c> 映射到该目录，避免 file:// 协议下的各种限制。
/// </para>
/// </summary>
public partial class SkinPage : UserControl
{
    private const string VirtualHost = "songyu.skin";

    private readonly AppServices _services;
    private readonly SkinService _skins;

    private SkinInfo? _skin;
    private bool _slim;
    private bool _viewerReady;
    private bool _initializing;
    private TaskCompletionSource? _bridgeReady;
    private bool _animationPaused;
    private FrameworkElement? _dialogLayer;

    public SkinPage(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _skins = new SkinService(services.Paths, services.Http);

        Loaded += OnLoadedAsync;
        Unloaded += (_, _) => { /* 页面切走时不销毁 WebView，回来还能用 */ };
    }

    /// <summary>切入页面时调用。</summary>
    public void Load(bool showSelectedOfflineSkin = false)
    {
        var account = _services.CreateAccountStore().Load();

        SubtitleText.Text = account?.Name is { Length: > 0 } name
            ? $"当前账号：{name}"
            : "未登录 · 可以导入本地 PNG 预览";

        RefreshLibrary();
        if (showSelectedOfflineSkin && account?.Name is not { Length: > 0 } &&
            _services.SelectedOfflineSkinPath is { } selected)
        {
            _ = LoadSelectedOfflineSkinAsync(selected);
            return;
        }
        if (_skin is null)
        {
            _ = LoadInitialSkinAsync(account?.SkinUrl);
        }
    }

    private async Task LoadSelectedOfflineSkinAsync(string path)
    {
        try { await ApplySkinAsync(await _skins.LoadFileAsync(path)); }
        catch { /* 页面仍可导入别的皮肤 */ }
    }

    private async Task LoadInitialSkinAsync(string? skinUrl)
    {
        if (_skin is not null) { return; }
        if (!string.IsNullOrWhiteSpace(skinUrl))
        {
            try { await ApplySkinAsync(await _skins.DownloadAsync(skinUrl)); return; }
            catch { /* 本地皮肤兜底 */ }
        }

        if (_services.SelectedOfflineSkinPath is { } selected)
        {
            try { await ApplySkinAsync(await _skins.LoadFileAsync(selected)); return; }
            catch { /* 文件不可用时继续尝试皮肤库 */ }
        }

        foreach (var path in _skins.ListLibrary())
        {
            try { await ApplySkinAsync(await _skins.LoadFileAsync(path)); return; }
            catch { /* 跳过无法读取的皮肤 */ }
        }
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        if (_dialogLayer is null && Window.GetWindow(this)?.FindName("DialogLayer") is FrameworkElement layer)
        {
            _dialogLayer = layer;
            layer.IsVisibleChanged += (_, _) => UpdateViewerVisibility();
        }
        if (_viewerReady || _initializing)
        {
            return;
        }

        _initializing = true;
        try
        {
            var userData = Path.Combine(_services.Paths.Root, "webview2");
            Directory.CreateDirectory(userData);

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userData,
                options: new CoreWebView2EnvironmentOptions(
                    // 只放开被拉黑的显卡；不能用 --enable-unsafe-swiftshader，
                    // 软件 WebGL 画出来的内容不会进入合成层，表现就是渲染成功但看不见
                    "--ignore-gpu-blocklist"));

            await SkinView.EnsureCoreWebView2Async(environment);

            var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "skinview");

            if (!Directory.Exists(assets))
            {
                ShowFallback("预览组件缺失", $"找不到 {assets}。\n\n重新完整解压程序包再试。");
                return;
            }

            SkinView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHost, assets, CoreWebView2HostResourceAccessKind.Allow);

            // 右键菜单和调试快捷键在成品里没必要
            SkinView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            SkinView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            SkinView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            SkinView.CoreWebView2.WebMessageReceived -= OnWebMessage;
            SkinView.CoreWebView2.WebMessageReceived += OnWebMessage;

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _bridgeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavigationCompleted(object? _, CoreWebView2NavigationCompletedEventArgs args)
            {
                if (args.IsSuccess)
                {
                    ready.TrySetResult();
                }
                else
                {
                    ready.TrySetException(new InvalidOperationException(
                        $"预览页加载失败：{args.WebErrorStatus}"));
                }
            }

            SkinView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            SkinView.Source = new Uri($"https://{VirtualHost}/skinview.html");

            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await _bridgeReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally { SkinView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted; }

            _viewerReady = true;

            if (_skin is not null)
            {
                await PushSkinAsync();
            }
        }
        catch (Exception ex)
        {
            ShowFallback(
                "3D 预览启动失败",
                ex.Message + "\n\n可以继续用「导入 PNG」加载皮肤并保存，" +
                "预览需要系统里的 WebView2 运行时（Windows 10/11 一般自带）。");
        }
        finally { _initializing = false; }
    }

    /// <summary>
    /// 前端确认脚本就绪、皮肤加载成功或渲染失败，避免调用静默丢失。
    /// </summary>
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var raw = args.TryGetWebMessageAsString();

            if (string.IsNullOrEmpty(raw))
            {
                return;
            }

            if (!raw.StartsWith('{'))
            {
                FallbackTitle.Text = "前端消息";
                FallbackText.Text = raw;
                PreviewFallback.Visibility = Visibility.Visible;
                return;
            }

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var type)) { return; }

            if (type.GetString() == "error")
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() : "未知错误";
                _bridgeReady?.TrySetException(new InvalidOperationException(message));
                ShowFallback("预览出错", message ?? "未知错误");
                return;
            }

            if (type.GetString() == "ready")
            {
                _bridgeReady?.TrySetResult();
                return;
            }

            if (type.GetString() == "loaded")
            {
                PreviewFallback.Visibility = Visibility.Collapsed;
                UpdateViewerVisibility();
                AnglePanel.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            // 非预期消息不影响其余页面。
        }
    }

    private async Task SendRotationAsync(double dYaw, double dPitch)
    {
        if (!_viewerReady) { return; }
        await ExecuteViewerAsync(FormattableString.Invariant($"window.skinBridge.rotate({dYaw}, {dPitch})"));
    }

    private async void OnResetViewClick(object sender, RoutedEventArgs e)
    {
        if (!_viewerReady) { return; }
        await ExecuteViewerAsync("window.skinBridge.reset()");
    }

    private async void OnRotateLeftClick(object sender, RoutedEventArgs e) => await SendRotationAsync(-20, 0);

    private async void OnRotateRightClick(object sender, RoutedEventArgs e) => await SendRotationAsync(20, 0);

    private async void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        if (!_viewerReady) { return; }
        await ExecuteViewerAsync("window.skinBridge.zoom(0.85)");
    }

    private async void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        if (!_viewerReady) { return; }
        await ExecuteViewerAsync("window.skinBridge.zoom(1.18)");
    }

    private void ShowFallback(string title, string text)
    {
        FallbackTitle.Text = title;
        FallbackText.Text = text;
        PreviewFallback.Visibility = Visibility.Visible;
        SkinView.Visibility = Visibility.Collapsed;
    }

    private void UpdateViewerVisibility()
    {
        // 原生浏览器窗口需要在 WPF 模态弹窗出现时隐藏。
        SkinView.Visibility = PreviewFallback.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : _dialogLayer?.IsVisible == true ? Visibility.Hidden : Visibility.Visible;
    }

    // ── 皮肤操作 ──

    private async Task ApplySkinAsync(SkinInfo skin)
    {
        _skin = skin;
        _slim = skin.IsSlim;

        SkinNameText.Text = Path.GetFileName(skin.Source) is { Length: > 0 } fileName
            ? fileName
            : "皮肤";

        UpdateOfflineSelectionLabel();

        UpdateMetaText();

        if (_viewerReady)
        {
            await PushSkinAsync();
        }
    }

    private void UpdateOfflineSelectionLabel()
    {
        var selected = _services.SelectedOfflineSkinPath;
        SelectOfflineSkinButton.Content = _skin is not null && selected is not null &&
            string.Equals(_skin.Source, selected, StringComparison.OrdinalIgnoreCase)
                ? "当前首页预览皮肤 ✓"
                : "设为首页预览皮肤";
    }

    private async Task PushSkinAsync()
    {
        if (_skin is null || !_viewerReady)
        {
            return;
        }

        try
        {
            var dataUrl = "data:image/png;base64," + Convert.ToBase64String(_skin.Png);
            var model = _slim ? "slim" : "classic";

            SkinView.Visibility = Visibility.Visible;
            await ExecuteViewerAsync($"window.skinBridge.setSkin({ToJsString(dataUrl)}, {ToJsString(model)})");
            if (_animationPaused) { await ExecuteViewerAsync("window.skinBridge.setAnimationPaused(true)"); }
        }
        catch (Exception ex)
        {
            ShowFallback("预览失败", ex.Message);
        }
    }

    private void UpdateMetaText()
    {
        ModelButton.Content = _slim ? "模型：slim" : "模型：classic";
        if (_skin is null)
        {
            SkinMetaText.Text = string.Empty;
            return;
        }

        SkinMetaText.Text = $"{_skin.SizeText}   ·   {(_skin.IsModern ? "现代格式" : "旧版格式")}   ·   " +
                            $"{(_slim ? "slim（细手臂）" : "classic（标准手臂）")}";
    }

    private static string ToJsString(string value)
        => JsonSerializer.Serialize(value);

    private async Task ExecuteViewerAsync(string command)
    {
        try
        {
            var result = await SkinView.CoreWebView2.ExecuteScriptAsync(
                "(() => { try { if (!window.skinBridge) throw new Error('预览尚未就绪'); " +
                command + "; return null; } catch (e) { return String(e); } })()");
            if (result != "null")
            {
                ShowFallback("预览失败", JsonSerializer.Deserialize<string>(result) ?? "未知错误");
            }
        }
        catch (Exception ex) { ShowFallback("预览失败", ex.Message); }
    }

    private void RefreshLibrary()
    {
        var files = _skins.ListLibrary();

        LibraryList.ItemsSource = files;
        LibraryEmpty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 界面事件 ──

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择皮肤",
            Filter = "皮肤图 (*.png)|*.png|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true || Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        try
        {
            var skin = await _skins.LoadFileAsync(dialog.FileName);
            await ApplySkinAsync(skin);
            main.ShowToast("皮肤已加载");
        }
        catch (Exception ex)
        {
            main.ShowDialog("这个文件不能当皮肤", ex.Message, allowCopy: true);
        }
    }

    private async void OnUseAccountSkinClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        var account = _services.CreateAccountStore().Load();

        if (account is null || string.IsNullOrEmpty(account.Uuid))
        {
            main.ShowDialog(
                "还没有正版登录",
                "从账号取皮肤需要先登录正版账号。\n\n当前可以先用「导入 PNG」加载本地皮肤文件。");
            return;
        }

        if (string.IsNullOrEmpty(account.SkinUrl))
        {
            main.ShowDialog(
                "账号没有皮肤",
                $"{account.Name} 用的是默认皮肤（Steve / Alex），没有可下载的贴图。");
            return;
        }

        try
        {
            main.ShowToast("正在下载账号皮肤…");
            var skin = await _skins.DownloadAsync(account.SkinUrl);

            await ApplySkinAsync(skin);
            main.ShowToast($"已加载 {account.Name} 的皮肤");
        }
        catch (Exception ex)
        {
            main.ShowDialog("下载皮肤失败", ex.Message, allowCopy: true);
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_skin is null || Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        var name = await main.ShowInputAsync(
            "保存到皮肤库",
            "给这张皮肤起个名字（会存到 data/skins/）：",
            Path.GetFileNameWithoutExtension(_skin.Source) is { Length: > 0 } suggested
                ? suggested
                : "my-skin",
            "例如：圣诞皮肤");

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var path = await _skins.SaveAsync(_skin.Png, name.Trim());
            RefreshLibrary();
            main.ShowToast($"已保存到 {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            main.ShowDialog("保存失败", ex.Message, allowCopy: true);
        }
    }

    private async void OnSelectOfflineSkinClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main) { return; }
        if (_skin is null)
        {
            main.ShowToast("先导入一张皮肤，或从皮肤库选择");
            return;
        }

        try
        {
            var selected = await _services.SelectOfflineSkinAsync(_skin);
            _skin = _skin with { Source = selected };
            UpdateOfflineSelectionLabel();
            main.RefreshAllPages();
            main.ShowToast("已设置首页预览皮肤");
        }
        catch (Exception ex) { main.ShowDialog("设置首页预览皮肤失败", ex.Message, allowCopy: true); }
    }

    private async void OnLibrarySelected(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryList.SelectedItem is not string path || Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        try
        {
            var skin = await _skins.LoadFileAsync(path);
            await ApplySkinAsync(skin);
        }
        catch (Exception ex)
        {
            main.ShowToast("这张皮肤读不了：" + ex.Message);
        }

        LibraryList.SelectedItem = null;
    }

    private async void OnToggleModelClick(object sender, RoutedEventArgs e)
    {
        if (_skin is null)
        {
            return;
        }

        _slim = !_slim;
        UpdateMetaText();

        if (_viewerReady)
        {
            await ExecuteViewerAsync($"window.skinBridge.setModel({ToJsString(_slim ? "slim" : "classic")})");
        }
    }

    private async void OnToggleAnimationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        _animationPaused = !_animationPaused;
        button.Content = _animationPaused ? "继续动画" : "暂停动画";

        if (_viewerReady)
        {
            await ExecuteViewerAsync($"window.skinBridge.setAnimationPaused({(_animationPaused ? "true" : "false")})");
        }
    }
}
