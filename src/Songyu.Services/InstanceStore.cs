using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Songyu.Services;

/// <summary>
/// 一个实例：稳定的目录 ID + 用户起的显示名 + 指向的版本。
/// <para>
/// 和"版本"分开的理由：同一个版本（比如 1.20.1 加 Fabric）可以开好几个实例，
/// 各自有自己的 mods / saves / config，互不干扰。版本只是实例引用的"程序文件"，
/// 那些大文件（jar、libraries、assets）继续共享，不重复下载。
/// </para>
/// </summary>
public sealed class InstanceInfo
{
    /// <summary>目录名，创建后不再变（改名只改 Name）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名，用户可随便改，允许重名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>引用的版本 ID（versions/ 下的目录名）。</summary>
    public string VersionId { get; set; } = string.Empty;

    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastPlayed { get; set; }

    /// <summary>启动设置直接存在实例里，不再散落在版本目录。</summary>
    public InstanceSettings Settings { get; set; } = new();
}

/// <summary>
/// 实例的读写与迁移。
/// <para>
/// 布局：<c>instances/&lt;id&gt;/instance.json</c>，同目录下是 mods / saves / config 等游戏文件。
/// 老数据（版本目录即实例）在 <see cref="EnsureMigrated"/> 里自动补成正式实例，
/// 所以升级后原来的版本照样能看见、能启动。
/// </para>
/// </summary>
public sealed class InstanceStore(MinecraftPaths paths)
{
    private const string JsonFileName = "instance.json";

