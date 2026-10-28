using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Songyu.Services;

/// <summary>
/// 代理模式。默认 <see cref="System"/>：跟随操作系统的代理设置，
/// 用户开了系统代理就走，没开就直连——不硬编码任何端口。
/// </summary>
public enum ProxyMode
{
    /// <summary>强制直连，忽略系统代理（适合确定网络通畅、但系统代理反而碍事的情况）。</summary>
    None,

    /// <summary>跟随系统代理设置（默认）。</summary>
    System,

    /// <summary>使用 <see cref="AppConfig.HttpProxy"/> 指定的地址。</summary>
    Custom,
}

/// <summary>
/// 启动器配置。
/// <para>
/// 设计取舍：不把任何具体代理地址写进默认值。三态设计（直连 / 跟随系统 / 自定义）
/// 是这类工具的通行做法，探测与回退逻辑均为本项目自行实现。
/// </para>
/// </summary>
public sealed class AppConfig
{
    /// <summary>代理模式，默认跟随系统。</summary>
    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;

    /// <summary>仅在 <see cref="ProxyMode.Custom"/> 下生效，例如 http://127.0.0.1:7897。</summary>
    public string? HttpProxy { get; set; }

    /// <summary>
    /// 镜像根地址。默认用 BMCLAPI——官方 CDN 在国内直连经常断，镜像能让「能不能玩」这件事
    /// 从碰运气变成稳定。填 null 或空串则只用官方源，官方永远是最后兜底。
    /// <para>
    /// 路径约定（与 BMCLAPI 一致）：<c>{root}/libraries/…</c>、<c>{root}/assets/…</c>、
    /// <c>{root}/version/{id}/client</c>。
    /// </para>
    /// </summary>
    public string? MirrorBaseUrl { get; set; } = "https://bmclapi2.bangbang93.com";

    /// <summary>并发下载数。官方 CDN 开太大容易触发限流，实测 8 稳定。</summary>
    public int Concurrency { get; set; } = 8;

    /// <summary>单请求超时秒数。设太长会让坏连接卡住整个队列。</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// 离线模式的玩家名。没有接登录系统时，用它生成离线 UUID 和游戏内名字。
    /// 正版登录接入后这里只作为未登录时的默认值。
    /// </summary>
    public string PlayerName { get; set; } = "Player";

    /// <summary>离线账号在启动器内预览的皮肤文件名，位于本地 skins 目录。</summary>
    public string? OfflineSkinFileName { get; set; }

    /// <summary>离线皮肤在界面上显示的原始名称。</summary>
    public string? OfflineSkinDisplayName { get; set; }

    /// <summary>
    /// 微软登录用的 client id 池。按顺序尝试，前一个失效（unauthorized_client /
    /// invalid_client）自动切下一个，全部失效才报错。
    /// <para>
    /// 之所以是列表而不是单个值：任何 client id 都可能哪天被停用，
    /// 配了备用就不用等启动器更新。
    /// </para>
    /// </summary>
    public List<string> MicrosoftClientIds { get; set; } = [];

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), FileOptions)
                       ?? new AppConfig();
            }
        }
        catch
        {
            // 配置损坏时退回默认值，不让启动失败。
        }

        return new AppConfig();
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, FileOptions));
    }

    public HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Math.Max(2, Concurrency),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = DecompressionMethods.None,
        };

        ApplyProxy(handler);

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Max(15, TimeoutSeconds)) };
    }

    private void ApplyProxy(SocketsHttpHandler handler)
    {
        switch (ProxyMode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;

            case ProxyMode.Custom:
                // 用户填了自定义地址，但那个端口可能根本没程序在听（比如 Clash 没开）。
                // 死代理比直连更糟：直连至少还能通没被墙的站点。
                if (!string.IsNullOrWhiteSpace(HttpProxy) &&
                    Uri.TryCreate(HttpProxy, UriKind.Absolute, out var proxyUri) &&
                    IsProxyAlive(proxyUri))
                {
                    handler.Proxy = new WebProxy(proxyUri) { BypassProxyOnLocal = true };
                    handler.UseProxy = true;
                }
                else
                {
                    handler.UseProxy = false;
                    ProxyFallbackReason = string.IsNullOrWhiteSpace(HttpProxy)
                        ? "自定义代理地址为空，已改为直连"
                        : $"自定义代理 {HttpProxy} 连不上，已改为直连";
                }
                break;

            default: // System：跟随系统代理，但系统代理也可能是死的
                var systemProxy = ReadSystemProxyUri();

                if (systemProxy is null)
                {
                    // 系统没配代理，正常直连
                    handler.UseProxy = false;
                }
                else if (IsProxyAlive(systemProxy))
                {
                    handler.Proxy = new WebProxy(systemProxy) { BypassProxyOnLocal = true };
                    handler.UseProxy = true;
                }
                else
                {
                    handler.UseProxy = false;
                    ProxyFallbackReason =
                        $"系统代理 {systemProxy.Host}:{systemProxy.Port} 连不上，已改为直连";
                }
                break;
        }
    }

    /// <summary>
    /// 本次探测的降级说明。代理不可用而退回直连时非空，界面可以据此提示用户，
    /// 而不是让人对着「连不上」猜是不是 id 坏了。
    /// </summary>
    [JsonIgnore]
    public string? ProxyFallbackReason { get; private set; }

    /// <summary>读 Windows 的「Internet 选项 → 连接 → 局域网设置」里的代理配置。</summary>
    private static Uri? ReadSystemProxyUri()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");

            if (key?.GetValue("ProxyEnable") is not int enabled || enabled == 0)
            {
                return null;
            }

            var raw = key.GetValue("ProxyServer") as string;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            // 可能是 "127.0.0.1:7897"，也可能是 "http=127.0.0.1:7897;https=127.0.0.1:7897"
            var candidate = raw
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Contains('=') ? part.Split('=', 2)[1] : part)
                .FirstOrDefault(part => !string.IsNullOrWhiteSpace(part));

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            if (!candidate.Contains(':'))
            {
                candidate += ":80";
            }

            return Uri.TryCreate($"http://{candidate}", UriKind.Absolute, out var uri) ? uri : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 探一下代理端口有没有人在听。300ms 连不上就当它没开——
    /// 这比让用户面对「请求超时 20 秒后才失败」友好得多。
    /// </summary>
    private static bool IsProxyAlive(Uri proxy)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(proxy.Host, proxy.Port);
            return connect.Wait(TimeSpan.FromMilliseconds(300)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>给界面显示的一句人话描述。</summary>
    public string DescribeProxy() => ProxyMode switch
    {
        ProxyMode.None => "直连（已忽略系统代理）",
        ProxyMode.Custom => string.IsNullOrWhiteSpace(HttpProxy) ? "自定义代理（地址为空，已退回系统）" : $"自定义代理 {HttpProxy}",
        _ => "跟随系统代理",
    };

    /// <summary>给界面显示的一句人话描述。</summary>
    public string DescribeSource() =>
        string.IsNullOrWhiteSpace(MirrorBaseUrl) ? "官方源" : $"镜像优先 {MirrorBaseUrl}（官方兜底）";
}
