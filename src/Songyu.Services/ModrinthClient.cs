using System.Net.Http;
using System.Text.Json;

namespace Songyu.Services;

public sealed record ModSearchHit(
    string ProjectId,
    string Slug,
    string Title,
    string Description,
    string IconUrl,
    long Downloads,
    string Author = "");

public sealed record ModProject(
    string Id,
    string Slug,
    string Title,
    string Description,
    string IconUrl,
    long Downloads,
    string SourceUrl,
    IReadOnlyList<string> Categories);

public sealed record ModFile(string Url, string Filename, string? Sha1, long Size);

public sealed record ModDependency(string? VersionId, string? ProjectId, string DependencyType);

/// <summary>项目摘要：标题 + 支持的加载器。</summary>
public sealed record ProjectSummary(string Title, IReadOnlyList<string> Loaders);

public sealed record ModVersion(
    string Id,
    string ProjectId,
    string Name,
    string VersionNumber,
    IReadOnlyList<ModFile> Files,
    IReadOnlyList<ModDependency> Dependencies,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders)
{
    /// <summary>主文件（Modrinth 里 primary=true 的那个，没有就取第一个 jar）。</summary>
    public ModFile? PrimaryFile =>
        Files.FirstOrDefault(f => f.Filename.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Modrinth v2 接口的薄封装。
/// 只做搜索、取版本、取单个版本三件事，够用且好懂。
/// </summary>
public sealed class ModrinthClient(HttpClient http)
{
    private const string BaseUrl = "https://api.modrinth.com/v2";

    public async Task<IReadOnlyList<ModSearchHit>> SearchAsync(
        string query,
        string? gameVersion,
        string? loader,
        string? projectType = "mod",
        int limit = 20,
        string? index = null,
        CancellationToken cancellationToken = default)
    {
        var facets = new List<string>();

        if (!string.IsNullOrEmpty(projectType))
        {
            facets.Add($"[\"project_type:{projectType}\"]");
        }

        if (!string.IsNullOrEmpty(gameVersion))
        {
            facets.Add($"[\"versions:{gameVersion}\"]");
        }

        if (!string.IsNullOrEmpty(loader))
        {
            facets.Add($"[\"categories:{loader}\"]");
        }

        var url = $"{BaseUrl}/search?query={Uri.EscapeDataString(query ?? string.Empty)}&limit={limit}";

        // index=downloads 就是「按下载量」的热门榜，空查询时用它出推荐
        if (!string.IsNullOrEmpty(index))
        {
            url += $"&index={Uri.EscapeDataString(index)}";
        }

        if (facets.Count > 0)
        {
            url += $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";
        }

        var raw = await GetAsync(url, cancellationToken);

        using var document = JsonDocument.Parse(raw);
        var hits = new List<ModSearchHit>();

        foreach (var hit in document.RootElement.GetProperty("hits").EnumerateArray())
        {
            hits.Add(new ModSearchHit(
                hit.GetProperty("project_id").GetString() ?? string.Empty,
                hit.TryGetProperty("slug", out var slug) ? slug.GetString() ?? string.Empty : string.Empty,
                hit.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                hit.TryGetProperty("description", out var desc) ? desc.GetString() ?? string.Empty : string.Empty,
                hit.TryGetProperty("icon_url", out var icon) ? icon.GetString() ?? string.Empty : string.Empty,
                hit.TryGetProperty("downloads", out var downloads) ? downloads.GetInt64() : 0,
                hit.TryGetProperty("author", out var author) ? author.GetString() ?? string.Empty : string.Empty));
        }

        return hits;
    }

    /// <summary>取项目详情（图标、下载量、简介、分类），用于搜索结果点开后的详情页。</summary>
    /// <summary>
    /// 取 Modrinth 支持的全部游戏版本（按发布时间倒序）。
    /// 用来让用户能筛任意版本，而不是只能筛已装实例的版本。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetGameVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var raw = await GetAsync($"{BaseUrl}/tag/game_version", cancellationToken);

        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return document.RootElement
            .EnumerateArray()
            .Select(v => new
            {
                Version = v.TryGetProperty("version", out var ver) ? ver.GetString() : null,
                Type = v.TryGetProperty("version_type", out var t) ? t.GetString() : null,
            })
            .Where(v => !string.IsNullOrEmpty(v.Version))
            .OrderBy(v => v.Type == "release" ? 0 : 1)
            .Select(v => v.Version!)
            .ToList();
    }

    public async Task<ModProject> GetProjectAsync(string idOrSlug, CancellationToken cancellationToken = default)
    {
        var raw = await GetAsync($"{BaseUrl}/project/{idOrSlug}", cancellationToken);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        return new ModProject(
            root.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("slug", out var slug) ? slug.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("description", out var desc) ? desc.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("icon_url", out var icon) ? icon.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("downloads", out var downloads) ? downloads.GetInt64() : 0,
            root.TryGetProperty("source_url", out var source) ? source.GetString() ?? string.Empty : string.Empty,
            ReadStringArray(root, "categories"));
    }

    public async Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        string projectId,
        string? gameVersion,
        string? loader,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();

        if (!string.IsNullOrEmpty(gameVersion))
        {
            query.Add($"game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}");
        }

        if (!string.IsNullOrEmpty(loader))
        {
            query.Add($"loaders={Uri.EscapeDataString($"[\"{loader}\"]")}");
        }

        var url = $"{BaseUrl}/project/{projectId}/version";

        if (query.Count > 0)
        {
            url += "?" + string.Join("&", query);
        }

        return ParseVersions(await GetAsync(url, cancellationToken));
    }

    public async Task<ModVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken = default)
    {
        var list = ParseVersions(await GetAsync($"{BaseUrl}/version/{versionId}", cancellationToken));

        return list.FirstOrDefault()
               ?? throw new InvalidOperationException($"Modrinth 里找不到版本 {versionId}。");
    }

    /// <summary>
    /// 按 sha1 批量反查"这个文件是哪个项目的哪个版本"。
    /// <para>
    /// 这是内容索引的关键一步：用户手动塞进 mods 的 jar 也能被认出来，
    /// 不需要本地先记录来源。Modrinth 用的是 sha1（不是 sha256）。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, FileSource>> GetFilesByHashesAsync(
        IReadOnlyCollection<string> sha1s,
        string? projectType = null,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, FileSource>(StringComparer.OrdinalIgnoreCase);

        if (sha1s.Count == 0)
        {
            return result;
        }

        // 一次别塞太多，分批（每批 500 个足够，283 个 jar 也就一批）
        foreach (var batch in sha1s.Chunk(500))
        {
            var body = new
            {
                hashes = batch,
                algorithm = "sha1",
            };

            var raw = await PostAsync($"{BaseUrl}/version_files", body, cancellationToken);

            using var document = JsonDocument.Parse(raw);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var version = property.Value;
                var projectId = version.TryGetProperty("project_id", out var pid) ? pid.GetString() : null;
                var versionId = version.TryGetProperty("id", out var vid) ? vid.GetString() : null;
                var versionNumber = version.TryGetProperty("version_number", out var num) ? num.GetString() : null;

                if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(versionId))
                {
                    continue;
                }

                result[property.Name] = new FileSource(projectId, versionId, projectType, versionNumber);
            }
        }

        return result;
    }

    /// <summary>
    /// 批量查更新：给一批已装文件的 sha1，返回"有新版本的文件"及目标版本。
    /// <para>
    /// 对应 <c>POST /v2/version_files/update_many</c>。loaders / game_versions 决定哪些版本算"可用"，
    /// 所以这两个参数必须来自实例本身（它的 MC 版本与加载器）。
    /// </para>
    /// <para>
    /// 注意返回格式：这个端点是 <c>{ "sha1": [版本, 版本, …] }</c>（**数组**，可能含多个渠道的候选），
    /// 而 <c>/version_files</c> 是 <c>{ "sha1": 版本 }</c>（单个对象）。两者不一样，别搞混。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ModVersion>> CheckUpdatesAsync(
        IReadOnlyCollection<string> sha1s,
        IReadOnlyCollection<string> loaders,
        IReadOnlyCollection<string> gameVersions,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ModVersion>(StringComparer.OrdinalIgnoreCase);

        if (sha1s.Count == 0)
        {
            return result;
        }

        foreach (var batch in sha1s.Chunk(500))
        {
            var body = new
            {
                hashes = batch,
                algorithm = "sha1",
                loaders,
                game_versions = gameVersions,
            };

            var raw = await PostAsync($"{BaseUrl}/version_files/update_many", body, cancellationToken);

            using var document = JsonDocument.Parse(raw);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // 数组（update_many）或对象（个别情况）都接住
                if (property.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
                {
                    continue;
                }

                var candidates = ParseVersions(property.Value.GetRawText());
                if (candidates.Count == 0)
                {
                    continue;
                }

                // Modrinth 后端已按 loader/game_version/渠道过滤并排好序，取第一个即可
                result[property.Name] = candidates[0];
            }
        }

        return result;
    }

    /// <summary>
    /// 按 sha1 批量取完整版本对象（含 dependencies / files / game_versions）。
    /// 冲突诊断要用依赖信息，所以不能只挑 project_id 那几个字段。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ModVersion>> GetVersionsByHashesAsync(
        IReadOnlyCollection<string> sha1s,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ModVersion>(StringComparer.OrdinalIgnoreCase);

        if (sha1s.Count == 0)
        {
            return result;
        }

        foreach (var batch in sha1s.Chunk(500))
        {
            var body = new
            {
                hashes = batch,
                algorithm = "sha1",
            };

            var raw = await PostAsync($"{BaseUrl}/version_files", body, cancellationToken);

            using var document = JsonDocument.Parse(raw);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var parsed = ParseVersions(property.Value.GetRawText());
                if (parsed.Count > 0)
                {
                    result[property.Name] = parsed[0];
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 按项目 ID 批量取项目摘要（标题 + 支持的加载器）。
    /// 加载器用来判断"这个依赖到底该不该装在本实例上"——
    /// Forge 实例里缺一个纯 Fabric 项目，通常是兼容层的事，不算真缺。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ProjectSummary>> GetProjectSummariesAsync(
        IReadOnlyCollection<string> projectIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ProjectSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in projectIds.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(100))
        {
            var ids = "[" + string.Join(",", batch.Select(id => $"\"{id}\"")) + "]";
            var url = $"{BaseUrl}/projects?ids={Uri.EscapeDataString(ids)}";

            try
            {
                var raw = await GetAsync(url, cancellationToken);

                using var document = JsonDocument.Parse(raw);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var project in document.RootElement.EnumerateArray())
                {
                    var id = project.TryGetProperty("id", out var pid) ? pid.GetString() : null;
                    var title = project.TryGetProperty("title", out var t) ? t.GetString() : null;

                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    result[id] = new ProjectSummary(
                        title ?? id,
                        project.TryGetProperty("loaders", out var loaders) &&
                        loaders.ValueKind == JsonValueKind.Array
                            ? loaders.EnumerateArray().Select(l => l.GetString() ?? string.Empty)
                                .Where(l => l.Length > 0).ToList()
                            : []);
                }
            }
            catch
            {
                // 查不到不影响诊断本身，退回显示 ID
            }
        }

        return result;
    }

    private async Task<string> PostAsync(string url, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8,
                "application/json"),
        };

        request.Headers.TryAddWithoutValidation("User-Agent", "Songyu-MCloader/0.4 (independent launcher)");

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Modrinth 要求带一个能识别客户端的 User-Agent
        request.Headers.TryAddWithoutValidation("User-Agent", "Songyu-MCloader/0.4 (independent launcher)");

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static List<ModVersion> ParseVersions(string raw)
    {
        using var document = JsonDocument.Parse(raw);

        // /version/{id} 返回单个对象，其余接口返回数组，统一成数组处理
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToList()
            : [document.RootElement];

        var result = new List<ModVersion>();

        foreach (var item in elements)
        {
            var files = new List<ModFile>();
            if (item.TryGetProperty("files", out var fileArray))
            {
                foreach (var file in fileArray.EnumerateArray())
                {
                    files.Add(new ModFile(
                        file.GetProperty("url").GetString() ?? string.Empty,
                        file.GetProperty("filename").GetString() ?? string.Empty,
                        file.TryGetProperty("hashes", out var hashes) &&
                        hashes.TryGetProperty("sha1", out var sha1)
                            ? sha1.GetString()
                            : null,
                        file.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
                }
            }

            var dependencies = new List<ModDependency>();
            if (item.TryGetProperty("dependencies", out var depArray))
            {
                foreach (var dep in depArray.EnumerateArray())
                {
                    dependencies.Add(new ModDependency(
                        dep.TryGetProperty("version_id", out var vid) ? vid.GetString() : null,
                        dep.TryGetProperty("project_id", out var pid) ? pid.GetString() : null,
                        dep.TryGetProperty("dependency_type", out var type) ? type.GetString() ?? "required" : "required"));
                }
            }

            result.Add(new ModVersion(
                item.GetProperty("id").GetString() ?? string.Empty,
                item.GetProperty("project_id").GetString() ?? string.Empty,
                item.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("version_number", out var number) ? number.GetString() ?? string.Empty : string.Empty,
                files,
                dependencies,
                ReadStringArray(item, "game_versions"),
                ReadStringArray(item, "loaders")));
        }

        return result;
    }

    private static List<string> ReadStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array))
        {
            return [];
        }

        return array.EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .Where(s => s.Length > 0)
            .ToList();
    }
}
