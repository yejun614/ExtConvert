using System.Globalization;
using ExtConvert.Conversion;

namespace ExtConvert.Media;

/// <summary>FFmpeg 명령으로 동영상/오디오를 다른 형식으로 저장하는 변환기.</summary>
public interface IMediaExporter : IExporter
{
    /// <summary>입력에 오디오 트랙이 있어야 변환할 수 있는지.</summary>
    bool RequiresAudio { get; }

    /// <summary>입력에 비디오 트랙이 있어야 변환할 수 있는지 (= 출력이 동영상인지).</summary>
    bool RequiresVideo { get; }

    /// <summary>설치된 ffmpeg에 필요한 인코더가 모두 있는지.</summary>
    bool IsAvailable { get; }

    /// <summary>선택 가능한 오디오 비트레이트 (kbps). 비어 있으면 고르지 않는다 (무손실 형식 등).</summary>
    IReadOnlyList<int> AudioBitrates { get; }

    int DefaultAudioBitrate { get; }

    /// <summary>선택 가능한 비디오 코덱 (설치된 인코더가 있는 것만). 오디오 출력이면 비어 있다.</summary>
    IReadOnlyList<VideoCodecOption> VideoCodecs { get; }

    /// <summary>옵션 대화상자의 기본 해상도(짧은 변 픽셀)/프레임 레이트. null이면 원본.</summary>
    int? DefaultMaxResolution => null;
    double? DefaultFrameRate => null;

    /// <summary>원본 코덱을 그대로 담을 수 있어 재인코딩 없이(스트림 복사) 저장할 수 있는지.</summary>
    bool CanCopy(MediaInfo source);

    /// <summary>ffmpeg 인자 목록. 진행률 보고를 위해 "-progress pipe:1"을 포함해야 한다.</summary>
    IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, MediaInfo source, MediaExportOptions options);
}

public enum VideoQuality { High, Medium, Low }

public sealed record MediaExportOptions
{
    /// <summary>오디오 비트레이트 (kbps). null이면 변환기 기본값.</summary>
    public int? AudioBitrateKbps { get; init; }

    /// <summary>호환되는 스트림은 재인코딩 없이 복사한다 (<see cref="IMediaExporter.CanCopy"/>일 때만 의미 있음).</summary>
    public bool CopyStreams { get; init; }

    /// <summary><see cref="VideoCodecOption.Name"/>. null이면 첫 번째 사용 가능한 코덱.</summary>
    public string? VideoCodec { get; init; }

    public VideoQuality Quality { get; init; } = VideoQuality.Medium;

    /// <summary>짧은 변의 최대 픽셀 (예: 720 → 1280×720). 원본보다 키우지 않는다. null이면 원본.</summary>
    public int? MaxResolution { get; init; }

    /// <summary>출력 프레임 레이트. null이면 원본.</summary>
    public double? FrameRate { get; init; }

    /// <summary>이 구간만 저장한다. null이면 전체.</summary>
    public MediaRange? Range { get; init; }

    /// <summary>화면에서 이 영역만 잘라서 저장한다 (회전이 반영된 표시 좌표, 픽셀). 동영상 출력에만 적용. null이면 전체.</summary>
    public CropRect? Crop { get; init; }

    /// <summary>ffmpeg 입력(-i) 앞에 넣을 구간 인자. 입력 쪽 -ss는 빠르고, 재인코딩 시 샘플 단위로 정확하다.</summary>
    internal IEnumerable<string> InputRangeArgs()
    {
        if (Range is not { } r)
            yield break;
        yield return "-ss";
        yield return r.Start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        yield return "-t";
        yield return r.Length.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
    }
}

/// <summary>동영상 화면에서 잘라낼 영역 (픽셀).</summary>
public readonly record struct CropRect(int X, int Y, int Width, int Height);

/// <summary>시작 시각과 길이로 나타낸 구간.</summary>
public readonly record struct MediaRange(TimeSpan Start, TimeSpan Length)
{
    public TimeSpan End => Start + Length;
}
