using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Songyu.Services;

/// <summary>实例里可被索引的内容类型（对应 Minecraft 的几个内容目录）。</summary>
public enum ContentKind
{
    Mod,
    ResourcePack,
    ShaderPack,
    Unknown,
}

/// <summary>索引里的一条记录：磁盘上的一个文件 + 它的来源。</summary>
public sealed record IndexedFile(
    string InstanceId,
    string RelativePath,
    string FileName,
    ContentKind Kind,
    bool Enabled,
    string Sha1,
    long Size,
    bool Missing,
    string? ProjectId,
    string? VersionId,
    string? ProjectType,
    string? ModId = null,
    string? DisplayName = null,
    string? ModVersion = null,
    string? ModLoader = null,
    string? RemoteVersion = null,
    string? UpdateVersionId = null,
    string? UpdateVersionNumber = null,
    long? CheckedAt = null)
{
    /// <summary>能定位到 Modrinth 项目才算"来源已知"。</summary>
    public bool HasSource => !string.IsNullOrEmpty(ProjectId);

    /// <summary>检查过更新，且确实有可用新版本。</summary>
    public bool HasUpdate => !string.IsNullOrEmpty(UpdateVersionId);

    /// <summary>查过更新了没有（没查过和"查过但没更新"要分开）。</summary>
    public bool UpdateChecked => CheckedAt.HasValue;

    public string UpdateText => HasUpdate
        ? (string.IsNullOrEmpty(UpdateVersionNumber) ? "有更新" : $"有更新 {UpdateVersionNumber}")
        : string.Empty;

    public string SourceText => HasSource
        ? (string.IsNullOrEmpty(VersionId) ? ProjectId! : $"{ProjectId} @ {VersionId}")
        : "本地（来源未知）";

    /// <summary>界面上显示的版本号：jar 里的优先，占位符被清掉时退回 Modrinth 的版本号。</summary>
    public string VersionText
    {
        get
        {
            if (!string.IsNullOrEmpty(ModVersion))
            {
                return ModVersion!;
            }

            return RemoteVersion ?? string.Empty;
        }
    }

    /// <summary>界面上显示的名字：jar 元数据优先，没有就用文件名。</summary>
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? FileName : DisplayName!;

    /// <summary>jar 内元数据是否已读过（没有的话需要打开 jar 读一次）。</summary>
    public bool HasJarMetadata => !string.IsNullOrEmpty(DisplayName);
}

/// <summary>
/// 本地内容索引 <c>data/index.db</c>。
/// <para>
/// 解决一个根本问题：光看文件名 / jar 内元数据，无法回答"这个文件是哪个项目的哪个版本"，
/// 于是更新检查、冲突诊断、整合包同步都做不了。
/// 索引层把"磁盘上有什么"和"它来自哪里"落到一张表里，后续功能都查这张表。
/// </para>
/// <para>
/// 表结构按"文件身份 + 来源元数据"两段设计：
/// 相对路径为主键、sha1 做身份、<c>missing</c> 标记外部删除、<c>enabled</c> 记录禁用状态。
/// </para>
/// </summary>
public sealed class ContentIndex(MinecraftPaths paths)
{
    /// <summary>会被索引的内容目录（相对实例根）。</summary>
    private static readonly (string Directory, ContentKind Kind)[] ScannedDirectories =
    [
        ("mods", ContentKind.Mod),
        ("resourcepacks", ContentKind.ResourcePack),
        ("shaderpacks", ContentKind.ShaderPack),
    ];

