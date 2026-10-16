using ExtConvert.Media;

namespace ExtConvert.Conversion;

/// <summary>사용 가능한 변환기 목록. 새 형식은 여기에 추가한다.</summary>
public static class Exporters
{
    /// <summary>이미지 파일을 열었을 때 저장 가능한 형식.</summary>
    public static IReadOnlyList<IImageExporter> Images { get; } =
    [
        new IcoExporter(),
        new PngExporter(),
        new JpegExporter(),
    ];

    /// <summary>동영상/오디오 파일을 열었을 때 저장 가능한 형식 (FFmpeg 사용).</summary>
    public static IReadOnlyList<IMediaExporter> Media { get; } =
    [
        new Mp3Exporter(),
    ];

    /// <summary>미리보기(디코딩)를 지원하는 이미지 확장자.</summary>
    public static IReadOnlyList<string> ImageExtensions { get; } =
        ["png", "jpg", "jpeg", "bmp", "gif", "webp", "ico"];

    /// <summary>FFmpeg로 여는 동영상/오디오 확장자.</summary>
    public static IReadOnlyList<string> MediaExtensions { get; } =
        ["mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "flv", "mp3", "m4a", "aac", "wav", "flac", "ogg", "opus"];
}
