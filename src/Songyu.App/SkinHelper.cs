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

        // 放大到 64×64，交给 Image 控件的 NearestNeighbor 保持像素感
        var scaled = new TransformedBitmap(head, new ScaleTransform(8, 8));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>按皮肤贴图坐标拼出正面角色，供首页在离线状态下直接预览。</summary>
    public static ImageSource RenderFront(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames[0];
        var modern = frame.PixelHeight >= 64;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (var drawing = visual.RenderOpen())
        {
            void Part(int sourceX, int sourceY, int width, int height, int targetX, int targetY)
            {
                if (sourceX + width > frame.PixelWidth || sourceY + height > frame.PixelHeight) { return; }
                var crop = new CroppedBitmap(frame, new Int32Rect(sourceX, sourceY, width, height));
                drawing.DrawImage(crop, new Rect(targetX * 8, targetY * 8, width * 8, height * 8));
            }

            Part(8, 8, 8, 8, 4, 0);       // 头
            Part(20, 20, 8, 12, 4, 8);   // 身体
            Part(44, 20, 4, 12, 0, 8);   // 右手
            Part(modern ? 36 : 44, modern ? 52 : 20, 4, 12, 12, 8);
            Part(4, 20, 4, 12, 4, 20);   // 右腿
            Part(modern ? 20 : 4, modern ? 52 : 20, 4, 12, 8, 20);

            if (modern)
            {
                Part(40, 8, 8, 8, 4, 0);    // 帽子层
                Part(20, 36, 8, 12, 4, 8);  // 外套层
                Part(44, 36, 4, 12, 0, 8);  // 右袖
                Part(52, 52, 4, 12, 12, 8); // 左袖
                Part(4, 36, 4, 12, 4, 20);  // 右裤腿
                Part(4, 52, 4, 12, 8, 20);  // 左裤腿
            }
        }

        var bitmap = new RenderTargetBitmap(128, 256, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
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
        var scaled = new TransformedBitmap(crop, new ScaleTransform(8, 8));
        scaled.Freeze();
        return scaled;
    }
}
