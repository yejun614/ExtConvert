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
            // 원본 사각형은 픽셀 단위 (source.Size는 DIP라 96 DPI가 아니면 크기가 달라짐)
            ctx.DrawImage(source, new Rect(0, 0, src.Width, src.Height), dest);
        }

        return target;
    }

    /// <summary>원본에서 <paramref name="area"/>(픽셀)만 잘라낸 새 비트맵. 1:1 복사라 보간하지 않는다.</summary>
    public static RenderTargetBitmap Crop(Bitmap source, PixelRect area)
    {
        var target = new RenderTargetBitmap(area.Size, new Vector(96, 96));
        using (var ctx = target.CreateDrawingContext())
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            // 원본 사각형은 이미지 DPI와 관계없이 픽셀 단위
            ctx.DrawImage(source,
                new Rect(area.X, area.Y, area.Width, area.Height),
                new Rect(0, 0, area.Width, area.Height));
        }
        return target;
    }
}
