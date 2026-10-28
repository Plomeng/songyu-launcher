using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Songyu.Services;

/// <summary>
/// 实例 mods 目录里的一个 Mod 文件。
/// <para>
/// 解析时以 jar 内的元数据为准（fabric.mod.json / quilt.mod.json / mods.toml / mcmod.info），
/// 文件名只作兜底——因为很多整合包里的 jar 名字和 Mod 真名、版本号都对不上。
/// </para>
/// </summary>
public sealed record LocalMod(
    string FileName,
    string FullPath,
    bool Enabled,
    string ModId,
    string Name,
    string Version,
    string Loader,
    long Bytes,
    DateTime Modified)
{
    public string SizeText => Bytes switch
    {
        >= 1024 * 1024 => $"{Bytes / 1024.0 / 1024:F1} MB",
        >= 1024 => $"{Bytes / 1024} KB",
        _ => $"{Bytes} B",
    };

    public string StatusText => Enabled ? "已启用" : "已禁用";

    /// <summary>那一行按钮上的文字：现在是启用的就显示"禁用"。</summary>
    public string ToggleLabel => Enabled ? "禁用" : "启用";

    /// <summary>卡片第二行：加载器 · modid · 大小 · 状态。</summary>
    public string DetailLine
    {
        get
        {
            var modId = string.IsNullOrWhiteSpace(ModId) ? "未知 modid" : ModId;
            return $"{Loader}   ·   {modId}   ·   {SizeText}   ·   {StatusText}";
        }
    }

    /// <summary>界面上的一行：名字 + 版本，没有元数据时退回文件名。</summary>
    public string Display => string.IsNullOrWhiteSpace(Version) ? Name : $"{Name} {Version}";
}

/// <summary>
/// 本地 Mod 管理：扫描、启用/禁用、删除、导入。
/// <para>
/// 只碰 <c>instances/&lt;id&gt;/mods</c>，不碰版本目录、不联网。
/// 删除一律先进实例的回收目录，不物理删除。
/// </para>
/// </summary>
public sealed class ModScanner(MinecraftPaths paths)
{
    private const string DisabledSuffix = ".disabled";

    private static readonly Regex TomlBlockRegex = new(@"\[\[mods\]\](?<body>.*?)(?=\[\[|\z)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TomlKeyRegex = new(@"^\s*(?<key>[\w.-]+)\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>扫一个实例的 mods 目录，返回全部 Mod（含已禁用的）。</summary>
    public IReadOnlyList<LocalMod> Scan(string instanceId, CancellationToken cancellationToken = default)
    {
        var directory = paths.InstanceModsDir(instanceId);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<LocalMod>();

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(file);

            // 只认 .jar 和禁用后的 .jar.disabled，其它（.part、说明文件）忽略
            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".jar" + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var enabled = !name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
            var info = new FileInfo(file);

            var (modId, modName, version, loader) = ReadMetadata(file, name);

            result.Add(new LocalMod(
                name,
                file,
                enabled,
                modId,
                string.IsNullOrWhiteSpace(modName) ? CleanFileName(name) : modName,
                version,
                loader,
                info.Length,
                info.LastWriteTime));
        }

        return result
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>启用 / 禁用：改后缀，不动文件内容。</summary>
    public LocalMod? SetEnabled(LocalMod mod, bool enabled)
    {
        if (mod.Enabled == enabled)
        {
            return mod;
        }

        var target = SetEnabled(mod.FullPath, enabled);
        var info = new FileInfo(target);

        return mod with
        {
            FileName = Path.GetFileName(target),
            FullPath = target,
            Enabled = enabled,
            Modified = info.LastWriteTime,
        };
    }

    /// <summary>启用 / 禁用（按绝对路径），返回改完之后的路径。</summary>
    public static string SetEnabled(string fullPath, bool enabled)
    {
        var isEnabled = !fullPath.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);

        if (isEnabled == enabled)
        {
            return fullPath;
        }

        var target = enabled
            ? fullPath[..^DisabledSuffix.Length]
            : fullPath + DisabledSuffix;

        if (File.Exists(target))
        {
            throw new IOException($"目标文件已存在：{Path.GetFileName(target)}");
        }

        File.Move(fullPath, target);
        return target;
    }

    /// <summary>删除：挪进 <c>instances/&lt;id&gt;/mods/.trash/</c>，返回新路径。</summary>
    public string? Delete(LocalMod mod) => Delete(mod.FullPath);

    /// <summary>删除（按绝对路径）：挪进同目录的 .trash，返回新路径。</summary>
    public static string? Delete(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is null || !File.Exists(fullPath))
        {
            return null;
        }

        var trash = Path.Combine(directory, ".trash");
        Directory.CreateDirectory(trash);

