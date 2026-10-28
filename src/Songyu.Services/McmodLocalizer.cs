using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Songyu.Services;

/// <summary>
/// 从 MC 百科（mcmod.cn）查 Mod 的中文名，带持久化缓存。
/// <para>
/// 设计目标是「列表和详情显示同一个名字」：所有地方都走 <see cref="LookupAsync"/>，
/// 结果按 slug 缓存在内存 + <c>data/mcmod-cache.json</c>，第二次查就是本地读取。
/// 列表里先显示英文名，后台并发补齐中文，补齐后自动刷新——所以不会卡界面，
/// 也不会出现列表与详情名字不一致。
/// </para>
/// <para>
/// 查询策略：先按英文全名搜，没命中再按 slug 搜。命中要求标题里含关键词，
/// 宁可查不到显示英文，也不要显示错的中文名。
/// </para>
/// </summary>
public sealed class McmodLocalizer
{
    private static readonly Regex ClassLinkRegex = new(
        @"href=""(?:https?:)?//(?:www\.)?mcmod\.cn/class/\d+\.html""[^>]*>(.*?)</a>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex LeadingBracketRegex = new(@"^\s*\[[^\]]*\]\s*", RegexOptions.Compiled);
    private static readonly Regex TrailingParenRegex = new(@"\s*\([^)]*\)\s*$", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    // mcmod 对高频请求会拒绝，并发压到 2 并给每次请求加间隔
    private readonly SemaphoreSlim _gate = new(2);
    private int _saveCountdown = 8;

    public McmodLocalizer(HttpClient http, MinecraftPaths paths)
    {
        _http = http;
        _cachePath = Path.Combine(paths.Root, "mcmod-cache.json");
        LoadCache();
    }

    public async Task<string?> LookupAsync(string englishTitle, string slug, CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(slug) ? englishTitle : slug;

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            string? result = null;

            try
            {
                // 只用完整英文名搜，不用 slug 兜底：
                // 用 "iris" 兜底会命中「Iris 修复 (IrisFixes)」这种同名不同物的 Mod，
                // 显示错的中文名比显示英文名更糟。查不到就交给内置映射。
                result = await SearchOnceAsync(englishTitle, cancellationToken);
            }
            catch
            {
                // 网络失败就当查不到，界面显示英文名
            }

            lock (_lock)
            {
                _cache[key] = result;
                _saveCountdown--;

                if (_saveCountdown <= 0)
                {
                    _saveCountdown = 8;
                    SaveCacheUnsafe();
                }
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> SearchOnceAsync(string term, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        var url = $"https://search.mcmod.cn/s?key={Uri.EscapeDataString(term)}&site=all";

        // 请求之间留点间隔，否则连续查一页会被判定为爬虫
        await Task.Delay(250, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseFirstChineseTitle(html, term);
    }

    /// <summary>
    /// 从搜索结果里挑出「英文名与搜索词完全相等」的那一条。
    /// <para>
    /// 不用「包含关键词」——mcmod 上同名变体很多，比如搜 <c>Curios API</c> 会同时出现
    /// <c>CuriosAPI：重织 (Curios Fabric)</c>（另一个 Mod），包含式匹配会抓错。
    /// </para>
    /// <para>
    /// 找到正主后：有中文就返回中文，<b>没中文就返回 null</b>——
    /// 说明这个 Mod 在 mcmod 上本来就没有中文名，不该继续往下找近似条目。
    /// </para>
    /// </summary>
    private static string? ParseFirstChineseTitle(string html, string searchTerm)
    {
        var target = Normalize(searchTerm);

        if (target.Length == 0)
        {
            return null;
        }

        foreach (Match match in ClassLinkRegex.Matches(html))
        {
            var raw = TagRegex.Replace(match.Groups[1].Value, string.Empty).Trim();
            var (english, chinese) = SplitTitle(raw);

            if (!string.Equals(Normalize(english), target, StringComparison.Ordinal))
            {
                continue;
            }

            // 正主找到了：有中文名才用，没有就是没有
            return chinese.Length > 0 ? chinese : null;
        }

        return null;
    }

    /// <summary>
    /// 把 <c>[JEI] JEI物品管理器 (Just Enough Items)</c> 拆成英文名和中文名。
    /// 末尾括号里通常是英文原名，主标题里通常是中文名。
    /// </summary>
    private static (string English, string Chinese) SplitTitle(string raw)
    {
        var text = LeadingBracketRegex.Replace(raw, string.Empty).Trim();

        var match = Regex.Match(text, @"^(.*?)\s*\(([^)]*)\)\s*$");
        if (match.Success)
        {
            var main = match.Groups[1].Value.Trim();
            var insideParen = match.Groups[2].Value.Trim();

            if (ModLocalization.ContainsChinese(insideParen))
            {
                return (main, insideParen);
            }

            if (ModLocalization.ContainsChinese(main))
            {
                return (insideParen, main);
            }

            return (main, string.Empty);
        }

        return ModLocalization.ContainsChinese(text)
            ? (string.Empty, text)
            : (text, string.Empty);
    }

    private static string Normalize(string text)
        => new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            var json = File.ReadAllText(_cachePath);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);

            if (loaded is null)
            {
                return;
            }

            foreach (var (key, value) in loaded)
            {
                _cache[key] = value;
            }
        }
        catch
        {
            // 缓存坏了就当没有
        }
    }

    private void SaveCacheUnsafe()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(_cache));
        }
        catch
        {
            // 写不进去就算了，不影响功能
        }
    }
}
