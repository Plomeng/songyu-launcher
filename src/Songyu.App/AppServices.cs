using System.IO;
using System.Net.Http;
using Songyu.Services;

namespace Songyu.App;

/// <summary>
/// 页面之间共享的服务容器。
/// 页面不自己造 HttpClient，也不各自读配置——统一从这里拿，避免出现两套网络设置。
/// </summary>
public sealed class AppServices
{
    public required MinecraftPaths Paths { get; init; }

    public required AppConfig Config { get; init; }

    public required HttpClient Http { get; init; }

    public required IJavaService JavaService { get; init; }

    public required LocalVersionService LocalVersions { get; init; }

    /// <summary>实例库：版本 ≠ 实例，游戏目录按实例隔离。</summary>
    public InstanceStore Instances => _instances ??= new InstanceStore(Paths);

    private InstanceStore? _instances;

    /// <summary>本地内容索引（data/index.db）：文件 sha1、来源、jar 元数据。</summary>
    public ContentIndex Content => _content ??= new ContentIndex(Paths);

    private ContentIndex? _content;

    public IReadOnlyList<JavaInstallation> JavaInstallations { get; set; } = [];

    public JavaInstallation? BestJava => JavaInstallations.FirstOrDefault();

    public MinecraftInstaller CreateInstaller() => new(Http, Paths, Config);

    public LaunchService CreateLauncher() => new(Paths);

    /// <summary>Fabric / Quilt 走 meta 服务的加载器。</summary>
    public MetaLoaderInstaller? CreateMetaLoader(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => new MetaLoaderInstaller(
            Http, Paths, "https://meta.fabricmc.net/v2", "Fabric"),
        LoaderKind.Quilt => new MetaLoaderInstaller(
            Http, Paths, "https://meta.quiltmc.org/v3", "Quilt"),
        _ => null,
    };

    /// <summary>Forge / NeoForge 需要跑官方安装器。</summary>
    public ForgeInstaller CreateForgeInstaller() => new(Http, Paths, Config);

    public NeoForgeInstaller CreateNeoForgeInstaller() => new(Http, Paths, Config);

    public ModrinthClient CreateModrinth() => new(Http);

    public ModInstaller CreateModInstaller() => new(Http, Paths, new ModrinthClient(Http));

    /// <summary>Mod 更新检查与应用。</summary>
    public ModUpdater CreateModUpdater() => new(Http, Paths, Content, CreateModrinth());

    /// <summary>Mod 冲突诊断（元数据层面）。</summary>
    public ConflictDiagnostics CreateDiagnostics() => new(Paths, Content, CreateModrinth());

    public McmodLocalizer CreateMcmodLocalizer() => new(Http, Paths);

    public ModpackInstaller CreateModpackInstaller() => new(Http, Paths, Config, Content);

    public MicrosoftAuth CreateMicrosoftAuth() => new(Http, Config.MicrosoftClientIds);

    public AccountStore CreateAccountStore() => new(Paths);

    public string? SelectedOfflineSkinPath
    {
        get
        {
            var name = Config.OfflineSkinFileName;
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name)) { return null; }
            var path = Path.Combine(Paths.Root, "skins", name);
            return File.Exists(path) ? path : null;
        }
    }

    public async Task<string> SelectOfflineSkinAsync(SkinInfo skin)
    {
        var skins = new SkinService(Paths, Http);
        var source = skin.Source;
        string path;
        if (File.Exists(source) &&
            string.Equals(Path.GetDirectoryName(Path.GetFullPath(source)), skins.SkinsDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            path = source;
        }
        else
        {
            var name = File.Exists(source) ? Path.GetFileNameWithoutExtension(source) : "account-skin";
            path = await skins.SaveAsync(skin.Png, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        }

        Config.OfflineSkinFileName = Path.GetFileName(path);
        Config.OfflineSkinDisplayName = File.Exists(source)
            ? Path.GetFileNameWithoutExtension(source)
            : "账号皮肤";
        Config.Save(Path.Combine(Paths.Root, "config.json"));
        return path;
    }

    public static bool IsSupported(LoaderKind kind)
        => kind is LoaderKind.Vanilla or LoaderKind.Fabric or LoaderKind.Quilt
            or LoaderKind.Forge or LoaderKind.NeoForge;

    /// <summary>加载器版本 ID 的前缀，用来在已安装列表里匹配。</summary>
    public static string? LoaderPrefix(LoaderKind kind) => kind switch
    {
        LoaderKind.Fabric => "fabric-loader-",
        LoaderKind.Quilt => "quilt-loader-",
        _ => null,
    };
}
