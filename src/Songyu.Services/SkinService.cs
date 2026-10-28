using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Songyu.Services;

/// <summary>一张皮肤的信息。</summary>
public sealed record SkinInfo(
    byte[] Png,
    int Width,
    int Height,
    bool IsSlim,
    string Source)
{
    /// <summary>64x64 皮肤才有独立的手臂/腿纹理；旧版 64x32 只有上半身。</summary>
    public bool IsModern => Height >= 64;

    public string SizeText => $"{Width}×{Height}";
}

/// <summary>
/// 皮肤的读取、下载与保存。
/// <para>
/// 皮肤永远是 64 的倍数（64×64 现代格式、64×32 旧格式），手臂细长模型
/// 靠 <c>IsSlim</c> 区分——判定办法就是看那个「是否为 Alex 模型」的 alpha 像素，
/// 或者更简单：用调用方选择的模型类型。
/// </para>
/// </summary>
public sealed class SkinService(MinecraftPaths paths, HttpClient http)
{
    /// <summary>本地皮肤库目录（导入过的皮肤存这儿，与实例无关）。</summary>
    public string SkinsDirectory => Path.Combine(paths.Root, "skins");

    public async Task<SkinInfo> LoadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return Parse(bytes, path);
    }

    /// <summary>从 URL 取皮肤（正版账号的 textures.skin.url 就是这种直链）。</summary>
    public async Task<SkinInfo> DownloadAsync(string url, CancellationToken cancellationToken = default)
    {
        var bytes = await http.GetByteArrayAsync(url, cancellationToken);
        return Parse(bytes, url);
    }

    /// <summary>存进本地皮肤库，返回落盘路径。</summary>
    public async Task<string> SaveAsync(
        byte[] png,
        string name,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(SkinsDirectory);

        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(ch, '_');
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "skin";
        }

        var path = Path.Combine(SkinsDirectory, name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".png");

        await File.WriteAllBytesAsync(path, png, cancellationToken);
        return path;
    }

    /// <summary>皮肤库里的历史皮肤，按修改时间倒序。</summary>
    public IReadOnlyList<string> ListLibrary()
    {
        if (!Directory.Exists(SkinsDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(SkinsDirectory, "*.png")
            .OrderByDescending(File.GetLastWriteTime)
            .ToList();
    }

    private static SkinInfo Parse(byte[] bytes, string source)
    {
        var image = DecodeImage(bytes);

        var width = image.PixelWidth;
        var height = image.PixelHeight;

        if (width % 64 != 0 || height is not (32 or 64))
        {
            throw new InvalidDataException(
                $"尺寸不对：{width}×{height}。皮肤必须是 64×64（现代）或 64×32（旧版）。");
        }

        return new SkinInfo(bytes, width, height, DetectSlim(bytes, width, height), source);
    }

    private static BitmapSource DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();

        return image;
    }

    /// <summary>
    /// 判定细手臂（Alex）模型：看 (54,20) 这个像素的透明度。
    /// 这是社区通行的判定位置——classic 模型那里是实心，slim 模型是透明的。
    /// </summary>
    private static bool DetectSlim(byte[] png, int width, int height)
    {
        if (height < 64)
        {
            return false;
        }

        try
        {
            var image = DecodeImage(png);
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            var x = 54;
            var y = 20;
            var index = y * stride + x * 4;
            var alpha = pixels[index + 3];

            return alpha == 0;
        }
        catch
        {
            return false;
        }
    }
}
