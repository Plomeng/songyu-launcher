using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

    public HomePage(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _skins = new SkinService(services.Paths, services.Http);
        Loaded += (_, _) => Refresh();
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

        SelectedVersionText.Text = row?.Name ?? "还没有选择实例";
        LaunchButton.IsEnabled = row is not null;
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
        SkinPreviewImage.Source = null;
        CapePreviewImage.Source = null;
        AvatarImage.Source = null;
        SkinPreviewEmpty.Visibility = Visibility.Visible;
        CapePreviewEmpty.Visibility = Visibility.Visible;
        SkinPreviewLabel.Text = "皮肤";

        if (account?.Name is { Length: > 0 } name)
        {
            AccountNameText.Text = name;
            AccountTypeText.Text = "正版账号";
        }
        else
        {
            AccountNameText.Text = string.IsNullOrWhiteSpace(_services.Config.PlayerName)
                ? "Player"
                : _services.Config.PlayerName;
            AccountTypeText.Text = "离线账号";
        }

        _ = RefreshPreviewAsync(account?.SkinUrl, account?.CapeUrl, _previewCts.Token);
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
                foreach (var path in _skins.ListLibrary())
                {
                    try { skin = await _skins.LoadFileAsync(path, token); break; }
                    catch (Exception) when (!token.IsCancellationRequested) { /* 忽略损坏的图片 */ }
                }
            }

            token.ThrowIfCancellationRequested();
            if (skin is not null)
            {
                SkinPreviewImage.Source = SkinHelper.RenderFront(skin.Png);
                AvatarImage.Source = SkinHelper.CropHead(skin.Png);
                SkinPreviewEmpty.Visibility = Visibility.Collapsed;
                SkinPreviewLabel.Text = skin.Source == skinUrl ? "账号皮肤" : "本地皮肤";
            }

            if (string.IsNullOrWhiteSpace(capeUrl)) { return; }
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
                if (SkinHelper.RenderCape(cape) is { } image)
                {
                    CapePreviewImage.Source = image;
                    CapePreviewEmpty.Visibility = Visibility.Collapsed;
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
