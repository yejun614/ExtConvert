using System.Globalization;

namespace ExtConvert.Media;

/// <summary>ffmpeg 비디오 인코더 하나와, 화질 단계별 인자.</summary>
public sealed record VideoEncoder(string Encoder, Func<VideoQuality, string[]> Args);

/// <summary>옵션 대화상자에서 고르는 비디오 코덱 하나 (예: "H.264").</summary>
/// <param name="Encoders">우선순위 순 후보. 설치된 ffmpeg에 있는 첫 번째를 사용한다.</param>
/// <param name="PixelFormat">출력 픽셀 형식. null이면 ffmpeg가 고른다.</param>
/// <param name="EvenDimensions">yuv420p처럼 가로·세로가 짝수여야 하는 코덱인지.</param>
/// <param name="PostFilter">크기/프레임 조절 뒤에 붙일 필터 (GIF 팔레트 생성 등).</param>
/// <param name="FrameRates">허용되는 프레임 레이트 목록 (MPEG-2 등). 원본과 가장 가까운 값으로 맞춘다.</param>
public sealed record VideoCodecOption(
    string Name,
    VideoEncoder[] Encoders,
    string? PixelFormat = "yuv420p",
    bool EvenDimensions = true,
    string? PostFilter = null,
    double[]? FrameRates = null,
    string[]? ExtraArgs = null)
{
    public VideoEncoder? Encoder => Encoders.FirstOrDefault(e => FFmpeg.HasEncoder(e.Encoder));
}

/// <summary>ffmpeg 오디오 인코더와 고정 비트레이트 (동영상 안의 오디오 트랙용).</summary>
public sealed record AudioEncoder(string Encoder, int BitrateKbps);

