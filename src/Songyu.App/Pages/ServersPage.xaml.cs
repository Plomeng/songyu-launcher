using System.Windows;
using System.Windows.Controls;
using Songyu.Services;

namespace Songyu.App.Pages;

/// <summary>
/// 联机页：直接读写实例的 <c>servers.dat</c>，和游戏内「多人游戏」共用同一份列表。
/// 地址支持 <c>host</c> 或 <c>host:port</c>，不带端口时按 25565 处理。
/// </summary>
public partial class ServersPage : UserControl
{
    private readonly AppServices _services;
    private readonly ServerListService _servers;

    private string? _instanceId;
    private List<GameServer> _rows = [];

    public ServersPage(AppServices services)
    {
        InitializeComponent();
        _services = services;
        _servers = new ServerListService(services.Paths);
    }

    /// <summary>切入页面时调用，实例由首页选中项决定。</summary>
    public void Load(string? instanceId = null)
    {
        var instance = _services.Instances.List()
            .FirstOrDefault(i => i.Id == instanceId)
            ?? _services.Instances.List().FirstOrDefault(i => i.Id == _instanceId)
            ?? _services.Instances.List().FirstOrDefault();

        if (instance is null)
        {
            _instanceId = null;
            CountText.Text = string.Empty;
            SubtitleText.Text = "还没有实例";
            PathText.Text = "先去「下载」页面装一个版本";
            ServerList.ItemsSource = null;
            ShowEmpty("还没有实例", "服务器列表跟着实例走。先装一个版本，再回来添加服务器。");
            return;
        }

        _instanceId = instance.Id;

        SubtitleText.Text = string.Equals(instance.Name, instance.VersionId, StringComparison.Ordinal)
            ? instance.VersionId
            : $"{instance.Name}   ·   {instance.VersionId}";

        PathText.Text = _servers.ServersDatPath(instance.Id);

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_instanceId is not { Length: > 0 } instanceId)
        {
            return;
        }

        try
        {
            _rows = (await _servers.ReadAsync(instanceId)).ToList();
            ServerList.ItemsSource = _rows;

            CountText.Text = _rows.Count == 0 ? "还没有服务器" : $"共 {_rows.Count} 个服务器";

            if (_rows.Count == 0)
            {
                ShowEmpty("还没有服务器", "点右上角「添加服务器」，填地址就行（支持 host 或 host:port）。");
            }
            else
            {
                EmptyHint.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            ShowEmpty("读取失败", ex.Message);
            CountText.Text = "读取失败";
        }
    }

    private void ShowEmpty(string title, string subtitle)
    {
        EmptyTitle.Text = title;
        EmptySubtitle.Text = subtitle;
        EmptyHint.Visibility = Visibility.Visible;
    }

    private async void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (_instanceId is not { Length: > 0 } || Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        var address = await main.ShowInputAsync(
            "添加服务器",
            "填服务器地址，支持 host 或 host:port（不带端口按 25565 处理）：",
            null,
            "例如：mc.example.com 或 play.example.com:25566");

        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        if (!IsValidAddress(address.Trim()))
        {
            ShowDialog("地址不对", $"「{address}」不像一个服务器地址。\n\n只填域名或 IP 即可，端口可选（host:port）。");
            return;
        }

        _rows.Add(new GameServer(address.Trim(), address.Trim(), false));
        await SaveAsync();
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (ResolveRow(sender) is not { } row || Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        var confirmed = await main.ShowConfirmAsync(
            "删除服务器",
            $"要从列表里移除「{row.DisplayName}」吗？\n\n地址：{row.Address}\n\n" +
            "只删启动器里的这条记录，游戏内的服务器数据也一并更新（两者是同一个 servers.dat）。",
            "删除",
            "取消");

        if (!confirmed)
        {
            return;
        }

        _rows.Remove(row);
        await SaveAsync();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (ResolveRow(sender) is not { } row)
        {
            return;
        }

        try
        {
            Clipboard.SetText(row.Address);
            if (Window.GetWindow(this) is MainWindow main)
            {
                main.ShowToast($"已复制 {row.Address}");
            }
        }
        catch
        {
            // 剪贴板被别的程序占用，忽略
        }
    }

    private async void OnJoinClick(object sender, RoutedEventArgs e)
    {
        if (ResolveRow(sender) is not { } row ||
            _instanceId is not { Length: > 0 } instanceId ||
            Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        var (host, port) = ParseAddress(row.Address);

        var confirmed = await main.ShowConfirmAsync(
            "进入服务器",
            $"启动游戏并直接连到「{row.DisplayName}」？\n\n服务器：{host}:{port}\n\n" +
            "游戏会用 --server / --port 参数启动，进去后直接尝试连接。",
            "启动",
            "取消");

        if (!confirmed)
        {
            return;
        }

        await main.RunInstanceAsync(instanceId, ["--server", host, "--port", port.ToString()]);
    }

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ServerList.SelectedItem is GameServer)
        {
            OnJoinClick(sender, new RoutedEventArgs());
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_instanceId is not { Length: > 0 } instanceId)
        {
            return;
        }

        try
        {
            var directory = _services.Paths.InstanceDir(instanceId);
            System.IO.Directory.CreateDirectory(directory);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowDialog("打不开目录", ex.Message);
        }
    }

    private async Task SaveAsync()
    {
        if (_instanceId is not { Length: > 0 } instanceId)
        {
            return;
        }

        try
        {
            await _servers.SaveAsync(instanceId, _rows);

            if (Window.GetWindow(this) is MainWindow main)
            {
                main.ShowToast("服务器列表已保存");
            }
        }
        catch (Exception ex)
        {
            ShowDialog("保存失败", ex.Message, allowCopy: true);
        }

        await RefreshAsync();
    }

    /// <summary>按钮的 Tag 或右键菜单选中的行。</summary>
    private static GameServer? ResolveRow(object sender)
    {
        if (sender is Button { Tag: GameServer fromButton })
        {
            return fromButton;
        }

        return null;
    }

    /// <summary>只做最宽松的校验：有内容、不含空格、不带 "minecraft://"。</summary>
    private static bool IsValidAddress(string address)
        => address.Length > 0 &&
           !address.Contains(' ') &&
           !address.Contains("://", StringComparison.Ordinal);

    private static (string Host, int Port) ParseAddress(string address)
    {
        var trimmed = address.Trim();

        // IPv6 形如 [::1]:25565
        if (trimmed.StartsWith('[') && trimmed.Contains("]:"))
        {
            var end = trimmed.IndexOf("]:", StringComparison.Ordinal);
            var host = trimmed[1..end];
            return int.TryParse(trimmed[(end + 2)..], out var v6Port)
                ? (host, v6Port)
                : (host, 25565);
        }

        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && trimmed.Count(c => c == ':') == 1)
        {
            var host = trimmed[..colon];
            return int.TryParse(trimmed[(colon + 1)..], out var port)
                ? (host, port)
                : (trimmed, 25565);
        }

        return (trimmed, 25565);
    }

    private void ShowDialog(string title, string message, bool allowCopy = false)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.ShowDialog(title, message, allowCopy);
        }
    }
}
