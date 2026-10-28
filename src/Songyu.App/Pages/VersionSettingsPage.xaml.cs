using System.IO;
using System.Windows;
using System.Windows.Controls;
using Songyu.Services;

namespace Songyu.App.Pages;

public partial class VersionSettingsPage : UserControl
{
    private readonly AppServices _services;
    private string _versionId = string.Empty;
    private string _instanceId = string.Empty;
    private InstanceInfo? _instance;
    private InstanceSettings _settings = new();

    public VersionSettingsPage(AppServices services)
    {
        InitializeComponent();
        _services = services;
    }

    /// <summary>
    /// 按<b>实例</b>打开设置页。参数是实例 ID，不是版本 ID——
    /// 同一个版本可能开了多个实例，设置必须落在用户点中的那一个上。
    /// （版本只是共享的程序文件，内存 / Java 这些本来就是每个实例各管各的。）
    /// </summary>
    public void Load(string instanceId)
    {
        var instance = _services.Instances.Get(instanceId);

        // 兜底：传进来的其实是版本 ID（老调用点）时按版本找默认实例
        instance ??= _services.Instances.EnsureForVersion(instanceId);

        _instance = instance;
        _instanceId = instance.Id;
        _versionId = instance.VersionId;
        _settings = instance.Settings;

        TitleText.Text = string.Equals(instance.Name, _versionId, StringComparison.Ordinal)
            ? _versionId
            : instance.Name;
        SubtitleText.Text = $"{_versionId}   ·   {_services.Instances.GameDirectory(_instanceId)}";

        MaxMemoryBox.Text = _settings.MaxMemoryMb.ToString();
        MinMemoryBox.Text = _settings.MinMemoryMb.ToString();
        WidthBox.Text = _settings.WindowWidth.ToString();
        HeightBox.Text = _settings.WindowHeight.ToString();
        FullscreenBox.IsChecked = _settings.Fullscreen;
        JvmArgsBox.Text = _settings.ExtraJvmArgs;

        FillJavaCombo();
    }

    private void FillJavaCombo()
    {
        JavaCombo.Items.Clear();
        JavaCombo.Items.Add(new ComboBoxItem { Content = "自动（使用检测到的最高版本）", Tag = null });

        var selectedIndex = 0;
        var index = 1;

        foreach (var java in _services.JavaInstallations)
        {
            JavaCombo.Items.Add(new ComboBoxItem
            {
                Content = $"Java {java.MajorVersion} · {java.Brand}",
                Tag = java.JavaExePath,
            });

            if (!string.IsNullOrEmpty(_settings.JavaPath) &&
                string.Equals(java.JavaExePath, _settings.JavaPath, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = index;
            }

            index++;
        }

        JavaCombo.SelectedIndex = selectedIndex;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        if (!int.TryParse(MaxMemoryBox.Text.Trim(), out var max) || max < 512)
        {
            main.ShowDialog("内存值无效", "最大内存请填 512 以上的整数（单位 MB）。");
            return;
        }

        _settings.MaxMemoryMb = max;
        _settings.MinMemoryMb = int.TryParse(MinMemoryBox.Text.Trim(), out var min) ? Math.Max(0, min) : 0;
        _settings.WindowWidth = int.TryParse(WidthBox.Text.Trim(), out var w) ? Math.Max(320, w) : 1280;
        _settings.WindowHeight = int.TryParse(HeightBox.Text.Trim(), out var h) ? Math.Max(240, h) : 720;
        _settings.Fullscreen = FullscreenBox.IsChecked == true;
        _settings.ExtraJvmArgs = JvmArgsBox.Text.Trim();
        _settings.JavaPath = (JavaCombo.SelectedItem as ComboBoxItem)?.Tag as string;

        // 设置存在实例里（不再写到版本目录）
        if (_instance is null)
        {
            _instance = _services.Instances.EnsureForVersion(_versionId);
            _instanceId = _instance.Id;
        }

        _instance.Settings = _settings;
        _services.Instances.Save(_instance);

        main.ShowDialog("已保存", $"实例「{_instance.Name}」的启动设置已更新。");
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow main)
        {
            return;
        }

        var confirmed = await main.ShowConfirmAsync(
            "删除版本文件",
            $"确定删除版本 {_versionId} 的程序文件？\n\n" +
            "只会删 versions 下这个版本的目录（jar / json / natives）。\n" +
            $"实例目录（{_instanceId}）里的存档、Mod 和设置都不动。\n\n" +
            "删完这个版本就启动不了，要用得重新下载。",
            "删除",
            "取消");

        if (!confirmed)
        {
            return;
        }

        try
        {
            var directory = _services.Paths.VersionDir(_versionId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            main.RefreshAfterChange();
            main.ShowDialog("已删除", $"版本 {_versionId} 已移除。");
        }
        catch (Exception ex)
        {
            main.ShowDialog("删除失败", ex.Message, allowCopy: true);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
        {
            main.NavigateHome();
        }
    }
}
