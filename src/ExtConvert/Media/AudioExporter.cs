namespace ExtConvert.Media;

/// <summary>첫 번째 오디오 트랙만 지정한 코덱으로 저장한다. 비디오(앨범 아트 포함)는 버린다.</summary>
/// <param name="encoder">ffmpeg 오디오 인코더 이름.</param>
/// <param name="bitrates">선택 가능한 비트레이트 (kbps). 무손실 형식이면 빈 배열.</param>
/// <param name="copyCodecs">재인코딩 없이 담을 수 있는 원본 코덱 (ffprobe codec_name).</param>
/// <param name="extraArgs">인코더/먹서 추가 인자.</param>
public sealed class AudioExporter(string displayName, string extension, string encoder,
    int[] bitrates, int defaultBitrate, string[] copyCodecs, params string[] extraArgs) : IMediaExporter
{
    public string DisplayName => displayName;
    public string Extension => extension;
    public bool RequiresAudio => true;
    public bool RequiresVideo => false;
    public bool IsAvailable => FFmpeg.HasEncoder(encoder);
    public IReadOnlyList<int> AudioBitrates => bitrates;
    public int DefaultAudioBitrate => defaultBitrate;
    public IReadOnlyList<VideoCodecOption> VideoCodecs => [];

    public bool CanCopy(MediaInfo source) => source.AudioCodec is { } codec && copyCodecs.Contains(codec);

    public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, MediaInfo source,
        MediaExportOptions options)
    {
        List<string> args = ["-hide_banner", "-nostdin", "-y", ..options.InputRangeArgs(), "-i", inputPath, "-map", "0:a:0", "-vn"];

        if (options.CopyStreams && CanCopy(source))
            args.AddRange(["-c:a", "copy"]);
        else
        {
            args.AddRange(["-c:a", encoder]);
            if (bitrates.Length > 0)
                args.AddRange(["-b:a", $"{options.AudioBitrateKbps ?? defaultBitrate}k"]);
        }

        args.AddRange(extraArgs);
        args.AddRange(["-progress", "pipe:1", "-nostats", outputPath]);
        return args;
    }
}
