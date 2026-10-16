using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ExtConvert.Conversion;

internal static class ImageOps
{
    /// <summary>
    /// <paramref name="source"/>를 width×height 캔버스에 비율을 유지한 채 가운데 맞춰 그린다.
    /// 남는 영역과 투명 영역은 <paramref name="background"/>(기본: 투명)로 채워진다.
    /// </summary>
    public static RenderTargetBitmap RenderFit(Bitmap source, int width, int height, Color? background = null)
    {
        var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));

        var src = source.PixelSize;
        var scale = Math.Min((double)width / src.Width, (double)height / src.Height);
        var w = src.Width * scale;
        var h = src.Height * scale;
        var dest = new Rect((width - w) / 2, (height - h) / 2, w, h);

        using (var ctx = target.CreateDrawingContext())
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        {
            if (background is { } bg)
                ctx.FillRectangle(new SolidColorBrush(bg), new Rect(0, 0, width, height));
            ctx.DrawImage(source, new Rect(source.Size), dest);
        }

        return target;
    }
}
