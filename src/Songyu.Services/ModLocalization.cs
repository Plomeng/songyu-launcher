namespace Songyu.Services;

/// <summary>
/// 文本工具。
/// <para>
/// 这里原本有一张手写的「常见 Mod 中文名」映射表，已删除——
/// 那些名字是估的，和真实译名对不上，比显示英文更误导。
/// 现在 Mod 中文名只有一个来源：mcmod.cn 的页面标题，查不到就显示原名。
/// </para>
/// </summary>
public static class ModLocalization
{
    /// <summary>文本里是否含中日韩汉字。</summary>
    public static bool ContainsChinese(string? text)
        => !string.IsNullOrEmpty(text) && text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
}
