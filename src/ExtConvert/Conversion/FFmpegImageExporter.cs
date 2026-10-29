using Avalonia.Media;
using Avalonia.Media.Imaging;
using ExtConvert.Media;

namespace ExtConvert.Conversion;

/// <summary>ffmpeg 인코더 하나와 그 인코더에 넘길 추가 인자.</summary>
public sealed record FFmpegCodec(string Encoder, params string[] Args);

/// <summary>
/// Avalonia가 직접 저장하지 못하는 이미지 형식(WebP, AVIF, TIFF 등)을 ffmpeg로 인코딩한다.
/// 원본을 임시 PNG로 저장한 뒤 ffmpeg로 변환하고, 결과를 출력 스트림에 복사한다.
/// </summary>
/// <param name="codecs">우선순위 순 후보. 설치된 ffmpeg에 있는 첫 번째 인코더를 사용한다.</param>
/// <param name="supportsAlpha">false면 투명 영역을 흰색 위에 합성한 뒤 인코딩한다.</param>
public sealed class FFmpegImageExporter(string displayName, string extension, bool supportsAlpha,
    params FFmpegCodec[] codecs) : IImageExporter
{
    public string DisplayName => displayName;
    public string Extension => extension;
    public bool IsAvailable => FFmpeg.IsAvailable && Codec is not null;

    private FFmpegCodec? Codec => codecs.FirstOrDefault(c => FFmpeg.HasEncoder(c.Encoder));

    public async Task ExportAsync(Bitmap source, Stream output, ExportOptions options, CancellationToken ct = default)
    {
        var codec = Codec ?? throw new InvalidOperationException(
            $"설치된 ffmpeg에 {DisplayName} 인코더({string.Join(", ", codecs.Select(c => c.Encoder))})가 없습니다.");

        var dir = Directory.CreateTempSubdirectory("ExtConvert_").FullName;
        try
        {
            var input = Path.Combine(dir, "input.png");
            var result = Path.Combine(dir, "output." + Extension);

            if (supportsAlpha)
                source.Save(input, PngBitmapEncoderOptions.Default);
            else
                using (var flattened = ImageOps.RenderFit(source, source.PixelSize.Width, source.PixelSize.Height, Colors.White))
                    flattened.Save(input, PngBitmapEncoderOptions.Default);

            await FFmpeg.ConvertAsync(
            [
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-i", input,
                "-frames:v", "1",
                "-c:v", codec.Encoder, ..codec.Args,
                "-update", "1", // 이미지 시퀀스(%d) 패턴이 아닌 단일 파일로 저장
                result,
            ], duration: null, progress: null, ct);

            await using var file = File.OpenRead(result);
            await file.CopyToAsync(output, ct);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
