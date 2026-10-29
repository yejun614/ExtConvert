using Avalonia.Media.Imaging;

namespace ExtConvert.Conversion;

/// <summary>미리보기 중인 이미지를 특정 파일 형식으로 저장하는 변환기.</summary>
public interface IImageExporter : IExporter
{
    /// <summary>현재 환경에서 사용할 수 있는지 (FFmpeg 기반 변환기는 ffmpeg와 인코더가 있어야 함).</summary>
    bool IsAvailable => true;

    Task ExportAsync(Bitmap source, Stream output, ExportOptions options, CancellationToken ct = default);
}

public sealed record ExportOptions
{
    /// <summary>ICO 파일에 포함할 정사각형 크기 목록 (픽셀).</summary>
    public IReadOnlyList<int> IcoSizes { get; init; } = IcoExporter.DefaultSizes;
}
