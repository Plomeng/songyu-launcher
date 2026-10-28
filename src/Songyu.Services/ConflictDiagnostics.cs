namespace Songyu.Services;

/// <summary>一条诊断结论。</summary>
public sealed record DiagnosticIssue(
    string Kind,
    string Subject,
    string Detail);

/// <summary>一次冲突诊断的结果。</summary>
public sealed record DiagnosticReport(
    int CheckedMods,
    IReadOnlyList<DiagnosticIssue> MissingDependencies,
    IReadOnlyList<DiagnosticIssue> Duplicates,
    IReadOnlyList<DiagnosticIssue> Incompatible,
    IReadOnlyList<DiagnosticIssue> CompatibilityProvided)
{
    /// <summary>只算"真实问题"，兼容层提供的不算。</summary>
    public int TotalIssues =>
        MissingDependencies.Count + Duplicates.Count + Incompatible.Count;

    public bool IsClean => TotalIssues == 0;

    /// <summary>给弹窗用的一段纯文本。</summary>
    public string Describe()
    {
        if (CheckedMods == 0)
        {
            return "这个实例里还没有 Mod。";
        }

        if (IsClean && CompatibilityProvided.Count == 0)
        {
            return $"检查了 {CheckedMods} 个 Mod，没有发现依赖缺失、重复安装或声明冲突。";
        }

        if (IsClean)
        {
            var onlyCompat = new List<string>
            {
                $"检查了 {CheckedMods} 个 Mod，没有发现真实问题。",
                string.Empty,
                $"另有 {CompatibilityProvided.Count} 个依赖由兼容层提供（不计入问题）：",
            };

            foreach (var issue in CompatibilityProvided.Take(6))
            {
                onlyCompat.Add($"· {issue.Subject} 需要「{issue.Detail}」");
            }

            if (CompatibilityProvided.Count > 6)
            {
                onlyCompat.Add($"… 还有 {CompatibilityProvided.Count - 6} 个");
            }

            onlyCompat.Add(string.Empty);
            onlyCompat.Add("游戏能正常进就不用管这些。");
            return string.Join(Environment.NewLine, onlyCompat);
        }

        var lines = new List<string>
        {
            $"检查了 {CheckedMods} 个 Mod，发现 {TotalIssues} 个真实问题" +
            (CompatibilityProvided.Count > 0
                ? $"（另有 {CompatibilityProvided.Count} 个依赖由兼容层提供，已排除）"
                : "："),
        };

        if (TotalIssues == 0)
        {
            lines[0] += "：";
        }

        AppendSection(lines, "缺少前置 / 依赖", MissingDependencies);
        AppendSection(lines, "重复安装", Duplicates);
        AppendSection(lines, "声明冲突", Incompatible);

        lines.Add(string.Empty);
        lines.Add("说明：这里只做元数据层面的检查（Modrinth 上的依赖声明），");
        lines.Add("不分析运行期崩溃日志。有些 Mod 的依赖声明本身就不严谨，会出现误报。");

        if (CompatibilityProvided.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"已排除 {CompatibilityProvided.Count} 个「由兼容层提供」的依赖：");
            lines.Add("这些项目的加载器与本实例不匹配（例如 Forge 包里的纯 Fabric 依赖），");
            lines.Add("通常由 Sinytra Connector 之类的兼容层提供，不需要单独安装。");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendSection(List<string> lines, string title, IReadOnlyList<DiagnosticIssue> issues)
    {
        if (issues.Count == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add($"【{title}】{issues.Count} 个");

        foreach (var issue in issues.Take(15))
        {
            lines.Add($"· {issue.Subject} —— {issue.Detail}");
        }

        if (issues.Count > 15)
        {
            lines.Add($"… 还有 {issues.Count - 15} 个");
        }
    }
}

/// <summary>
/// Mod 冲突诊断（元数据层面）。
/// <para>
/// 只看 Modrinth 上的版本声明：<c>dependencies[]</c> 里的
/// <c>required</c>（是否装了）、<c>incompatible</c>（是否在场），
/// 加上"同一个项目装了两个版本"。
/// </para>
/// <para>
/// 刻意不做运行期崩溃分析 —— Modrinth 官方客户端也不做，
/// 承诺了又做不到比不承诺更糟。
/// </para>
/// </summary>
public sealed class ConflictDiagnostics(
    MinecraftPaths paths,
    ContentIndex index,
    ModrinthClient modrinth)
{
    public async Task<DiagnosticReport> AnalyzeAsync(
        string instanceId,
        string versionId,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var mods = index.GetFiles(instanceId)
            .Where(f => f.Kind == ContentKind.Mod && !f.Missing)
            .ToList();

        if (mods.Count == 0)
        {
            return new DiagnosticReport(0, [], [], [], []);
        }

        // 加载器决定"某个依赖到底该不该装在这儿"。
        // 版本 json 缺失时（整合包导入留下的历史目录）从 Mod 本身推断。
        var context = GameContextResolver.ResolveForInstance(paths, versionId, mods);
        var instanceLoaders = context.Loaders;

        // 已装 Mod 的名字（归一化），用来兜住"同一项目在 Modrinth 上有多个条目"的情况 ——
        // API 的 project_id 对不上，但装的东西其实是同一个。
        var installedNames = mods
            .Select(m => NormalizeName(m.Name))
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        progress?.Report($"读取 {mods.Count} 个 Mod 的依赖声明…");

        var sha1s = mods.Select(m => m.Sha1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var versionsBySha1 = await modrinth.GetVersionsByHashesAsync(sha1s, cancellationToken);

        // 已装的项目集合（Modrinth 认得出来的那些）
        var installedProjects = mods
            .Where(m => !string.IsNullOrEmpty(m.ProjectId))
            .GroupBy(m => m.ProjectId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var missing = new List<DiagnosticIssue>();
        var duplicates = new List<DiagnosticIssue>();
        var incompatible = new List<DiagnosticIssue>();
        var compatibilityProvided = new List<DiagnosticIssue>();

        // 先把缺失的依赖 ID 收集起来，一次批量查项目信息（名字 + 支持的加载器）
        var missingProjectIds = new List<string>();
        var missingByMod = new List<(IndexedFile Mod, ModDependency Dependency)>();

        foreach (var mod in mods)
        {
            if (string.IsNullOrEmpty(mod.ProjectId) ||
                !versionsBySha1.TryGetValue(mod.Sha1, out var version))
            {
                continue;
            }

            foreach (var dependency in version.Dependencies)
            {
                if (dependency.DependencyType != "required" || string.IsNullOrEmpty(dependency.ProjectId))
                {
                    continue;
                }

                if (installedProjects.ContainsKey(dependency.ProjectId) ||
                    string.Equals(dependency.ProjectId, mod.ProjectId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                missingProjectIds.Add(dependency.ProjectId);
                missingByMod.Add((mod, dependency));
            }
        }

        IReadOnlyDictionary<string, ProjectSummary> summaries =
            new Dictionary<string, ProjectSummary>(StringComparer.OrdinalIgnoreCase);

        if (missingProjectIds.Count > 0)
        {
            progress?.Report($"查询 {missingProjectIds.Count} 个缺失依赖的信息…");
            summaries = await modrinth.GetProjectSummariesAsync(missingProjectIds, cancellationToken);
        }

        // 1. 同一项目装了多个版本
        foreach (var (projectId, group) in installedProjects)
        {
            if (group.Count <= 1)
            {
                continue;
            }

            var versions = group
                .Select(m => string.IsNullOrEmpty(m.VersionText) ? m.FileName : m.VersionText)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            duplicates.Add(new DiagnosticIssue(
                "duplicate",
                group[0].Name,
                $"{group.Count} 个文件：{string.Join("、", versions)}"));
        }

        // 2. 缺失的 required 依赖：按加载器分成"真实缺失"和"兼容层提供"
        foreach (var (mod, dependency) in missingByMod)
        {
            summaries.TryGetValue(dependency.ProjectId!, out var summary);

            var name = summary?.Title ?? dependency.ProjectId!;

            // 已装列表里名字对得上的，就当它已经装了 —— 同一个 Mod 在 Modrinth
            // 上可能有两个项目条目（比如旧的 Fabric 版和新的合并版），project_id 对不上
            if (installedNames.Contains(NormalizeName(name)))
            {
                continue;
            }

            var issue = new DiagnosticIssue("missing", mod.Name, name);

            // 只支持别的加载器 → 这个实例本来就不该装它，多半由兼容层提供
            if (IsCompatibilityProvided(instanceLoaders, summary))
            {
                compatibilityProvided.Add(issue);
            }
            else
            {
                missing.Add(issue);
            }
        }

        // 3. incompatible 声明
        foreach (var mod in mods)
        {
            if (string.IsNullOrEmpty(mod.ProjectId) ||
                !versionsBySha1.TryGetValue(mod.Sha1, out var version))
            {
                continue;
            }

            foreach (var dependency in version.Dependencies)
            {
                if (dependency.DependencyType != "incompatible" ||
                    string.IsNullOrEmpty(dependency.ProjectId))
                {
                    continue;
                }

                CheckIncompatible(mod, dependency, installedProjects, incompatible);
            }
        }

        // 4. 只声明了 version_id 的 required 依赖（没法反查项目，单独提示）
        foreach (var mod in mods)
        {
            if (string.IsNullOrEmpty(mod.ProjectId) ||
                !versionsBySha1.TryGetValue(mod.Sha1, out var version))
            {
                continue;
            }

            foreach (var dependency in version.Dependencies)
            {
                if (dependency.DependencyType == "required" &&
                    string.IsNullOrEmpty(dependency.ProjectId) &&
                    !string.IsNullOrEmpty(dependency.VersionId))
                {
                    missing.Add(new DiagnosticIssue(
                        "missing",
                        mod.Name,
                        $"需要版本 {dependency.VersionId}，未在实例里找到"));
                }
            }
        }

        progress?.Report($"检查完成：{missing.Count + duplicates.Count + incompatible.Count} 个问题");

        return new DiagnosticReport(mods.Count, missing, duplicates, incompatible, compatibilityProvided);
    }

    /// <summary>
    /// 归一化名字用于比对：去掉大小写、空格、常见标点和括号内容。
    /// "Fabric API" / "fabric-api" / "FabricAPI" 应该算同一个东西。
    /// </summary>
    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var buffer = new System.Text.StringBuilder(name.Length);

        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer.Append(char.ToLowerInvariant(ch));
            }
        }

        return buffer.ToString();
    }

    /// <summary>
    /// 判断一个"缺失的依赖"是不是由兼容层提供的。
    /// <para>
    /// 依据：该项目的支持加载器与实例的加载器没有交集。
    /// 例如 Forge 实例里缺一个只支持 fabric 的项目 —— 那不是真缺，
    /// 是 Fabric Mod 通过 Sinytra Connector 跑在 Forge 上时的经典情形。
    /// </para>
    /// <para>
    /// 信息不足时（查不到项目、或项目没写加载器）一律按"真实缺失"处理，宁可多报不漏报。
    /// </para>
    /// </summary>
    private static bool IsCompatibilityProvided(
        IReadOnlyList<string> instanceLoaders,
        ProjectSummary? summary)
    {
        if (instanceLoaders.Count == 0)
        {
            return false;
        }

        if (summary is null || summary.Loaders.Count == 0)
        {
            return false;
        }

        return !summary.Loaders.Any(loader =>
            instanceLoaders.Contains(loader, StringComparer.OrdinalIgnoreCase));
    }

    private static void CheckIncompatible(
        IndexedFile mod,
        ModDependency dependency,
        IReadOnlyDictionary<string, List<IndexedFile>> installedProjects,
        List<DiagnosticIssue> incompatible)
    {
        if (string.IsNullOrEmpty(dependency.ProjectId) ||
            !installedProjects.TryGetValue(dependency.ProjectId, out var others))
        {
            return;
        }

        var other = others.FirstOrDefault(o => !string.Equals(o.ProjectId, mod.ProjectId, StringComparison.OrdinalIgnoreCase));
        if (other is null)
        {
            return;
        }

        incompatible.Add(new DiagnosticIssue(
            "incompatible",
            mod.Name,
            $"声明与 {other.Name} 不兼容，两者都在实例里"));
    }
}
