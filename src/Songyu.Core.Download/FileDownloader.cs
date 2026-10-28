using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Songyu.Core.Download;

public enum DownloadOutcome
{
    Skipped,
    Downloaded,
}

public sealed class DownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 资源下载器。M2 的三件事：多源回退、断点续传、速度统计。
/// <para>
/// 候选地址的顺序是「镜像优先，官方兜底」——镜像来自用户配置，
/// 程序自身不内置任何第三方地址。某个源彻底失败才换下一个。
/// </para>
/// </summary>
public sealed class FileDownloader(HttpClient http, IReadOnlyList<IDownloadSource>? sources = null)
{
    private const int AttemptsPerSource = 3;

    private readonly IReadOnlyList<IDownloadSource> _sources = sources ?? [new OfficialSource()];

    /// <summary>诊断回调。只在续传、换源这类关键事件触发，避免刷屏。</summary>
    public Action<string>? Trace { get; init; }

    public async Task<DownloadOutcome> DownloadAsync(
        ResourceSpec spec,
        SpeedMeter? meter = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (await IsValidAsync(spec, cancellationToken))
        {
            return DownloadOutcome.Skipped;
        }

        var directory = Path.GetDirectoryName(spec.TargetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var partPath = spec.TargetPath + ".part";
        Exception? last = null;

        foreach (var url in BuildCandidates(spec))
        {
            for (var attempt = 1; attempt <= AttemptsPerSource; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await TransferAsync(url, spec, partPath, meter, progress, cancellationToken);

                    if (spec.Sha1 is not null &&
                        !await Sha1Helper.MatchesAsync(partPath, spec.Sha1, cancellationToken))
                    {
                        // 内容坏了，留着只会反复失败，删掉从头来。
                        TryDelete(partPath);
                        throw new InvalidDataException($"SHA1 校验失败：{url}");
                    }

                    File.Move(partPath, spec.TargetPath, overwrite: true);
                    return DownloadOutcome.Downloaded;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (!IsTransient(ex))
                    {
                        break; // 该源无望，换下一个
                    }

                    await Task.Delay(200 * attempt, cancellationToken);
                }
            }
        }

        throw new DownloadException($"所有下载源均失败：{spec.DisplayName}", last);
    }

    private async Task TransferAsync(
        Uri url,
        ResourceSpec spec,
        string partPath,
        SpeedMeter? meter,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        if (existing > 0)
        {
            Trace?.Invoke($"续传 {spec.DisplayName}：从 {SpeedMeter.FormatSize(existing)} 处继续");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            // 服务端认为已经取完，说明 .part 其实是完整的。
            return;
        }

        response.EnsureSuccessStatusCode();

        var resuming = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resuming)
        {
            existing = 0;
        }

        var total = response.Content.Headers.ContentLength is { } contentLength
            ? contentLength + existing
            : (long?)null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(
            partPath,
            resuming ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        var buffer = new byte[81920];
        long received = existing;
        var lastReportAt = 0L;

        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            meter?.Add(read);

            var now = Environment.TickCount64;
            if (now - lastReportAt >= 250)
            {
                lastReportAt = now;
                progress?.Report(new DownloadProgress(
                    received, total, meter?.BytesPerSecond ?? 0, spec.DisplayName));
            }
        }

        progress?.Report(new DownloadProgress(received, total, meter?.BytesPerSecond ?? 0, spec.DisplayName));
    }

    private IEnumerable<Uri> BuildCandidates(ResourceSpec spec)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in _sources.OrderBy(s => s.Priority))
        {
            if (source.Resolve(spec.Kind, spec.RelativePath) is { } uri && seen.Add(uri.ToString()))
            {
                yield return uri;
            }
        }

        if (Uri.TryCreate(spec.OfficialUrl, UriKind.Absolute, out var official) && seen.Add(official.ToString()))
        {
            yield return official;
        }
    }

    private static async Task<bool> IsValidAsync(ResourceSpec spec, CancellationToken cancellationToken)
    {
        if (!File.Exists(spec.TargetPath))
        {
            return false;
        }

        if (spec.Sha1 is null)
        {
            return true;
        }

        return await Sha1Helper.MatchesAsync(spec.TargetPath, spec.Sha1, cancellationToken);
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException => true,
        IOException => true,
        // OperationCanceledException 的派生类（含 TaskCanceledException，即超时）一并覆盖
        OperationCanceledException => true,
        InvalidDataException => true,
        _ => false,
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响后续
        }
    }
}

public static class Sha1Helper
{
    public static async Task<string> ComputeAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    public static async Task<bool> MatchesAsync(string path, string expected, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var actual = await ComputeAsync(path, cancellationToken);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
