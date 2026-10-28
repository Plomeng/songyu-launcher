using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Songyu.Core.Minecraft;

/// <summary>
/// 库与参数的适用性规则。Mojang 的语义是：
/// 从 false 开始；按顺序遍历所有匹配当前环境的规则，用最后一条的 action 覆盖结果；
/// 一条规则都不匹配时保持 false；整个 rules 缺省视为允许。
/// </summary>
public sealed class Rule
{
    [JsonPropertyName("action")]
    public string Action { get; init; } = "allow";

    [JsonPropertyName("os")]
    public OsRule? Os { get; init; }

    [JsonPropertyName("features")]
    public Dictionary<string, bool>? Features { get; init; }
}

public sealed class OsRule
{
    /// <summary>windows / osx / linux。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>正则，匹配操作系统版本。</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>x86 / x86_64 / arm64。</summary>
    [JsonPropertyName("arch")]
    public string? Arch { get; init; }
}

/// <summary>求值时的运行环境快照。</summary>
public sealed class RuleContext
{
    public required string OsName { get; init; }

    /// <summary>x86 / x86_64 / arm64。</summary>
    public required string Arch { get; init; }

    public string OsVersion { get; init; } = string.Empty;

    /// <summary>可选的 features 开关，例如 has_custom_resolution。</summary>
    public IReadOnlyDictionary<string, bool> Features { get; init; } = new Dictionary<string, bool>();

    public static RuleContext Current()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : "linux";

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };

        return new RuleContext
        {
            OsName = os,
            Arch = arch,
            OsVersion = Environment.OSVersion.Version.ToString(),
        };
    }
}

public static class RuleEvaluator
{
    public static bool IsAllowed(IReadOnlyList<Rule>? rules, RuleContext context)
    {
        if (rules is null || rules.Count == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule, context))
            {
                continue;
            }

            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static bool Matches(Rule rule, RuleContext context)
    {
        if (rule.Os is { } os)
        {
            if (os.Name is not null && !string.Equals(os.Name, context.OsName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (os.Arch is not null && !string.Equals(os.Arch, context.Arch, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (os.Version is not null && !IsVersionMatch(os.Version, context.OsVersion))
            {
                return false;
            }
        }

        if (rule.Features is { Count: > 0 })
        {
            foreach (var (key, expected) in rule.Features)
            {
                var actual = context.Features.TryGetValue(key, out var value) && value;
                if (actual != expected)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsVersionMatch(string pattern, string version)
    {
        try
        {
            return Regex.IsMatch(version, pattern);
        }
        catch (ArgumentException)
        {
            // Mojang 曾经出现过畸形正则，退化为"不匹配"比抛异常好。
            return false;
        }
    }
}
