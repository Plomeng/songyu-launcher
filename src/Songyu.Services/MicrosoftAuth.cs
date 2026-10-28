using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Songyu.Services;

public sealed record DeviceCodeInfo(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int IntervalSeconds,
    int ExpiresInSeconds);

public sealed record MicrosoftToken(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt);

public sealed record MinecraftProfile(
    string Uuid,
    string Name,
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string? SkinUrl = null,
    string? CapeUrl = null);

/// <summary>
/// 微软登录的完整链路。
/// <para>
/// OAuth 拿到的只是 Microsoft 令牌，要进 Minecraft 还得走三段：
/// Xbox Live 认证 → XSTS 授权 → Minecraft 登录，最后用 profile 拿 UUID 和名字。
/// 任何一步失败都要单独报错，否则用户只会看到"登录失败"四个字。
/// </para>
/// <para>
/// 用的是设备码（device code）流程：适合桌面程序，不需要内嵌浏览器或处理回调端口。
/// </para>
/// <para>
/// client id 支持一组而不是一个：某个被停用（unauthorized_client / invalid_client）时
/// 自动换下一个，全部失效才报错。轮询必须用申请设备码时的同一个 id，否则微软会拒绝。
/// </para>
/// </summary>
public sealed class MicrosoftAuth
{
    private const string Scope = "XboxLive.signin offline_access";

    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    private const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    /// <summary>
    /// Xbox Live 的两个端点对 JSON 属性名大小写敏感，只认 PascalCase 的
    /// Properties / AuthMethod / RpsTicket / RelyingParty / TokenType。
    /// <para>
    /// 坑：HttpClient 的 PostAsJsonAsync 默认按 Web 默认值序列化（属性名转小驼峰），
    /// 发出去变成 properties / rpsTicket，Xbox 的边缘层直接回 HTTP 400 且响应体为空、
    /// X-XblCorrelationId 全零，完全不给原因——看起来像 client id 没权限，实际是这个。
    /// 所以这里必须显式关掉命名策略。
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions XboxJsonOptions =
        new() { PropertyNamingPolicy = null };

    /// <summary>Minecraft 服务端要的相反：小驼峰的 identityToken。</summary>
    private static readonly JsonSerializerOptions McJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string[] _clientIds;

    private int _index;
    private string _active = string.Empty;

    public MicrosoftAuth(HttpClient http, IEnumerable<string> clientIds)
    {
        _http = http;
        _clientIds = (clientIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>池里至少有一个 id 才能登录。</summary>
    public bool IsConfigured => _clientIds.Length > 0;

    /// <summary>当前在用的是池里第几个（从 1 开始），给界面显示用。</summary>
    public int CurrentIndex => _clientIds.Length == 0 ? 0 : _index + 1;

    public int Total => _clientIds.Length;

    /// <summary>
    /// 已经成功申请到设备码的那个 id。
    /// 轮询令牌必须用同一个 id，否则微软会报 device_code 不匹配。
    /// </summary>
    public string ActiveClientId => _active;

    /// <summary>
    /// 第一步：申请设备码。
    /// <para>
    /// 按池顺序尝试：某个 id 返回 unauthorized_client / invalid_client（被停用、写错、租户不对）
    /// 就自动切下一个，直到有人给出设备码。全部失败才抛错，并带上最后一个原因。
    /// </para>
    /// </summary>
    public async Task<DeviceCodeInfo> StartDeviceCodeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("还没有配置任何 client id。");
        }

        Exception? last = null;
        var attempted = 0;

        while (attempted < _clientIds.Length)
        {
            var candidate = _clientIds[_index];

            try
            {
                var info = await RequestDeviceCodeAsync(candidate, cancellationToken);
                _active = candidate;
                return info;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 连不上服务器时不要换 id：换再多也一样连不上，
                // 而且会让人以为是 id 的问题，实际该查的是网络和代理。
                if (ex is HttpRequestException or TaskCanceledException)
                {
                    throw new InvalidOperationException(
                        $"连不上微软登录服务器：{ex.Message}\n\n" +
                        "这是网络或代理问题，跟 client id 无关。检查一下：\n" +
                        "· 代理软件是否在运行\n" +
                        "· 设置里的代理方式是否和实际情况一致（跟随系统 / 自定义 / 不使用）",
                        ex);
                }

                last = ex;
                attempted++;
                _index = (_index + 1) % _clientIds.Length;
            }
        }

        throw new InvalidOperationException(
            $"{_clientIds.Length} 个 client id 全部失效。最后一个错误：{last?.Message}", last);
    }

