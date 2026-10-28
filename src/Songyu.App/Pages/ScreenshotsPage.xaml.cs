using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Songyu.Services;

namespace Songyu.App.Pages;

/// <summary>
/// 截图页：列出当前实例 <c>screenshots/</c> 下的图，缩略图用解码尺寸压到 240px 宽，
/// 几百张截图也不会把内存吃掉。
/// </summary>
public partial class ScreenshotsPage : UserControl
{
    private readonly AppServices _services;
    private readonly ScreenshotScanner _scanner;

    private string? _instanceId;
    private List<ShotRow> _rows = [];

    public ScreenshotsPage(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _scanner = new ScreenshotScanner(services.Paths);
    }

    /// <summary>界面用的一行：截图本身 + 解码后的缩略图。</summary>
    private sealed record ShotRow(Screenshot Shot, BitmapImage? Thumbnail)
    {
        public string CapturedText => Shot.CapturedText;

        public string DetailText => $"{Shot.FileName}   ·   {Shot.SizeText}";
    }

    /// <summary>切入页面时调用。实例由首页选中项决定。</summary>
    public void Load(string? instanceId = null)
    {
        var instance = _services.Instances.List()
            .FirstOrDefault(i => i.Id == instanceId)
            ?? _services.Instances.List().FirstOrDefault(i => i.Id == _instanceId)
            ?? _services.Instances.List().FirstOrDefault();

        if (instance is null)
        {
            _instanceId = null;
            ShowEmpty("还没有实例", "去「下载」页面装一个版本，玩的时候按 F2 就有截图了。");
            return;
        }

        _instanceId = instance.Id;

        SubtitleText.Text = string.Equals(instance.Name, instance.VersionId, StringComparison.Ordinal)
            ? instance.VersionId
            : $"{instance.Name}   ·   {instance.VersionId}";

        PathText.Text = _scanner.DirectoryFor(instance.Id);

        Refresh();
    }

    private void Refresh()
    {
        if (_instanceId is not { Length: > 0 } instanceId)
        {
            return;
        }

        var shots = _scanner.Scan(instanceId);

        // 缩略图在后台解，避免一次性卡住 UI
        _rows = shots.Select(s => new ShotRow(s, LoadThumbnail(s.FullPath))).ToList();
        ShotList.ItemsSource = _rows;

        var totalMb = shots.Sum(s => s.Bytes) / 1024.0 / 1024;
        CountText.Text = shots.Count == 0
            ? "还没有截图"
            : $"共 {shots.Count} 张，{totalMb:F1} MB";

        if (shots.Count == 0)
        {
            ShowEmpty("还没有截图", "游戏里按 F2 截图，会出现在这个目录里。");
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 用 DecodePixelWidth 解码：WPF 会把整张图读进内存，
    /// 一张 2560×1440 的 PNG 解出来约 14MB，几十张就能吃掉几百兆。
    /// </summary>
    private static BitmapImage? LoadThumbnail(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.DecodePixelWidth = 240;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch
        {
            return null;
        }
    }

    private void ShowEmpty(string title, string subtitle)
    {
        ShotList.ItemsSource = null;
        CountText.Text = string.Empty;

        EmptyTitle.Text = title;
        EmptySubtitle.Text = subtitle;
        EmptyHint.Visibility = Visibility.Visible;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_instanceId is not { Length: > 0 } instanceId)
        {
            return;
        }

        try
        {
            _scanner.OpenFolder(instanceId);
        }
        catch (Exception ex)
        {
            ShowDialog("打不开目录", ex.Message);
        }
    }

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ShotList.SelectedItem is ShotRow row)
        {
            OpenShot(row);
        }
    }

    private void OnOpenShotClick(object sender, RoutedEventArgs e)
    {
        if (ShotList.SelectedItem is ShotRow row)
        {
            OpenShot(row);
        }
    }

    private void OnRevealShotClick(object sender, RoutedEventArgs e)
    {
        if (ShotList.SelectedItem is not ShotRow row)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{row.Shot.FullPath}\"");
        }
        catch (Exception ex)
        {
            ShowDialog("打不开文件夹", ex.Message);
        }
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (ShotList.SelectedItem is not ShotRow row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(row.Shot.FullPath);
        }
        catch
        {
            // 剪贴板被别的程序占着，忽略
        }
    }

    private async void OnDeleteShotClick(object sender, RoutedEventArgs e)
    {
        if (ShotList.SelectedItem is not ShotRow row ||
            Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        var confirmed = await main.ShowConfirmAsync(
            "删除截图",
            $"要删除「{row.Shot.FileName}」吗？\n\n" +
            "不会真删，会挪到实例的 .trash\\screenshots 里，需要时可以翻回来。",
            "删除",
            "取消");

        if (!confirmed)
        {
            return;
        }

        try
        {
            _scanner.Delete(row.Shot);
        }
        catch (Exception ex)
        {
            ShowDialog("删除失败", ex.Message, allowCopy: true);
        }

        Refresh();
    }

    private void OpenShot(ShotRow row)
    {
        try
        {
            ScreenshotScanner.OpenFile(row.Shot);
        }
        catch (Exception ex)
        {
            ShowDialog("打不开图片", ex.Message);
        }
    }

    private void ShowDialog(string title, string message, bool allowCopy = false)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.ShowDialog(title, message, allowCopy);
        }
    }
}
