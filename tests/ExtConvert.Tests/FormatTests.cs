using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ExtConvert.Conversion;
using ExtConvert.Media;
using Xunit;

namespace ExtConvert.Tests;

internal static class TestImages
{
    /// <summary>빨간색 이미지. <paramref name="transparentLeft"/>면 왼쪽 절반이 완전 투명.</summary>
    public static Bitmap Create(int width, int height, bool transparentLeft = false)
    {
        using var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            var row = new byte[fb.RowBytes];
            for (var x = 0; x < width; x++)
            {
                var opaque = !transparentLeft || x >= width / 2;
                row[x * 4 + 2] = (byte)(opaque ? 255 : 0); // R (premultiplied)
                row[x * 4 + 3] = (byte)(opaque ? 255 : 0); // A
            }
            for (var y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        }

        using var ms = new MemoryStream();
        wb.Save(ms, PngBitmapEncoderOptions.Default);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    public static byte[] Pixel(Bitmap bitmap, int x, int y)
    {
        var bgra = BmpExporter.ReadBgra(bitmap);
        return bgra.AsSpan((y * bitmap.PixelSize.Width + x) * 4, 4).ToArray();
    }
}

public class BmpTests
{
    [AvaloniaFact]
    public void Bmp_OpaqueImageIs24BitAndDecodable()
    {
        using var source = TestImages.Create(33, 20); // 33*3 = 99 → 행 패딩 필요
        using var ms = new MemoryStream();
        new BmpExporter().Export(source, ms, new ExportOptions());
        var bytes = ms.ToArray();

        Assert.Equal((byte)'B', bytes[0]);
        Assert.Equal((byte)'M', bytes[1]);
        Assert.Equal(24, BitConverter.ToUInt16(bytes, 28));
        Assert.Equal(bytes.Length, BitConverter.ToInt32(bytes, 2));

        using var decoded = new Bitmap(new MemoryStream(bytes));
        Assert.Equal(new PixelSize(33, 20), decoded.PixelSize);
        Assert.Equal([0, 0, 255, 255], TestImages.Pixel(decoded, 5, 5));
    }

    [AvaloniaFact]
    public void Bmp_TransparentImageKeepsAlpha()
    {
        using var source = TestImages.Create(40, 10, transparentLeft: true);
        using var ms = new MemoryStream();
        new BmpExporter().Export(source, ms, new ExportOptions());
        var bytes = ms.ToArray();

        Assert.Equal(32, BitConverter.ToUInt16(bytes, 28));
        using var decoded = new Bitmap(new MemoryStream(bytes));
        Assert.Equal(0, TestImages.Pixel(decoded, 2, 5)[3]);
        Assert.Equal(255, TestImages.Pixel(decoded, 30, 5)[3]);
    }
}

/// <summary>ffmpeg 기반 이미지 형식: 저장한 파일을 다시 읽어 크기가 유지되는지 확인한다.</summary>
public class FFmpegImageTests
{
    [AvaloniaFact]
    public async Task AllAvailableImageFormats_RoundTrip()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");
        var dir = Directory.CreateTempSubdirectory("ExtConvertTests_").FullName;
        using var source = TestImages.Create(64, 48, transparentLeft: true);
        var failures = new List<string>();

        try
        {
            foreach (var exporter in Exporters.Images.Where(e => e.IsAvailable))
            {
                var path = Path.Combine(dir, "out." + exporter.Extension);
                try
                {
                    await using (var file = File.Create(path))
                        await exporter.ExportAsync(source, file, new ExportOptions(), TestContext.Current.CancellationToken);

                    // 저장한 형식을 앱이 다시 열 수 있어야 한다 (ICO는 가장 큰 크기 = 256)
                    var ext = exporter.Extension;
                    using var decoded = Exporters.NativeImageExtensions.Contains(ext)
                        ? new Bitmap(path)
                        : new Bitmap(new MemoryStream(await FFmpeg.DecodeImagePngAsync(path, TestContext.Current.CancellationToken)));
                    var expected = ext == "ico" ? new PixelSize(256, 256) : new PixelSize(64, 48);
                    if (decoded.PixelSize != expected)
                        failures.Add($"{ext}: 크기 {decoded.PixelSize}");
                }
                catch (Exception ex)
                {
                    failures.Add($"{exporter.Extension}: {ex.Message}");
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        Assert.Empty(failures);
    }

    [AvaloniaFact]
    public async Task FormatWithoutAlpha_IsFlattenedOntoWhite()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable && FFmpeg.HasEncoder("pcx"), "ffmpeg pcx 인코더가 없습니다.");
        using var source = TestImages.Create(20, 10, transparentLeft: true);
        using var ms = new MemoryStream();
        await Exporters.Images.FindByExtension("pcx")!.ExportAsync(source, ms, new ExportOptions(),
            TestContext.Current.CancellationToken);

        var path = Path.Combine(Path.GetTempPath(), $"ExtConvertTests_{Guid.NewGuid():N}.pcx");
        await File.WriteAllBytesAsync(path, ms.ToArray(), TestContext.Current.CancellationToken);
        try
        {
            using var decoded = new Bitmap(new MemoryStream(await FFmpeg.DecodeImagePngAsync(path, TestContext.Current.CancellationToken)));
            Assert.Equal([255, 255, 255, 255], TestImages.Pixel(decoded, 2, 5)); // 투명 → 흰색
            Assert.Equal([0, 0, 255, 255], TestImages.Pixel(decoded, 15, 5));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