/// <summary>
/// 첫 번째 비디오 트랙(+ 있으면 첫 번째 오디오 트랙)을 동영상 컨테이너로 저장한다.
/// </summary>
/// <param name="codecs">선택 가능한 비디오 코덱. 첫 번째가 기본값.</param>
/// <param name="audio">오디오 인코더 후보 (첫 번째 사용 가능한 것). 빈 배열이면 오디오를 버린다 (GIF 등).</param>
/// <param name="copyVideoCodecs">재인코딩 없이 담을 수 있는 원본 비디오 코덱. null이면 모두 (MKV).</param>
/// <param name="copyAudioCodecs">재인코딩 없이 담을 수 있는 원본 오디오 코덱. null이면 모두.</param>
/// <param name="muxerArgs">컨테이너 관련 추가 인자 (예: -movflags +faststart).</param>
public sealed class VideoExporter(
    string displayName,
    string extension,
    VideoCodecOption[] codecs,
    AudioEncoder[] audio,
    string[]? copyVideoCodecs,
    string[]? copyAudioCodecs,
    string[] muxerArgs,
    int? defaultMaxResolution = null,
    double? defaultFrameRate = null) : IMediaExporter
{
    /// <summary>MP4/MOV에서 HEVC를 Apple 기기가 인식하도록 붙이는 태그가 필요한 컨테이너.</summary>
    private bool IsMp4Family => extension is "mp4" or "m4v" or "mov";

    public string DisplayName => displayName;
    public string Extension => extension;
    public bool RequiresAudio => false;
    public bool RequiresVideo => true;
    public bool IsAvailable => VideoCodecs.Count > 0 && (audio.Length == 0 || AudioCodec is not null);
    public IReadOnlyList<int> AudioBitrates => [];
    public int DefaultAudioBitrate => AudioCodec?.BitrateKbps ?? 0;
    public IReadOnlyList<VideoCodecOption> VideoCodecs => codecs.Where(c => c.Encoder is not null).ToList();
    public int? DefaultMaxResolution => defaultMaxResolution;
    public double? DefaultFrameRate => defaultFrameRate;

    private AudioEncoder? AudioCodec => audio.FirstOrDefault(a => FFmpeg.HasEncoder(a.Encoder));

    public bool CanCopy(MediaInfo source) =>
        copyVideoCodecs is not [] && source.VideoCodec is { } v && (copyVideoCodecs is null || copyVideoCodecs.Contains(v));

    private bool CanCopyAudio(MediaInfo source) =>
        source.AudioCodec is { } a && (copyAudioCodecs is null || copyAudioCodecs.Contains(a));

    public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, MediaInfo source,
        MediaExportOptions options)
    {
        // 0:V = 앨범 아트(attached_pic)를 제외한 비디오 트랙
        List<string> args = ["-hide_banner", "-nostdin", "-y", ..options.InputRangeArgs(), "-i", inputPath, "-map", "0:V:0"];
        var keepAudio = audio.Length > 0 && source.HasAudio;
        if (keepAudio)
            args.AddRange(["-map", "0:a:0"]);

        // 화면을 자르려면 다시 인코딩해야 한다
        var copy = options.CopyStreams && CanCopy(source) && options.Crop is null;
        if (copy)
        {
            args.AddRange(["-c:v", "copy"]);
            if (IsMp4Family && source.VideoCodec == "hevc")
                args.AddRange(["-tag:v", "hvc1"]);
        }
        else
        {
            var codec = VideoCodecs.FirstOrDefault(c => c.Name == options.VideoCodec) ?? VideoCodecs[0];
            var encoder = codec.Encoder!;
            args.AddRange(["-vf", BuildFilter(codec, source, options)]);
            args.AddRange(["-c:v", encoder.Encoder, ..encoder.Args(options.Quality)]);
            if (codec.PixelFormat is { } pix)
                args.AddRange(["-pix_fmt", pix]);
            if (IsMp4Family && encoder.Encoder == "libx265")
                args.AddRange(["-tag:v", "hvc1"]);
            if (codec.ExtraArgs is { } extra)
                args.AddRange(extra);
        }

        if (keepAudio)
        {
            // 영상을 그대로 복사할 때는 오디오도 가능하면 복사, 안 되면 이 컨테이너의 기본 코덱으로 인코딩
            if (copy && CanCopyAudio(source))
                args.AddRange(["-c:a", "copy"]);
            else if (AudioCodec is { } a)
                args.AddRange(["-c:a", a.Encoder, "-b:a", $"{a.BitrateKbps}k"]);
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(muxerArgs);
        args.AddRange(["-progress", "pipe:1", "-nostats", outputPath]);
        return args;
    }

    private static string BuildFilter(VideoCodecOption codec, MediaInfo source, MediaExportOptions options)
    {
        var filters = new List<string>();

        // 영역 자르기가 먼저 (ffmpeg는 회전을 자동으로 적용한 뒤 필터를 거치므로 표시 좌표 그대로 사용)
        if (options.Crop is { } c)
        {
            var (w, h) = codec.EvenDimensions ? (Math.Max(2, c.Width & ~1), Math.Max(2, c.Height & ~1)) : (c.Width, c.Height);
            filters.Add($"crop={w}:{h}:{c.X}:{c.Y}");
        }

        var fps = options.FrameRate;
        if (codec.FrameRates is { } allowed)
        {
            var target = fps ?? (source.FrameRate > 0 ? source.FrameRate : 30);
            fps = allowed.MinBy(r => Math.Abs(r - target));
        }
        if (fps is { } f && (options.FrameRate is not null || Math.Abs(f - source.FrameRate) > 0.01))
            filters.Add("fps=" + f.ToString("0.###", CultureInfo.InvariantCulture));

        // 짝수 크기가 필요한 코덱은 trunc(x/2)*2로 내림. -2는 "비율 유지 + 짝수"를 뜻한다.
        string Even(string expr) => codec.EvenDimensions ? $"trunc({expr}/2)*2" : expr;
        var any = codec.EvenDimensions ? "-2" : "-1";
        if (options.MaxResolution is { } max)
        {
            // 짧은 변을 max 이하로 줄인다 (세로 영상도 동일하게 취급). 원본보다 키우지는 않음.
            filters.Add($"scale='if(gt(iw,ih),{any},min({max},{Even("iw")}))':'if(gt(iw,ih),min({max},{Even("ih")}),{any})'"
                        + ":flags=lanczos");
        }
        else if (codec.EvenDimensions)
        {
            filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        }

        if (codec.PostFilter is { } post)
            filters.Add(post);

        return filters.Count > 0 ? string.Join(",", filters) : "null";
    }
}
