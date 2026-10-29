using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ExtConvert.Conversion;

/// <summary>
/// 무압축 BMP. 불투명한 이미지는 호환성이 가장 좋은 24비트로,
/// 투명 영역이 있으면 알파 채널을 담은 32비트(BITMAPV4HEADER)로 저장한다.
/// </summary>
public sealed class BmpExporter : IImageExporter
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int V4HeaderSize = 108;

    public string DisplayName => "BMP 이미지";
    public string Extension => "bmp";

    public Task ExportAsync(Bitmap source, Stream output, ExportOptions options, CancellationToken ct = default)
    {
        Export(source, output, options);
        return Task.CompletedTask;
    }

    public void Export(Bitmap source, Stream output, ExportOptions options)
    {
        var (width, height) = (source.PixelSize.Width, source.PixelSize.Height);
        var bgra = ReadBgra(source);

        var hasAlpha = false;
        for (var i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 255) { hasAlpha = true; break; }

        var bytesPerPixel = hasAlpha ? 4 : 3;
        var stride = (width * bytesPerPixel + 3) & ~3; // 행은 4바이트 단위로 맞춘다
        var headerSize = hasAlpha ? V4HeaderSize : InfoHeaderSize;
        var dataOffset = FileHeaderSize + headerSize;

        using var w = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);

        // BITMAPFILEHEADER
        w.Write((byte)'B');
        w.Write((byte)'M');
        w.Write(dataOffset + stride * height); // file size
        w.Write(0);                            // reserved
        w.Write(dataOffset);

        // BITMAPINFOHEADER (V4는 뒤에 색 마스크 등을 이어 붙인다)
        w.Write(headerSize);
        w.Write(width);
        w.Write(height);                       // 양수 = 아래쪽 행부터 저장
        w.Write((ushort)1);                    // planes
        w.Write((ushort)(bytesPerPixel * 8));
        w.Write(hasAlpha ? 3 : 0);             // BI_BITFIELDS : BI_RGB
        w.Write(stride * height);
        w.Write(3780);                         // 96 DPI (pixels per meter)
        w.Write(3780);
        w.Write(0);                            // palette colors
        w.Write(0);                            // important colors
        if (hasAlpha)
        {
            w.Write(0x00FF0000u);              // red mask
            w.Write(0x0000FF00u);              // green mask
            w.Write(0x000000FFu);              // blue mask
            w.Write(0xFF000000u);              // alpha mask
            w.Write(0x73524742u);              // LCS_sRGB ("sRGB")
            w.Write(new byte[36 + 12]);        // endpoints + gamma (sRGB에서는 무시됨)
        }

        var row = new byte[stride];
        for (var y = height - 1; y >= 0; y--)
        {
            var src = bgra.AsSpan(y * width * 4, width * 4);
            if (hasAlpha)
                src.CopyTo(row);
            else
                for (var x = 0; x < width; x++)
                    src.Slice(x * 4, 3).CopyTo(row.AsSpan(x * 3));
            w.Write(row);
        }
    }

    /// <summary>원본의 픽셀 형식과 관계없이 straight-alpha BGRA 바이트 배열로 읽는다.</summary>
    internal static byte[] ReadBgra(Bitmap source)
    {
        var size = source.PixelSize;
        using var wb = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = wb.Lock();
        source.CopyPixels(fb); // 대상 버퍼 형식(BGRA, straight alpha)으로 변환된다

        var pixels = new byte[size.Width * size.Height * 4];
        for (var y = 0; y < size.Height; y++)
            Marshal.Copy(fb.Address + y * fb.RowBytes, pixels, y * size.Width * 4, size.Width * 4);
        return pixels;
    }
}
