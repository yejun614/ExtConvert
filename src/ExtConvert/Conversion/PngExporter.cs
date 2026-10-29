using Avalonia.Media.Imaging;

namespace ExtConvert.Conversion;

public sealed class PngExporter : IImageExporter
{
    public string DisplayName => "PNG 이미지";
    public string Extension => "png";

    public void Export(Bitmap source, Stream output, ExportOptions options) =>
        source.Save(output, PngBitmapEncoderOptions.Default);

    public Task ExportAsync(Bitmap source, Stream output, ExportOptions options, CancellationToken ct = default)
    {
        Export(source, output, options);
        return Task.CompletedTask;
    }
}