        var target = Path.Combine(trash, $"{Path.GetFileName(fullPath)}.{DateTime.Now:yyyyMMdd-HHmmss}");
        File.Move(fullPath, target);
        return target;
    }

    /// <summary>把外部 jar 复制进实例的 mods 目录。重名时自动加序号，不覆盖。</summary>
    public LocalMod Import(string instanceId, string sourcePath)
    {
        var directory = paths.InstanceModsDir(instanceId);
        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileName(sourcePath);
        if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".jar";
        }

        var target = Path.Combine(directory, fileName);
        var index = 2;

        while (File.Exists(target))
        {
            target = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fileName)}-{index++}.jar");
        }

        File.Copy(sourcePath, target);

        var info = new FileInfo(target);
        var (modId, modName, version, loader) = ReadMetadata(target, Path.GetFileName(target));

        return new LocalMod(
            Path.GetFileName(target),
            target,
            true,
            modId,
            string.IsNullOrWhiteSpace(modName) ? CleanFileName(fileName) : modName,
            version,
            loader,
            info.Length,
            info.LastWriteTime);
    }

    /// <summary>在资源管理器里打开 mods 目录。</summary>
    public void OpenDirectory(string instanceId)
    {
        var directory = paths.InstanceModsDir(instanceId);
        Directory.CreateDirectory(directory);

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{directory}\"",
            UseShellExecute = true,
        });
    }

    // ── 元数据解析 ──

    /// <summary>
    /// 读单个 jar 的元数据，给内容索引缓存用。
    /// 解析不出东西时返回 null 字段（而不是空串），方便判断"没读到"。
    /// </summary>
    public static JarMetadata ReadJarMetadata(string jarPath)
    {
        var (modId, name, version, loader) = ReadMetadata(jarPath, Path.GetFileName(jarPath));

        return new JarMetadata(
            string.IsNullOrWhiteSpace(modId) ? null : modId,
            string.IsNullOrWhiteSpace(name) ? null : name,
            CleanVersion(version),
            string.IsNullOrWhiteSpace(loader) ? null : loader);
    }

    /// <summary>
    /// Forge 的 mods.toml 里版本常写成 <c>${file.jarVersion}</c> 这类占位符，
    /// 显示出来是个噪音，直接当"没有版本"处理。
    /// </summary>
    private static string? CleanVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return version.Contains("${", StringComparison.Ordinal) ? null : version;
    }

    private static (string ModId, string Name, string Version, string Loader) ReadMetadata(
        string jarPath, string fileName)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);

            if (FindEntry(archive, "fabric.mod.json") is { } fabric)
            {
                return ReadFabric(fabric);
            }

            if (FindEntry(archive, "quilt.mod.json") is { } quilt)
            {
                return ReadQuilt(quilt);
            }

            if (FindEntry(archive, "META-INF/neoforge.mods.toml") is { } neo)
            {
                return ReadToml(neo, "neoforge");
            }

            if (FindEntry(archive, "META-INF/mods.toml") is { } forge)
            {
                return ReadToml(forge, "forge");
            }

            if (FindEntry(archive, "mcmod.info") is { } legacy)
            {
                return ReadMcmodInfo(legacy);
            }
        }
        catch
        {
            // jar 损坏 / 不是 zip：退回文件名，不让一个坏文件拖垮整个列表
        }

        return (string.Empty, string.Empty, string.Empty, "unknown");
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name)
        => archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));

    private static (string, string, string, string) ReadFabric(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        return (
            Str(root, "id"),
            Str(root, "name"),
            Str(root, "version"),
            "fabric");
    }

    private static (string, string, string, string) ReadQuilt(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        if (root.TryGetProperty("quilt_loader", out var loader) && loader.ValueKind == JsonValueKind.Object)
        {
            var id = Str(loader, "id");
            var version = Str(loader, "version");

            var name = root.TryGetProperty("minecraft", out _) ? id : id;
            if (loader.TryGetProperty("metadata", out var metadata))
            {
                var metaName = Str(metadata, "name");
                if (!string.IsNullOrWhiteSpace(metaName))
                {
                    name = metaName;
                }
            }

            return (id, name, version, "quilt");
        }

        return ReadFabric(entry);
    }

    private static (string, string, string, string) ReadToml(ZipArchiveEntry entry, string loader)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        var block = TomlBlockRegex.Match(text);
        if (!block.Success)
        {
            return (string.Empty, string.Empty, string.Empty, loader);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TomlKeyRegex.Matches(block.Groups["body"].Value))
        {
            values.TryAdd(match.Groups["key"].Value, match.Groups["value"].Value);
        }

        return (
            values.GetValueOrDefault("modId", string.Empty),
            values.GetValueOrDefault("displayName", string.Empty),
            values.GetValueOrDefault("version", string.Empty),
            loader);
    }

    private static (string, string, string, string) ReadMcmodInfo(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);

        // mcmod.info 顶层是数组，个别 Mod 包成对象
        var first = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().FirstOrDefault()
            : document.RootElement;

        if (first.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, string.Empty, string.Empty, "forge");
        }

        return (
            Str(first, "modid"),
            Str(first, "name"),
            Str(first, "version"),
            "forge");
    }

    private static string Str(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>没有元数据时用文件名当名字：去掉 .jar / .disabled 和常见版本尾巴。</summary>
    private static string CleanFileName(string fileName)
    {
        var name = fileName;

        if (name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^DisabledSuffix.Length];
        }

        if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name;
    }
}
