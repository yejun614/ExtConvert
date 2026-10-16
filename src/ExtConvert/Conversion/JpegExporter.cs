using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ExtConvert.Conversion;

/// <summary>JPEG는 투명도를 지원하지 않으므로 흰 배경 위에 합성해서 저장한다.</summary>
public sealed class JpegExporter : IImageExporter
{
    public string DisplayName => "JPEG 이미지";
    public string Extension => "jpg";

    public void Export(Bitmap source, Stream output, ExportOptions options)
    {
        var size = source.PixelSize;
        using var flattened = ImageOps.RenderFit(source, size.Width, size.Height, Colors.White);
        flattened.Save(output, new JpegBitmapEncoderOptions { Quality = 92 });
    }
}