    private static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly char[] InvalidIdChars =
        Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).Distinct().ToArray();

    /// <summary>列出全部实例，最近玩过的在前。</summary>
    public IReadOnlyList<InstanceInfo> List()
    {
        var result = new List<InstanceInfo>();

        if (!Directory.Exists(paths.InstancesDir))
        {
            return result;
        }

        foreach (var directory in Directory.GetDirectories(paths.InstancesDir))
        {
            var id = Path.GetFileName(directory);

            // 点开头的是内部目录（回收站之类），不当实例
            if (id.StartsWith('.'))
            {
                continue;
            }

            var info = Load(id);
            if (info is not null)
            {
                result.Add(info);
            }
        }

        return result
            .OrderByDescending(i => i.LastPlayed ?? i.Created)
            .ToList();
    }

    public InstanceInfo? Load(string id)
    {
        var info = Read(id);
        if (info is not null)
        {
            return info;
        }

        // 目录在但没有 instance.json：可能是迁移前就存在的 mods 目录，补一个
        if (Directory.Exists(paths.InstanceDir(id)))
        {
            return LegacyInstance(id);
        }

        return null;
    }

    public InstanceInfo? Get(string id) => Load(id);

    public void Save(InstanceInfo info)
    {
        info.Id = string.IsNullOrWhiteSpace(info.Id) ? MakeUniqueId(info.Name) : info.Id;

        var directory = paths.InstanceDir(info.Id);
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, JsonFileName),
            JsonSerializer.Serialize(info, FileOptions));
    }

    /// <summary>创建实例（不下载任何东西，版本得事先装好）。</summary>
    public InstanceInfo Create(string name, string versionId)
    {
        var info = new InstanceInfo
        {
            Id = MakeUniqueId(name),
            Name = string.IsNullOrWhiteSpace(name) ? versionId : name.Trim(),
            VersionId = versionId,
        };

        Save(info);
        Directory.CreateDirectory(paths.InstanceModsDir(info.Id));
        return info;
    }

    /// <summary>
    /// 按版本取"默认实例"，没有就建一个。
    /// 老接口（只认版本）走这里，保证行为不变但底层已经是实例模型。
    /// </summary>
    public InstanceInfo EnsureForVersion(string versionId)
    {
        var existing = List().FirstOrDefault(i =>
            string.Equals(i.VersionId, versionId, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            // 老布局（目录名即版本）识别出来的只是内存里的虚拟实例，
            // 补写一次文件，之后改名 / 改设置才落得了盘。
            if (!File.Exists(paths.InstanceJsonPath(existing.Id)))
            {
                Save(existing);
            }

            return existing;
        }

        // 旧版把游戏目录放在 instances/<版本id>/，优先沿用这个目录名，
        // 用户已有的 mods / saves 才不会丢。
        var id = Directory.Exists(Path.Combine(paths.InstancesDir, versionId))
            ? versionId
            : MakeUniqueId(versionId);

        var info = new InstanceInfo { Id = id, Name = versionId, VersionId = versionId };
        Save(info);
        Directory.CreateDirectory(paths.InstanceModsDir(id));

        // 老设置（存在版本目录里的 instance.json）搬过来
        var legacy = InstanceSettings.Load(paths, versionId);
        if (!SettingsAreDefault(legacy))
        {
            info.Settings = legacy;
            Save(info);
        }

        return info;
    }

    /// <summary>
    /// 把"版本即实例"的老数据补成正式实例文件。
    /// 幂等，启动器每次启动调一次即可，代价只有几十次 File.Exists。
    /// </summary>
    public int EnsureMigrated()
    {
        if (!Directory.Exists(paths.VersionsDir))
        {
            return 0;
        }

        var created = 0;

        foreach (var directory in Directory.GetDirectories(paths.VersionsDir))
        {
            var versionId = Path.GetFileName(directory);

            if (!File.Exists(paths.VersionJsonPath(versionId)))
            {
                continue;
            }

            // 先看有没有现成实例，判断"这算不算新建"只为了给调用方一个数字
            var known = List().FirstOrDefault(i =>
                string.Equals(i.VersionId, versionId, StringComparison.OrdinalIgnoreCase));

            var hadFile = known is not null && File.Exists(paths.InstanceJsonPath(known.Id));

            // EnsureForVersion 是幂等的：已存在的只补文件，不存在的才建
            EnsureForVersion(versionId);

            if (!hadFile)
            {
                created++;
            }
        }

        // 兜底：不在 versions/ 里的实例（整合包直接建在 instances 下的）
        // 也补一份 instance.json，让它们和正式实例一样可改名、可存设置。
        foreach (var info in List())
        {
            if (!File.Exists(paths.InstanceJsonPath(info.Id)))
            {
                Save(info);
                created++;
            }
        }

        return created;
    }

    /// <summary>改名。只改显示名，目录 ID 不动，避免路径全乱。</summary>
    public void Rename(string id, string newName)
    {
        var info = Load(id);
        if (info is null || string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        info.Name = newName.Trim();
        Save(info);
    }

    /// <summary>
    /// 删除实例：整个目录挪进 instances/.trash/，不直接物理删除。
    /// 存档在里面，先给后悔的机会。
    /// </summary>
    public string? Delete(string id)
    {
        var directory = paths.InstanceDir(id);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var trashRoot = Path.Combine(paths.InstancesDir, ".trash");
        Directory.CreateDirectory(trashRoot);

        var target = Path.Combine(trashRoot, $"{id}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.Move(directory, target);
        return target;
    }

    /// <summary>实例的游戏目录（mods / saves / config 都在里面）。</summary>
    public string GameDirectory(string id) => paths.InstanceDir(id);

    /// <summary>保证游戏目录存在，启动前调。</summary>
    public string EnsureGameDirectory(string id)
    {
        var directory = paths.InstanceDir(id);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "mods"));
        return directory;
    }

    /// <summary>从显示名生成一个能当目录名用的唯一 ID。</summary>
    public string MakeUniqueId(string name)
    {
        var raw = (name ?? string.Empty).Trim();

        var cleaned = new string(raw
            .Select(c => InvalidIdChars.Contains(c) || char.IsControl(c) ? '-' : c)
            .ToArray())
            .Trim('.', '-', ' ', '\t');

        if (cleaned.Length == 0)
        {
            cleaned = "instance";
        }

        if (cleaned.Length > 48)
        {
            cleaned = cleaned[..48];
        }

        var candidate = cleaned;
        var index = 2;

        while (Directory.Exists(paths.InstanceDir(candidate)))
        {
            candidate = $"{cleaned}-{index++}";
        }

        return candidate;
    }

    private InstanceInfo? Read(string id)
    {
        var path = Path.Combine(paths.InstanceDir(id), JsonFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = JsonSerializer.Deserialize<InstanceInfo>(File.ReadAllText(path), FileOptions);
            if (info is null)
            {
                return null;
            }

            info.Id = id;
            info.Name = string.IsNullOrWhiteSpace(info.Name) ? id : info.Name;
            info.Settings ??= new InstanceSettings();

            // 版本信息丢了就按目录名兜底：老数据目录名就是版本 ID
            if (string.IsNullOrWhiteSpace(info.VersionId))
            {
                info.VersionId = id;
            }

            return info;
        }
        catch
        {
            // 坏掉的实例文件不拖垮整个列表
            return null;
        }
    }

    /// <summary>没有 instance.json 的老目录：当成"版本同名实例"。</summary>
    private InstanceInfo LegacyInstance(string id)
    {
        var info = new InstanceInfo
        {
            Id = id,
            Name = id,
            VersionId = id,
            Settings = InstanceSettings.Load(paths, id),
        };

        // 整合包导入建的实例：版本和名字记在 modpack.json 里，比目录名准
        var modpackPath = Path.Combine(paths.InstanceDir(id), "modpack.json");
        if (File.Exists(modpackPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(modpackPath));
                var root = document.RootElement;

                if (root.TryGetProperty("VersionId", out var version) &&
                    version.GetString() is { Length: > 0 } versionId)
                {
                    info.VersionId = versionId;
                }

                if (root.TryGetProperty("Name", out var name) &&
                    name.GetString() is { Length: > 0 } packName)
                {
                    info.Name = packName;
                }
            }
            catch
            {
                // 读不出来就按目录名兜底
            }
        }

        return info;
    }

    /// <summary>判断老设置是不是一片空白（全是默认值），避免给每个版本都写一遍。</summary>
    private static bool SettingsAreDefault(InstanceSettings settings)
        => settings.MaxMemoryMb == 2048
           && settings.MinMemoryMb == 0
           && string.IsNullOrWhiteSpace(settings.JavaPath)
           && string.IsNullOrWhiteSpace(settings.ExtraJvmArgs)
           && settings.WindowWidth == 1280
           && settings.WindowHeight == 720
           && !settings.Fullscreen;
}
