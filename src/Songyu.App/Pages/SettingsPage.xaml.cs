using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Songyu.Services;

namespace Songyu.App.Pages;

public partial class SettingsPage : UserControl
{
    private readonly AppServices _services;
    private bool _loaded;

    public SettingsPage(AppServices services)
    {
        InitializeComponent();
        _services = services;

        ProxyModeCombo.Items.Add(new ComboBoxItem { Content = "跟随系统代理（推荐）", Tag = ProxyMode.System });
        ProxyModeCombo.Items.Add(new ComboBoxItem { Content = "自定义代理", Tag = ProxyMode.Custom });
        ProxyModeCombo.Items.Add(new ComboBoxItem { Content = "不使用代理", Tag = ProxyMode.None });

        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        // 磁盘上的配置可能比内存新：用户可能直接编辑了 config.json，
        // 或者程序启动时那份文件还没写好。每次进页面重新同步一次，省得改完不生效。
        try
        {
            var path = Path.Combine(_services.Paths.Root, "config.json");
            var onDisk = AppConfig.Load(path);
            _services.Config.MicrosoftClientIds = onDisk.MicrosoftClientIds;
            _services.Config.PlayerName = onDisk.PlayerName;
        }
        catch
        {
            // 读失败就沿用内存里的，不影响界面
        }

        var config = _services.Config;

        PlayerNameBox.Text = config.PlayerName;
        ProxyBox.Text = config.HttpProxy ?? string.Empty;
        MirrorBox.Text = config.MirrorBaseUrl ?? string.Empty;
        ClientIdBox.Text = string.Join(Environment.NewLine, config.MicrosoftClientIds);
        ConcurrencyBox.Text = config.Concurrency.ToString();

        ProxyModeCombo.SelectedIndex = config.ProxyMode switch
        {
            ProxyMode.Custom => 1,
            ProxyMode.None => 2,
            _ => 0,
        };

        UpdateProxyBoxState();
        RefreshAccountStatus();

        if (_loaded)
        {
            return;
        }

        _loaded = true;
        ProxyModeCombo.SelectionChanged += (_, _) => UpdateProxyBoxState();
    }

    private void RefreshAccountStatus()
    {
        var account = _services.CreateAccountStore().Load();

        if (account is null || string.IsNullOrEmpty(account.Name))
        {
            AccountStatusText.Text = "未登录";
            LoginButton.Visibility = Visibility.Visible;
            LogoutButton.Visibility = Visibility.Collapsed;
            return;
        }

        AccountStatusText.Text = $"已登录：{account.Name}";
        LoginButton.Visibility = Visibility.Collapsed;
        LogoutButton.Visibility = Visibility.Visible;
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        LoginButton.IsEnabled = false;

        try
        {
            // 登录流程统一放在主窗口，首页头像和这里走同一套
            await main.LoginMicrosoftAsync();
            RefreshAccountStatus();
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        var confirmed = await main.ShowConfirmAsync(
            "退出登录",
            "退出后本地保存的登录凭据会被删除，之后只能用离线账号。",
            "退出登录",
            "取消");

        if (!confirmed)
        {
            return;
        }

        _services.CreateAccountStore().Clear();
        RefreshAccountStatus();
        main.RefreshAfterChange();
        main.ShowDialog("已退出", "本地保存的登录凭据已删除。");
    }

    private void UpdateProxyBoxState()
    {
        var custom = (ProxyModeCombo.SelectedItem as ComboBoxItem)?.Tag is ProxyMode.Custom;
        ProxyBox.IsEnabled = custom;
        ProxyBox.Opacity = custom ? 1 : 0.5;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        var config = _services.Config;

        var playerName = PlayerNameBox.Text.Trim();
        if (playerName.Length == 0)
        {
            main.ShowDialog("玩家名不能为空", "请填一个游戏内显示的名字。");
            return;
        }

        if (!int.TryParse(ConcurrencyBox.Text.Trim(), out var concurrency) || concurrency < 1 || concurrency > 32)
        {
            main.ShowDialog("并发数无效", "请填 1–32 之间的数字，建议 8。");
            return;
        }

        config.PlayerName = playerName;
        config.Concurrency = concurrency;
        config.ProxyMode = (ProxyModeCombo.SelectedItem as ComboBoxItem)?.Tag is ProxyMode mode
            ? mode
            : ProxyMode.System;
        config.HttpProxy = string.IsNullOrWhiteSpace(ProxyBox.Text) ? null : ProxyBox.Text.Trim();
        config.MirrorBaseUrl = string.IsNullOrWhiteSpace(MirrorBox.Text) ? null : MirrorBox.Text.Trim();
        config.MicrosoftClientIds = ClientIdBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        try
        {
            config.Save(Path.Combine(_services.Paths.Root, "config.json"));
            SaveHint.Text = "已保存。网络相关改动需要重启启动器才生效。";
            main.RefreshConfigDependentState();
        }
        catch (Exception ex)
        {
            main.ShowDialog("保存失败", ex.Message, allowCopy: true);
        }
    }

    private void OnClientIdHelpClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        main.ShowDialog(
            "怎么获取 Client ID",
            "微软已经下线了 Minecraft 官方的公开 id，登录必须用你自己注册的 Azure 应用。\n\n" +
            "1. 浏览器打开 https://portal.azure.com ，用微软账号登录\n" +
            "2. 搜索「应用注册」→ 点「新注册」\n" +
            "3. 名称随便填（比如 songyu-launcher）\n" +
            "   受支持的账户类型选「仅个人 Microsoft 账户」\n" +
            "   重定向 URI 留空即可\n" +
            "4. 注册完成后，页面上会显示「应用程序(客户端) ID」，复制它\n" +
            "5. 左侧「身份验证」→ 拉到最下面，把「允许公共客户端流」设为「是」，保存\n" +
            "6. 回到这里把 ID 填进上面那个框，点保存\n\n" +
            "这一步只做一次。免费，不需要绑卡。");
    }

    private void OnOpenDataClick(object sender, RoutedEventArgs e) => Open(_services.Paths.Root);

    private void OnOpenConfigClick(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(_services.Paths.Root, "config.json");
        if (!File.Exists(path))
        {
            _services.Config.Save(path);
        }

        Open(path);
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了，不影响主流程
        }
    }
}
