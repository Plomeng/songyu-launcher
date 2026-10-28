using System.Diagnostics;

namespace Songyu.Core.Download;

/// <summary>下载进度快照。</summary>
public sealed record DownloadProgress(
    long Received,
    long? Total,
    double BytesPerSecond,
    string? CurrentFile)
{
    public double? Percent => Total is > 0 ? (double)Received / Total.Value : null;

    public TimeSpan? Remaining
    {
        get
        {
            if (Total is not > 0 || BytesPerSecond <= 1)
            {
                return null;
            }

            var left = (Total.Value - Received) / BytesPerSecond;
            return left <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(left);
        }
    }
}

/// <summary>
/// 滑动窗口测速。每收到一批字节记一次采样，取最近 2 秒的差值算瞬时速度。
/// <para>
/// 必须加锁：并行下载时多个线程会同时 <see cref="Add"/> 并在 UI 线程读
/// <see cref="BytesPerSecond"/>，队列本身不是线程安全的。
/// 用「总字节 / 总耗时」会在中断重试后严重失真，所以坚持用窗口。
/// </para>
/// </summary>
public sealed class SpeedMeter
{
    private const int WindowSeconds = 2;

    private readonly object _lock = new();
    private readonly Queue<(long ElapsedMs, long Bytes)> _samples = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _bytes;
    private long _lastSampleAt;

    public long TotalBytes => Interlocked.Read(ref _bytes);

    public void Add(long bytes)
    {
        Interlocked.Add(ref _bytes, bytes);

        var now = _clock.ElapsedMilliseconds;
        // 采样不必每个数据块都记，10ms 一次足够，否则队列会被挤爆。
        if (now - Interlocked.Read(ref _lastSampleAt) < 10)
        {
            return;
        }

        Interlocked.Exchange(ref _lastSampleAt, now);
        var total = TotalBytes;

        lock (_lock)
        {
            _samples.Enqueue((now, total));
            TrimLocked(now);
        }
    }

    public double BytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                var now = _clock.ElapsedMilliseconds;
                TrimLocked(now);

                if (_samples.Count < 2)
                {
                    return 0;
                }

                var (firstMs, firstBytes) = _samples.Peek();
                var (lastMs, lastBytes) = _samples.Last();
                var seconds = (lastMs - firstMs) / 1000.0;
                return seconds <= 0.001 ? 0 : (lastBytes - firstBytes) / seconds;
            }
        }
    }

    private void TrimLocked(long now)
    {
        var threshold = now - WindowSeconds * 1000;
        while (_samples.Count > 2 && _samples.Peek().ElapsedMs < threshold)
        {
            _samples.Dequeue();
        }
    }

    public static string FormatSpeed(double bytesPerSecond) => bytesPerSecond switch
    {
        <= 0 => "-",
        < 1024 => $"{bytesPerSecond:F0} B/s",
        < 1024 * 1024 => $"{bytesPerSecond / 1024:F1} KB/s",
        _ => $"{bytesPerSecond / 1024 / 1024:F2} MB/s",
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1024.0 / 1024.0:F1} MB",
    };
}