    private string DatabasePath => Path.Combine(paths.Root, "index.db");

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
    }.ToString();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        // WAL：读写不互相阻塞，扫描和 UI 查询可以同时进行
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>建表（幂等），启动时调一次。</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(paths.Root);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS instance_files (
                instance_id   TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                file_name     TEXT NOT NULL,
                kind          TEXT NOT NULL,
                enabled       INTEGER NOT NULL,
                sha1          TEXT NOT NULL,
                size          INTEGER NOT NULL,
                mtime_ticks   INTEGER NOT NULL,
                missing       INTEGER NOT NULL DEFAULT 0,
                project_id    TEXT NULL,
                version_id    TEXT NULL,
                project_type  TEXT NULL,
                mod_id        TEXT NULL,
                display_name  TEXT NULL,
                mod_version   TEXT NULL,
                mod_loader    TEXT NULL,
                remote_version TEXT NULL,
                update_version_id TEXT NULL,
                update_version_number TEXT NULL,
                checked_at    INTEGER NULL,
                updated_at    INTEGER NOT NULL,
                PRIMARY KEY (instance_id, relative_path)
            );

            CREATE INDEX IF NOT EXISTS idx_instance_files_sha1
                ON instance_files(sha1);
            CREATE INDEX IF NOT EXISTS idx_instance_files_missing
                ON instance_files(instance_id, missing);

            -- 按 sha1 缓存"这个文件属于哪个项目/版本"，跨实例共用，避免重复请求 API
            CREATE TABLE IF NOT EXISTS file_sources (
                sha1         TEXT PRIMARY KEY,
                project_id   TEXT NOT NULL,
                version_id   TEXT NOT NULL,
                project_type TEXT NULL,
                version_number TEXT NULL,
                fetched_at   INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        // 老库兼容：SQLite 的 ADD COLUMN 没有 IF NOT EXISTS，重复执行会抛错，逐列吞掉
        foreach (var column in new[] { "mod_id", "display_name", "mod_version", "mod_loader", "remote_version", "update_version_id", "update_version_number" })
        {
            try
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE instance_files ADD COLUMN {column} TEXT NULL;";
                alter.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // 列已存在，跳过
            }
        }
    }

    /// <summary>
    /// 扫描实例的内容目录，把结果同步进索引，返回同步后的完整列表。
    /// <para>
    /// 策略完全是增量式的：文件大小 + 修改时间都没变就复用上次算的 sha1，
    /// 只有变动过的文件才重新哈希 —— 283 个 jar 冷启动约 1 秒，之后接近 0。
    /// </para>
    /// </summary>
    public IReadOnlyList<IndexedFile> Sync(string instanceId, CancellationToken cancellationToken = default)
    {
        Initialize();

        var directory = paths.InstanceDir(instanceId);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var existing = LoadExisting(instanceId);

        var scanned = new List<(string RelativePath, string FileName, ContentKind Kind, bool Enabled, long Size, long MtimeTicks, string FullPath)>();

        foreach (var (subDirectory, kind) in ScannedDirectories)
        {
            var full = Path.Combine(directory, subDirectory);
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = Path.GetFileName(file);
                if (!LooksLikeContentFile(name))
                {
                    continue;
                }

                var enabled = !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var info = new FileInfo(file);

                scanned.Add((
                    Path.GetRelativePath(directory, file).Replace('\\', '/'),
                    name,
                    kind,
                    enabled,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks,
                    file));
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<IndexedFile>(scanned.Count);

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        foreach (var item in scanned)
        {
            seenPaths.Add(item.RelativePath);

            // 大小与修改时间都没变 → 直接复用上次的 sha1、来源与 jar 元数据
            string sha1;
            string? projectId = null;
            string? versionId = null;
            string? projectType = null;
            long? checkedAt = null;
            string? modId = null;
            string? displayName = null;
            string? modVersion = null;
            string? modLoader = null;
            string? remoteVersion = null;
            string? updateVersionId = null;
            string? updateVersionNumber = null;

            if (existing.TryGetValue(item.RelativePath, out var previous) &&
                previous.Size == item.Size &&
                previous.MtimeTicks == item.MtimeTicks)
            {
                sha1 = previous.Sha1;
                projectId = previous.ProjectId;
                versionId = previous.VersionId;
                projectType = previous.ProjectType;
                checkedAt = previous.CheckedAt;

                modId = previous.Self.ModId;
                displayName = previous.Self.DisplayName;
                modVersion = previous.Self.ModVersion;
                modLoader = previous.Self.ModLoader;
                remoteVersion = previous.Self.RemoteVersion;
                updateVersionId = previous.Self.UpdateVersionId;
                updateVersionNumber = previous.Self.UpdateVersionNumber;
            }
            else
            {
                sha1 = ComputeSha1(item.FullPath, cancellationToken);

                // 同一个 hash 之前查过来源就直接沿用，省一次 API
                if (LookupCachedSource(connection, transaction, sha1) is { } cached)
                {
                    projectId = cached.ProjectId;
                    versionId = cached.VersionId;
                    projectType = cached.ProjectType;
                    remoteVersion = cached.VersionNumber;
                }
            }

            var indexed = new IndexedFile(
                instanceId,
                item.RelativePath,
                item.FileName,
                item.Kind,
                item.Enabled,
                sha1,
                item.Size,
                false,
                projectId,
                versionId,
                projectType,
                modId,
                displayName,
                modVersion,
                modLoader,
                remoteVersion,
                updateVersionId,
                updateVersionNumber);

            UpsertFile(connection, transaction, indexed, item.MtimeTicks, checkedAt, now);

            result.Add(indexed);
        }

        // 索引里有、磁盘上没了 → 只标 missing，不删记录（用户可能只是临时移走）
        foreach (var (relativePath, previous) in existing)
        {
            if (seenPaths.Contains(relativePath))
            {
                continue;
            }

            MarkMissing(connection, transaction, instanceId, relativePath, now);

            result.Add(previous.Self with { Missing = true });
        }

        transaction.Commit();

        return result
            .OrderBy(f => f.Kind)
            .ThenBy(f => f.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 回写更新检查结果。
    /// <para>
    /// 先把该实例所有 Mod 的更新字段清空（表示"这次检查过、当前没更新"），
    /// 再填入确实有更新的那些 —— 这样 UI 能区分"没查过"和"查了没更新"。
    /// </para>
    /// </summary>
    public void ApplyUpdateChecks(string instanceId, IReadOnlyDictionary<string, UpdateInfo> updates)
    {
        Initialize();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = """
                UPDATE instance_files
                SET update_version_id = NULL, update_version_number = NULL, checked_at = $now
                WHERE instance_id = $instance AND kind = 'Mod';
                """;
            clear.Parameters.AddWithValue("$instance", instanceId);
            clear.Parameters.AddWithValue("$now", now);
            clear.ExecuteNonQuery();
        }

        foreach (var (sha1, info) in updates)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE instance_files
                SET update_version_id = $versionId, update_version_number = $versionNumber,
                    checked_at = $now
                WHERE instance_id = $instance AND sha1 = $sha1;
                """;
            update.Parameters.AddWithValue("$instance", instanceId);
            update.Parameters.AddWithValue("$sha1", sha1);
            update.Parameters.AddWithValue("$versionId", info.VersionId);
            update.Parameters.AddWithValue("$versionNumber", (object?)info.VersionNumber ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>读索引（不扫盘）。</summary>
    public IReadOnlyList<IndexedFile> GetFiles(string instanceId)
    {
        Initialize();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT instance_id, relative_path, file_name, kind, enabled, sha1, size,
                   missing, project_id, version_id, project_type,
                   mod_id, display_name, mod_version, mod_loader, remote_version,
                   update_version_id, update_version_number, checked_at
            FROM instance_files
            WHERE instance_id = $instance
            ORDER BY kind, file_name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$instance", instanceId);

        var result = new List<IndexedFile>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadFile(reader));
        }

        return result;
    }

    /// <summary>
    /// 按 sha1 找一个本地已存在的同内容文件（跨实例复用，免掉重复下载）。
    /// 找不到或索引里的记录已失效时返回 null。
    /// </summary>
    public string? FindExistingFile(string sha1)
    {
        if (string.IsNullOrEmpty(sha1))
        {
            return null;
        }

        Initialize();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT instance_id, relative_path
            FROM instance_files
            WHERE sha1 = $sha1 AND missing = 0
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sha1", sha1);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var candidate = Path.Combine(
            paths.InstanceDir(reader.GetString(0)),
            reader.GetString(1).Replace('/', Path.DirectorySeparatorChar));

        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>实例里所有 Mod 的 sha1（给更新检查用）。</summary>
    public IReadOnlyList<string> GetModSha1s(string instanceId)
    {
        Initialize();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT sha1
            FROM instance_files
            WHERE instance_id = $instance AND kind = 'Mod' AND missing = 0;
            """;
        command.Parameters.AddWithValue("$instance", instanceId);

        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>哪些 sha1 还没查到来源（给批量反查用）。</summary>
    public IReadOnlyList<string> GetUnresolvedSha1s(string instanceId)
    {
        Initialize();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT sha1
            FROM instance_files
            WHERE instance_id = $instance AND project_id IS NULL AND missing = 0;
            """;
        command.Parameters.AddWithValue("$instance", instanceId);

        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>
    /// 批量回填来源（来自 <c>POST /v2/version_files</c> 的结果）。
    /// 跨实例共用：同一个 sha1 在别的实例里也能直接用。
    /// </summary>
    public int ApplySources(IReadOnlyDictionary<string, FileSource> sources)
    {
        if (sources.Count == 0)
        {
            return 0;
        }

        Initialize();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        foreach (var (sha1, source) in sources)
        {
            using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO file_sources (sha1, project_id, version_id, project_type, version_number, fetched_at)
                    VALUES ($sha1, $project, $version, $type, $versionNumber, $now)
                    ON CONFLICT(sha1) DO UPDATE SET
                        project_id = excluded.project_id,
                        version_id = excluded.version_id,
                        project_type = excluded.project_type,
                        version_number = excluded.version_number,
                        fetched_at = excluded.fetched_at;
                    """;
                upsert.Parameters.AddWithValue("$sha1", sha1);
                upsert.Parameters.AddWithValue("$project", source.ProjectId);
                upsert.Parameters.AddWithValue("$version", source.VersionId);
                upsert.Parameters.AddWithValue("$type", (object?)source.ProjectType ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$versionNumber", (object?)source.VersionNumber ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$now", now);
                upsert.ExecuteNonQuery();
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE instance_files
                SET project_id = $project, version_id = $version,
                    project_type = $type, remote_version = $versionNumber, updated_at = $now
                WHERE sha1 = $sha1;
                """;
            update.Parameters.AddWithValue("$sha1", sha1);
            update.Parameters.AddWithValue("$project", source.ProjectId);
            update.Parameters.AddWithValue("$version", source.VersionId);
            update.Parameters.AddWithValue("$type", (object?)source.ProjectType ?? DBNull.Value);
            update.Parameters.AddWithValue("$versionNumber", (object?)source.VersionNumber ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
        return sources.Count;
    }

    /// <summary>把这个实例里已识别的文件清掉来源（用于强制重新识别）。</summary>
    public void ClearSources(string instanceId)
    {
        Initialize();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE instance_files
            SET project_id = NULL, version_id = NULL, project_type = NULL
            WHERE instance_id = $instance;
            """;
        command.Parameters.AddWithValue("$instance", instanceId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 回写 jar 内元数据（名字/版本/modid/加载器）。
    /// <para>
    /// 打开 jar 读元数据是有成本的（283 个 jar 约 3 秒），所以结果必须缓存，
    /// 之后进同一实例就不用再开 jar 了。
    /// </para>
    /// </summary>
    public void ApplyJarMetadata(string instanceId, IReadOnlyDictionary<string, JarMetadata> metadata)
    {
        if (metadata.Count == 0)
        {
            return;
        }

        Initialize();

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        foreach (var (relativePath, jar) in metadata)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE instance_files
                SET mod_id = $modId, display_name = $name,
                    mod_version = $version, mod_loader = $loader
                WHERE instance_id = $instance AND relative_path = $path;
                """;
            command.Parameters.AddWithValue("$instance", instanceId);
            command.Parameters.AddWithValue("$path", relativePath);
            command.Parameters.AddWithValue("$modId", (object?)jar.ModId ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", (object?)jar.Name ?? DBNull.Value);
            command.Parameters.AddWithValue("$version", (object?)jar.Version ?? DBNull.Value);
            command.Parameters.AddWithValue("$loader", (object?)jar.Loader ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>当前实例里哪些文件还没读过 jar 元数据（返回相对路径 → 绝对路径）。</summary>
    public IReadOnlyDictionary<string, string> GetFilesNeedingJarMetadata(string instanceId)
    {
        Initialize();

        var root = paths.InstanceDir(instanceId);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT relative_path FROM instance_files
            WHERE instance_id = $instance
              AND kind = 'Mod'
              AND missing = 0
              AND (display_name IS NULL OR display_name = '');
            """;
        command.Parameters.AddWithValue("$instance", instanceId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var relative = reader.GetString(0);
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            result[relative] = full;
        }

        return result;
    }

    // ── 内部 ──

    private sealed record ExistingFile(
        string Sha1,
        long Size,
        long MtimeTicks,
        string? ProjectId,
        string? VersionId,
        string? ProjectType,
        long? CheckedAt,
        IndexedFile Self);

    private Dictionary<string, ExistingFile> LoadExisting(string instanceId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT instance_id, relative_path, file_name, kind, enabled, sha1, size,
                   missing, project_id, version_id, project_type,
                   mod_id, display_name, mod_version, mod_loader, remote_version,
                   update_version_id, update_version_number,
                   mtime_ticks, checked_at
            FROM instance_files
            WHERE instance_id = $instance;
            """;
        command.Parameters.AddWithValue("$instance", instanceId);

        var result = new Dictionary<string, ExistingFile>(StringComparer.OrdinalIgnoreCase);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var file = ReadFile(reader);
            var mtime = reader.GetInt64(18);
            var checkedAt = reader.IsDBNull(19) ? (long?)null : reader.GetInt64(19);

            result[file.RelativePath] = new ExistingFile(
                file.Sha1, file.Size, mtime, file.ProjectId, file.VersionId, file.ProjectType,
                checkedAt, file);
        }

        return result;
    }

    private static IndexedFile ReadFile(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        Enum.TryParse<ContentKind>(reader.GetString(3), out var kind) ? kind : ContentKind.Unknown,
        reader.GetInt32(4) != 0,
        reader.GetString(5),
        reader.GetInt64(6),
        reader.GetInt32(7) != 0,
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.IsDBNull(16) ? null : reader.GetString(16),
        reader.IsDBNull(17) ? null : reader.GetString(17),
        reader.IsDBNull(18) ? null : reader.GetInt64(18));

    private static FileSource? LookupCachedSource(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sha1)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT project_id, version_id, project_type, version_number
            FROM file_sources WHERE sha1 = $sha1;
            """;
        command.Parameters.AddWithValue("$sha1", sha1);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new FileSource(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static void UpsertFile(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IndexedFile file,
        long mtimeTicks,
        long? checkedAt,
        long now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO instance_files
                (instance_id, relative_path, file_name, kind, enabled, sha1, size,
                 mtime_ticks, missing, project_id, version_id, project_type,
                 mod_id, display_name, mod_version, mod_loader, remote_version,
                 update_version_id, update_version_number, checked_at, updated_at)
            VALUES
                ($instance, $path, $name, $kind, $enabled, $sha1, $size,
                 $mtime, 0, $project, $version, $type,
                 $modId, $displayName, $modVersion, $modLoader, $remoteVersion,
                 $updateId, $updateNumber, $checked, $now)
            ON CONFLICT(instance_id, relative_path) DO UPDATE SET
                file_name = excluded.file_name,
                kind = excluded.kind,
                enabled = excluded.enabled,
                sha1 = excluded.sha1,
                size = excluded.size,
                mtime_ticks = excluded.mtime_ticks,
                missing = 0,
                project_id = excluded.project_id,
                version_id = excluded.version_id,
                project_type = excluded.project_type,
                mod_id = COALESCE(excluded.mod_id, instance_files.mod_id),
                display_name = COALESCE(excluded.display_name, instance_files.display_name),
                mod_version = COALESCE(excluded.mod_version, instance_files.mod_version),
                mod_loader = COALESCE(excluded.mod_loader, instance_files.mod_loader),
                remote_version = COALESCE(excluded.remote_version, instance_files.remote_version),
                update_version_id = excluded.update_version_id,
                update_version_number = excluded.update_version_number,
                checked_at = excluded.checked_at,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$instance", file.InstanceId);
        command.Parameters.AddWithValue("$path", file.RelativePath);
        command.Parameters.AddWithValue("$name", file.FileName);
        command.Parameters.AddWithValue("$kind", file.Kind.ToString());
        command.Parameters.AddWithValue("$enabled", file.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$sha1", file.Sha1);
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$mtime", mtimeTicks);
        command.Parameters.AddWithValue("$project", (object?)file.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", (object?)file.VersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", (object?)file.ProjectType ?? DBNull.Value);
        command.Parameters.AddWithValue("$modId", (object?)file.ModId ?? DBNull.Value);
        command.Parameters.AddWithValue("$displayName", (object?)file.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$modVersion", (object?)file.ModVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$modLoader", (object?)file.ModLoader ?? DBNull.Value);
        command.Parameters.AddWithValue("$remoteVersion", (object?)file.RemoteVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$updateId", (object?)file.UpdateVersionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$updateNumber", (object?)file.UpdateVersionNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$checked", (object?)checkedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static void MarkMissing(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string instanceId,
        string relativePath,
        long now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE instance_files
            SET missing = 1, updated_at = $now
            WHERE instance_id = $instance AND relative_path = $path;
            """;
        command.Parameters.AddWithValue("$instance", instanceId);
        command.Parameters.AddWithValue("$path", relativePath);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    /// <summary>只有 jar / zip 这类才可能是内容文件，跳过 .part、说明文件等。</summary>
    private static bool LooksLikeContentFile(string fileName)
        => fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
           || fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
           || fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
           || fileName.EndsWith(".zip.disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>SHA1（Modrinth 用 sha1 做文件身份，所以这里必须用 sha1 而不是 sha256）。</summary>
    public static string ComputeSha1(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var sha1 = SHA1.Create();

        var buffer = new byte[81920];
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha1.TransformBlock(buffer, 0, read, null, 0);
        }

        sha1.TransformFinalBlock(buffer, 0, 0);
        return Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
    }
}

/// <summary>一个文件在 Modrinth 上的来源。</summary>
public sealed record FileSource(
    string ProjectId,
    string VersionId,
    string? ProjectType,
    string? VersionNumber = null);

/// <summary>从 jar 里读出来的元数据（缓存进索引，避免每次重新开 jar）。</summary>
public sealed record JarMetadata(string? ModId, string? Name, string? Version, string? Loader);

/// <summary>一个文件的更新检查结果。</summary>
public sealed record UpdateInfo(string VersionId, string? VersionNumber);

/// <summary>索引记录的路径辅助。</summary>
public static class IndexedFileExtensions
{
    /// <summary>把索引里的相对路径还原成绝对路径。</summary>
    public static string FullPathFor(this IndexedFile file, MinecraftPaths paths)
        => Path.Combine(
            paths.InstanceDir(file.InstanceId),
            file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
}
