using Avalonia.Media.Imaging;

namespace ExtConvert.Conversion;

/// <summary>
/// 여러 해상도의 PNG를 담은 ICO 파일을 만든다 (Windows Vista 이후 표준 형식).
/// 정사각형이 아닌 이미지는 비율을 유지하고 투명 여백을 넣는다.
/// </summary>
public sealed class IcoExporter : IImageExporter
{
    public static readonly int[] AllSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
    public static readonly int[] DefaultSizes = [16, 24, 32, 48, 64, 128, 256];

    public string DisplayName => "Windows 아이콘";
    public string Extension => "ico";

    public void Export(Bitmap source, Stream output, ExportOptions options)
    {
        var sizes = options.IcoSizes.Where(s => s is >= 1 and <= 256).Distinct().Order().ToArray();
        if (sizes.Length == 0)
            throw new ArgumentException("ICO 크기를 하나 이상 선택해야 합니다.");

        var images = sizes.Select(size =>
        {
            using var frame = ImageOps.RenderFit(source, size, size);
            using var ms = new MemoryStream();
            frame.Save(ms, PngBitmapEncoderOptions.Default);
            return (Size: size, Data: ms.ToArray());
        }).ToArray();

        WriteIco(output, images);
    }

    private static void WriteIco(Stream output, IReadOnlyList<(int Size, byte[] Data)> images)
    {
        using var w = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);

        // ICONDIR
        w.Write((ushort)0);            // reserved
        w.Write((ushort)1);            // type: 1 = icon
        w.Write((ushort)images.Count); // image count

        // ICONDIRENTRY × N
        var offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            var dim = (byte)(size >= 256 ? 0 : size); // 0은 256을 의미
            w.Write(dim);          // width
            w.Write(dim);          // height
            w.Write((byte)0);      // palette color count
            w.Write((byte)0);      // reserved
            w.Write((ushort)1);    // color planes
            w.Write((ushort)32);   // bits per pixel
            w.Write(data.Length);  // image data size
            w.Write(offset);       // image data offset
            offset += data.Length;
        }

        foreach (var (_, data) in images)
            w.Write(data);
    }
}
