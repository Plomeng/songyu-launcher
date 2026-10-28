using System.IO;
using System.Text.Json;

namespace Songyu.Services;

/// <summary>
/// 已登录账号的本地存储，落在 <c>data/account.json</c>。
/// <para>
/// 只存刷新令牌和 Minecraft 令牌——刷新令牌是长期凭据，泄露等同于账号被盗，
/// 所以这个文件只放在用户自己的数据目录里，不上传任何地方。
/// </para>
/// </summary>
public sealed class AccountStore(MinecraftPaths paths)
{
    public sealed class AccountData
    {
        public string? RefreshToken { get; set; }

        public string? MinecraftAccessToken { get; set; }

        public string? Uuid { get; set; }

        public string? Name { get; set; }

        public DateTimeOffset? MinecraftExpiresAt { get; set; }

        /// <summary>皮肤贴图地址，用来在界面上画头像。</summary>
        public string? SkinUrl { get; set; }

        public string? CapeUrl { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private string FilePath => Path.Combine(paths.Root, "account.json");

    public AccountData? Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AccountData>(File.ReadAllText(FilePath), Options)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(AccountData data)
    {
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(data, Options));
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch
        {
            // 删不掉也不影响使用，下次登录会覆盖
        }
    }
}
