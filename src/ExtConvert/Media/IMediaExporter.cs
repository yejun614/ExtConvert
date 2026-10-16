using ExtConvert.Conversion;

namespace ExtConvert.Media;

/// <summary>FFmpeg 명령으로 동영상/오디오를 다른 형식으로 저장하는 변환기.</summary>
public interface IMediaExporter : IExporter
{
    /// <summary>입력에 오디오 트랙이 있어야 변환할 수 있는지.</summary>
    bool RequiresAudio { get; }

    /// <summary>ffmpeg 인자 목록. 진행률 보고를 위해 "-progress pipe:1"을 포함해야 한다.</summary>
    IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, MediaExportOptions options);
}

public sealed record MediaExportOptions
{
    /// <summary>오디오 비트레이트 (kbps).</summary>
    public int AudioBitrateKbps { get; init; } = 192;
}
