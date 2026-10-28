using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Songyu.App;

/// <summary>
/// 从正版皮肤图里裁出头部做头像。
/// <para>
/// 皮肤是 64×64 的 PNG，头部正面固定在 (8,8) 的 8×8 方块。
/// 放大用最近邻（在 XAML 里设 <c>BitmapScalingMode="NearestNeighbor"</c>），
/// 否则像素画会被插值糊掉。
/// </para>
/// </summary>
public static class SkinHelper
{
    public static async Task<ImageSource?> LoadAvatarAsync(
        HttpClient http,
        string? skinUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skinUrl))
        {
            return null;
        }

        try
        {
            var bytes = await http.GetByteArrayAsync(skinUrl, cancellationToken);
            return CropHead(bytes);
        }
        catch
        {
            // 皮肤拿不到就显示占位，不影响启动
            return null;
        }
    }

    public static ImageSource? CropHead(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes);

        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        var skin = decoder.Frames[0];

        // 老皮肤是 64×32，头部位置一样
        if (skin.PixelWidth < 16 || skin.PixelHeight < 16)
        {
            return null;
        }

        var head = new CroppedBitmap(skin, new Int32Rect(8, 8, 8, 8));

        head.Freeze();
        return head;
    }

    /// <summary>按皮肤贴图坐标拼出正面角色，供首页在离线状态下直接预览。</summary>
    public static ImageSource RenderFront(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        var modern = frame.PixelHeight >= 64;
        var source = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var sourceStride = source.PixelWidth * 4;
        var sourcePixels = new byte[sourceStride * source.PixelHeight];
        source.CopyPixels(sourcePixels, sourceStride, 0);
        var pixels = new byte[16 * 32 * 4];

        void Part(int sourceX, int sourceY, int width, int height, int targetX, int targetY)
        {
            if (sourceX + width > source.PixelWidth || sourceY + height > source.PixelHeight) { return; }
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var from = (sourceY + y) * sourceStride + (sourceX + x) * 4;
                var to = ((targetY + y) * 16 + targetX + x) * 4;
                var alpha = sourcePixels[from + 3] / 255.0;
                var previous = pixels[to + 3] / 255.0;
                var combined = alpha + previous * (1 - alpha);
                if (combined <= 0) { continue; }
                for (var channel = 0; channel < 3; channel++)
                {
                    pixels[to + channel] = (byte)Math.Round(
                        (sourcePixels[from + channel] * alpha +
                         pixels[to + channel] * previous * (1 - alpha)) / combined);
                }
                pixels[to + 3] = (byte)Math.Round(combined * 255);
            }
        }

        Part(8, 8, 8, 8, 4, 0);
        Part(20, 20, 8, 12, 4, 8);
        Part(44, 20, 4, 12, 0, 8);
        Part(modern ? 36 : 44, modern ? 52 : 20, 4, 12, 12, 8);
        Part(4, 20, 4, 12, 4, 20);
        Part(modern ? 20 : 4, modern ? 52 : 20, 4, 12, 8, 20);
        if (modern)
        {
            Part(40, 8, 8, 8, 4, 0);
            Part(20, 36, 8, 12, 4, 8);
            Part(44, 36, 4, 12, 0, 8);
            Part(52, 52, 4, 12, 12, 8);
            Part(4, 36, 4, 12, 4, 20);
            Part(4, 52, 4, 12, 8, 20);
        }

        var bitmap = BitmapSource.Create(16, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static ImageSource? RenderCape(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        if (frame.PixelWidth < 12 || frame.PixelHeight < 17) { return null; }
        var crop = new CroppedBitmap(frame, new Int32Rect(1, 1, 10, 16));
        crop.Freeze();
        return crop;
    }
}