    private async Task<DeviceCodeInfo> RequestDeviceCodeAsync(string clientId, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = Scope,
        });

        using var response = await _http.PostAsync(DeviceCodeUrl, content, cancellationToken);
        var json = await ReadOrThrowAsync(response, "申请设备码", cancellationToken);

        return new DeviceCodeInfo(
            json.GetProperty("device_code").GetString() ?? string.Empty,
            json.GetProperty("user_code").GetString() ?? string.Empty,
            json.TryGetProperty("verification_uri", out var uri) ? uri.GetString() ?? string.Empty : string.Empty,
            json.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5,
            json.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 900);
    }

    /// <summary>
    /// 第二步：轮询令牌。用户没登录完时服务端返回 authorization_pending，
    /// 这里会一直等到成功、过期或被取消。
    /// </summary>
    public async Task<MicrosoftToken> PollForTokenAsync(
        DeviceCodeInfo deviceCode,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresInSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _active,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = deviceCode.DeviceCode,
            });

            using var response = await _http.PostAsync(TokenUrl, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(body);
                return ReadToken(document.RootElement);
            }

            using var errorDocument = JsonDocument.Parse(body);
            var error = errorDocument.RootElement.TryGetProperty("error", out var e)
                ? e.GetString()
                : null;

            // 用户还没在网页上点"允许"，继续等
            if (error is "authorization_pending" or "slow_down")
            {
                await Task.Delay(TimeSpan.FromSeconds(deviceCode.IntervalSeconds), cancellationToken);
                continue;
            }

            if (error == "expired_token")
            {
                throw new InvalidOperationException("登录码已过期，请重新发起登录。");
            }

            if (error == "authorization_declined")
            {
                throw new InvalidOperationException("你在网页上拒绝了授权。");
            }

            throw new InvalidOperationException($"登录失败：{error ?? body}");
        }

        throw new InvalidOperationException("登录超时，请重试。");
    }

    public async Task<MicrosoftToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var clientId = string.IsNullOrEmpty(_active) ? _clientIds.FirstOrDefault() ?? string.Empty : _active;

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope,
        });

        using var response = await _http.PostAsync(TokenUrl, content, cancellationToken);
        var json = await ReadOrThrowAsync(response, "刷新令牌", cancellationToken);

        return ReadToken(json);
    }

    /// <summary>
    /// 第三步：Microsoft 令牌换 Minecraft 令牌。
    /// 链路上任何一段失败都要带上具体原因（尤其是 XSTS 的常见错误码）。
    /// </summary>
    public async Task<MinecraftProfile> LoginToMinecraftAsync(
        string microsoftAccessToken,
        CancellationToken cancellationToken = default)
    {
        // 微软令牌本身是 JWT，把它携带的 scope / audience 解出来记一笔。
        // XBL 返回 400 且响应体为空时，靠这个判断问题出在令牌权限还是令牌本身。
        LogDiagnostic($"MS token {DescribeJwt(microsoftAccessToken)}");

        var (xblToken, userHash) = await XboxAuthenticateAsync(microsoftAccessToken, cancellationToken);
        var (xstsToken, xstsHash) = await XstsAuthorizeAsync(xblToken, cancellationToken);

        var mcToken = await MinecraftLoginAsync(xstsHash, xstsToken, cancellationToken);
        return await GetProfileAsync(mcToken, cancellationToken);
    }

    /// <summary>解出 JWT 的 scope / audience / tenant，用于诊断。不输出任何身份信息。</summary>
    private static string DescribeJwt(string jwt)    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
            {
                return $"不是 JWT（长度 {jwt.Length}）";
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var scope = root.TryGetProperty("scp", out var s) ? s.GetString() : "(无 scp)";
            var audience = root.TryGetProperty("aud", out var a) ? a.GetString() : "(无 aud)";
            var tenant = root.TryGetProperty("tid", out var t) ? t.GetString() : "(无 tid)";

            return $"scope=[{scope}] aud=[{audience}] tid=[{tenant}]";
        }
        catch (Exception ex)
        {
            return $"解析失败：{ex.Message}（长度 {jwt.Length}）";
        }
    }

    private async Task<(string Token, string UserHash)> XboxAuthenticateAsync(
        string microsoftToken,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = $"d={microsoftToken}",
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        };

        using var response = await _http.PostAsJsonAsync(XblUrl, payload, XboxJsonOptions, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // 400 这类错误微软会在响应体里写原因，必须原样带出来，
            // 否则只剩一句"HTTP 400"根本没法定位。
            throw new InvalidOperationException(
                $"Xbox Live 认证失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n\n" +
                $"微软的回应：\n{Summarize(body)}");
        }

        using var document = JsonDocument.Parse(body);
        var json = document.RootElement;

        var token = json.GetProperty("Token").GetString() ?? string.Empty;
        var hash = ReadUserHash(json);

        return (token, hash);
    }

    /// <summary>把诊断信息落到 data 目录，方便排查登录链路问题。不含令牌原文。</summary>
    private static void LogDiagnostic(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Songyu");

            Directory.CreateDirectory(directory);

            File.AppendAllText(
                Path.Combine(directory, "auth-diagnostic.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 写不进日志不影响登录
        }
    }

    /// <summary>把响应体压成便于阅读的一段，太长就截断。</summary>
    private static string Summarize(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(响应体为空)";
        }

        var trimmed = body.Trim();

        // 是 JSON 就缩进一下，读起来清楚
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            trimmed = JsonSerializer.Serialize(
                doc.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            // 不是 JSON 就原样显示
        }

        return trimmed.Length <= 800 ? trimmed : trimmed[..800] + "…";
    }

    private async Task<(string Token, string UserHash)> XstsAuthorizeAsync(
        string xblToken,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken },
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        };

        using var response = await _http.PostAsJsonAsync(XstsUrl, payload, XboxJsonOptions, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // XSTS 的 401 有明确的 XErr 码，各自含义不同，直接告诉用户
            if (body.Contains("2148916233", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("这个微软账号还没有 Xbox 档案，请先在 xbox.com 登录一次创建。");
            }

            if (body.Contains("2148916238", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("这个账号是未成年人账号，需要家长同意后才能使用。");
            }

            if (body.Contains("2148916235", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("该地区不支持 Xbox Live。");
            }

            throw new InvalidOperationException($"XSTS 授权失败：HTTP {(int)response.StatusCode} {body}");
        }

        var json = await ReadOrThrowAsync(response, "XSTS 授权", cancellationToken);
        var token = json.GetProperty("Token").GetString() ?? string.Empty;
        var hash = ReadUserHash(json);

        return (token, hash);
    }

    private async Task<string> MinecraftLoginAsync(
        string userHash,
        string xstsToken,
        CancellationToken cancellationToken)
    {
        var payload = new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" };

        using var response = await _http.PostAsJsonAsync(McLoginUrl, payload, McJsonOptions, cancellationToken);
        var json = await ReadOrThrowAsync(response, "Minecraft 登录", cancellationToken);

        return json.GetProperty("access_token").GetString() ?? string.Empty;
    }

    private async Task<MinecraftProfile> GetProfileAsync(string mcToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, McProfileUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {mcToken}");

        using var response = await _http.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("这个账号没有购买 Minecraft（或没有创建过档案）。");
        }

        var json = await ReadOrThrowAsync(response, "读取 Minecraft 档案", cancellationToken);

        return new MinecraftProfile(
            json.GetProperty("id").GetString() ?? string.Empty,
            json.GetProperty("name").GetString() ?? string.Empty,
            mcToken,
            DateTimeOffset.UtcNow.AddHours(24),
            ReadTextureUrl(json, "skins"),
            ReadTextureUrl(json, "capes"));
    }

    /// <summary>从档案里取皮肤/披风贴图地址（只取 state=ACTIVE 的那条）。</summary>
    private static string? ReadTextureUrl(JsonElement profile, string property)
    {
        if (!profile.TryGetProperty(property, out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            var state = item.TryGetProperty("state", out var s) ? s.GetString() : null;
            if (state is not null && !string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (item.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    private static string ReadUserHash(JsonElement json)
    {
        if (json.TryGetProperty("DisplayClaims", out var claims) &&
            claims.TryGetProperty("xui", out var xui) &&
            xui.GetArrayLength() > 0 &&
            xui[0].TryGetProperty("uhs", out var uhs))
        {
            return uhs.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException("认证响应里缺少 user hash。");
    }

    private static MicrosoftToken ReadToken(JsonElement json)
    {
        var access = json.GetProperty("access_token").GetString() ?? string.Empty;
        var refresh = json.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? string.Empty : string.Empty;
        var expiresIn = json.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;

        return new MicrosoftToken(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    private static async Task<JsonElement> ReadOrThrowAsync(
        HttpResponseMessage response,
        string step,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{step}失败：HTTP {(int)response.StatusCode} {body}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
