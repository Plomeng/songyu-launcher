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

    private static ImageSource? CropHead(byte[] pngBytes)
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
}
