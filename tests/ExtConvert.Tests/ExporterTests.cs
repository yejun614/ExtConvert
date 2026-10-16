using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ExtConvert.Conversion;
using Xunit;

namespace ExtConvert.Tests;

public class ExporterTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>불투명한 빨간색으로 채운 width×height 비트맵을 PNG로 왕복시켜 만든다.</summary>
    private static Bitmap CreateSolidBitmap(int width, int height)
    {
        using var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            var row = new byte[fb.RowBytes];
            for (var x = 0; x < width; x++)
            {
                row[x * 4 + 0] = 0;   // B
                row[x * 4 + 1] = 0;   // G
                row[x * 4 + 2] = 255; // R
                row[x * 4 + 3] = 255; // A
            }
            for (var y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }

        using var ms = new MemoryStream();
        wb.Save(ms, PngBitmapEncoderOptions.Default);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    private static byte[] ReadPixel(Bitmap bitmap, int x, int y)
    {
        var buffer = new byte[4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4);
        }
        finally
        {
            handle.Free();
        }
        return buffer;
    }

    [AvaloniaFact]
    public void Ico_ContainsPngEntryForEachRequestedSize()
    {
        using var source = CreateSolidBitmap(300, 200);
        int[] sizes = [16, 32, 48, 256];

        using var ms = new MemoryStream();
        new IcoExporter().Export(source, ms, new ExportOptions { IcoSizes = sizes });
        var bytes = ms.ToArray();

        using var reader = new BinaryReader(new MemoryStream(bytes));
        Assert.Equal(0, reader.ReadUInt16()); // reserved
        Assert.Equal(1, reader.ReadUInt16()); // type = icon
        Assert.Equal(sizes.Length, reader.ReadUInt16());

        foreach (var expected in sizes)
        {
            int w = reader.ReadByte(), h = reader.ReadByte();
            reader.ReadBytes(6); // colors, reserved, planes, bpp
            var length = reader.ReadInt32();
            var offset = reader.ReadInt32();

            Assert.Equal(expected == 256 ? 0 : expected, w);
            Assert.Equal(expected == 256 ? 0 : expected, h);
            Assert.True(offset + length <= bytes.Length);

            var data = bytes.AsSpan(offset, length).ToArray();
            Assert.Equal(PngSignature, data[..8]);

            using var frame = new Bitmap(new MemoryStream(data));
            Assert.Equal(new PixelSize(expected, expected), frame.PixelSize);
        }
    }

    [AvaloniaFact]
    public void Ico_NonSquareSourceIsPaddedWithTransparency()
    {
        using var source = CreateSolidBitmap(300, 100);

        using var ms = new MemoryStream();
        new IcoExporter().Export(source, ms, new ExportOptions { IcoSizes = [48] });
        var bytes = ms.ToArray();
        var offset = BitConverter.ToInt32(bytes, 6 + 12);
        var length = BitConverter.ToInt32(bytes, 6 + 8);

        using var frame = new Bitmap(new MemoryStream(bytes, offset, length));
        Assert.Equal(0, ReadPixel(frame, 24, 0)[3]);    // 위쪽 여백: 투명
        Assert.Equal(255, ReadPixel(frame, 24, 24)[3]); // 가운데: 원본
    }

    [AvaloniaFact]
    public void Ico_CanBeDecodedForPreview()
    {
        using var source = CreateSolidBitmap(64, 64);
        using var ms = new MemoryStream();
        new IcoExporter().Export(source, ms, new ExportOptions());
        ms.Position = 0;

        using var decoded = new Bitmap(ms);
        Assert.True(decoded.PixelSize.Width >= 16);
    }

    [AvaloniaFact]
    public void Jpeg_ProducesDecodableImageOfSameSize()
    {
        using var source = CreateSolidBitmap(120, 80);
        using var ms = new MemoryStream();
        new JpegExporter().Export(source, ms, new ExportOptions());
        var bytes = ms.ToArray();

        Assert.Equal([0xFF, 0xD8], bytes[..2]); // JPEG SOI
        using var decoded = new Bitmap(new MemoryStream(bytes));
        Assert.Equal(new PixelSize(120, 80), decoded.PixelSize);
    }

    [Theory]
    [InlineData(".ico", typeof(IcoExporter))]
    [InlineData("PNG", typeof(PngExporter))]
    [InlineData(".jpeg", typeof(JpegExporter))]
    [InlineData(".jpg", typeof(JpegExporter))]
    [InlineData(".MP3", typeof(ExtConvert.Media.Mp3Exporter))]
    public void FindByExtension_ResolvesExporter(string extension, Type expected) =>
        Assert.IsType(expected, Exporters.Images.Concat<IExporter>(Exporters.Media).FindByExtension(extension));
}
