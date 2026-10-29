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
            _services.Config.MicrosoftClientId = onDisk.MicrosoftClientId;
        }
        catch
        {
            // 读失败就沿用内存里的，不影响界面
        }

        var config = _services.Config;

        ProxyBox.Text = config.HttpProxy ?? string.Empty;
        ClientIdBox.Text = config.MicrosoftClientId ?? string.Empty;
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

        var auth = _services.CreateMicrosoftAuth();
        var current = auth.IsConfigured && !string.IsNullOrWhiteSpace(account.RefreshToken) &&
            string.Equals(account.ClientId, auth.ClientId, StringComparison.OrdinalIgnoreCase);
        AccountStatusText.Text = current ? $"已登录：{account.Name}" :
            $"旧登录不能用于当前 Client ID：{account.Name}。请重新登录。";
        LoginButton.Visibility = current ? Visibility.Collapsed : Visibility.Visible;
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
            "退出后本地保存的登录凭据会被删除，重新登录前无法启动游戏。",
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

        var clientId = ClientIdBox.Text.Trim();
        if (clientId.Length > 0 && (!Guid.TryParse(clientId, out var parsedId) || parsedId == Guid.Empty))
        {
            main.ShowDialog("Client ID 无效", "请填写单个有效的应用程序 Client ID，或暂时留空。");
            return;
        }

        if (!int.TryParse(ConcurrencyBox.Text.Trim(), out var concurrency) || concurrency < 1 || concurrency > 32)
        {
            main.ShowDialog("并发数无效", "请填 1–32 之间的数字，建议 8。");
            return;
        }

        config.Concurrency = concurrency;
        config.ProxyMode = (ProxyModeCombo.SelectedItem as ComboBoxItem)?.Tag is ProxyMode mode
            ? mode
            : ProxyMode.System;
        config.HttpProxy = string.IsNullOrWhiteSpace(ProxyBox.Text) ? null : ProxyBox.Text.Trim();
        config.MicrosoftClientId = clientId.Length == 0 ? null : Guid.Parse(clientId).ToString("D");

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
            "开发者需注册本应用自己的 Azure 公共客户端，并向 Minecraft 服务申请 AppID allow list。\n\n" +
            "仅注册 Azure 应用不能开通 Minecraft API。审核获批后，在这里填写该应用的单个 Client ID。\n\n" +
            "不要填写其他应用的 ID。未获批前可管理版本和预览本地皮肤，但不能启动游戏。");
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
