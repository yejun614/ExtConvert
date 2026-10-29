using ExtConvert.Media;

namespace ExtConvert.Conversion;

/// <summary>사용 가능한 변환기 목록. 새 형식은 여기에 추가한다.</summary>
public static class Exporters
{
    /// <summary>이미지 파일을 열었을 때 저장 가능한 형식. ffmpeg 기반 형식은 <see cref="IImageExporter.IsAvailable"/>로 거른다.</summary>
    public static IReadOnlyList<IImageExporter> Images { get; } =
    [
        new IcoExporter(),
        new PngExporter(),
        new JpegExporter(),
        new BmpExporter(),
        new FFmpegImageExporter("WebP 이미지", "webp", true, new FFmpegCodec("libwebp", "-quality", "90")),
        new FFmpegImageExporter("GIF 이미지", "gif", true, new FFmpegCodec("gif",
            "-vf", "split[a][b];[a]palettegen=reserve_transparent=1[p];[b][p]paletteuse=alpha_threshold=128")),
        new FFmpegImageExporter("TIFF 이미지", "tif", true, new FFmpegCodec("tiff", "-compression_algo", "lzw")),
        new FFmpegImageExporter("AVIF 이미지", "avif", false,
            new FFmpegCodec("libaom-av1", "-still-picture", "1", "-crf", "28", "-cpu-used", "6", "-row-mt", "1"),
            new FFmpegCodec("libsvtav1", "-crf", "28", "-preset", "8", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2")),
        new FFmpegImageExporter("JPEG XL 이미지", "jxl", true, new FFmpegCodec("libjxl", "-distance", "1")),
        new FFmpegImageExporter("JPEG 2000 이미지", "jp2", true, new FFmpegCodec("libopenjpeg"), new FFmpegCodec("jpeg2000")),
        new FFmpegImageExporter("Targa 이미지", "tga", true, new FFmpegCodec("targa")),
        new FFmpegImageExporter("QOI 이미지", "qoi", true, new FFmpegCodec("qoi")),
        new FFmpegImageExporter("OpenEXR 이미지", "exr", true, new FFmpegCodec("exr", "-compression", "zip16")),
        new FFmpegImageExporter("Radiance HDR 이미지", "hdr", false, new FFmpegCodec("hdr")),
        new FFmpegImageExporter("DPX 이미지", "dpx", false, new FFmpegCodec("dpx")),
        new FFmpegImageExporter("PCX 이미지", "pcx", false, new FFmpegCodec("pcx")),
        new FFmpegImageExporter("SGI 이미지", "sgi", true, new FFmpegCodec("sgi")),
        new FFmpegImageExporter("PPM 이미지 (Netpbm)", "ppm", false, new FFmpegCodec("ppm")),
    ];

    private static string Pick(VideoQuality q, string high, string medium, string low) =>
        q switch { VideoQuality.High => high, VideoQuality.Medium => medium, _ => low };

    private static readonly VideoCodecOption H264 = new("H.264",
        [new("libx264", q => ["-preset", "medium", "-crf", Pick(q, "18", "23", "28")])]);
    private static readonly VideoCodecOption H265 = new("H.265 (HEVC)",
        [new("libx265", q => ["-preset", "medium", "-crf", Pick(q, "20", "26", "31"), "-x265-params", "log-level=error"])]);
    private static readonly VideoCodecOption Av1 = new("AV1",
    [
        new("libsvtav1", q => ["-preset", "8", "-crf", Pick(q, "28", "35", "42")]),
        new("libaom-av1", q => ["-cpu-used", "6", "-row-mt", "1", "-b:v", "0", "-crf", Pick(q, "28", "35", "42")]),
    ]);
    private static readonly VideoCodecOption Vp9 = new("VP9",
        [new("libvpx-vp9", q => ["-b:v", "0", "-crf", Pick(q, "24", "32", "40"), "-row-mt", "1", "-deadline", "good", "-cpu-used", "4"])]);
    private static readonly VideoCodecOption Vp8 = new("VP8",
        [new("libvpx", q => ["-crf", Pick(q, "6", "10", "20"), "-b:v", Pick(q, "8M", "4M", "2M"), "-deadline", "good", "-cpu-used", "4"])]);
    private static readonly VideoCodecOption ProRes = new("Apple ProRes",
        [new("prores_ks", q => ["-profile:v", Pick(q, "3", "2", "1")])], PixelFormat: "yuv422p10le");
    private static readonly VideoCodecOption Xvid = new("MPEG-4 (Xvid)",
        [new("mpeg4", q => ["-q:v", Pick(q, "2", "4", "7")])], ExtraArgs: ["-vtag", "xvid"]);
    private static readonly VideoCodecOption Wmv = new("WMV 8",
        [new("wmv2", q => ["-q:v", Pick(q, "2", "5", "9")])]);
    private static readonly VideoCodecOption Mpeg2 = new("MPEG-2",
        [new("mpeg2video", q => ["-q:v", Pick(q, "2", "4", "7")])],
        FrameRates: [24000 / 1001.0, 24, 25, 30000 / 1001.0, 30, 50, 60000 / 1001.0, 60]);
    private static readonly VideoCodecOption Theora = new("Theora",
        [new("libtheora", q => ["-q:v", Pick(q, "9", "7", "5")])]);
    private static readonly VideoCodecOption Gif = new("GIF", [new("gif", _ => [])],
        PixelFormat: null, EvenDimensions: false,
        PostFilter: "split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle");
    private static readonly VideoCodecOption AnimatedWebp = new("WebP",
        [new("libwebp_anim", q => ["-quality", Pick(q, "90", "75", "50")])], PixelFormat: null, EvenDimensions: false);

    private static readonly AudioEncoder Aac = new("aac", 192);

    /// <summary>동영상/오디오 파일을 열었을 때 저장 가능한 형식 (FFmpeg 사용). <see cref="IMediaExporter.IsAvailable"/>로 거른다.</summary>
    public static IReadOnlyList<IMediaExporter> Media { get; } =
    [
        // ── 동영상 ──
        new VideoExporter("MP4 동영상", "mp4", [H264, H265, Av1], [Aac],
            ["h264", "hevc", "av1", "mpeg4"], ["aac", "mp3", "ac3", "eac3", "opus", "alac", "flac"],
            ["-movflags", "+faststart"]),
        new VideoExporter("Matroska 동영상", "mkv", [H264, H265, Av1, Vp9], [Aac], null, null, []),
        new VideoExporter("WebM 동영상", "webm", [Vp9, Av1, Vp8], [new("libopus", 128), new("libvorbis", 160)],
            ["vp8", "vp9", "av1"], ["opus", "vorbis"], []),
        new VideoExporter("QuickTime 동영상", "mov", [H264, H265, ProRes], [Aac],
            ["h264", "hevc", "prores", "mjpeg", "mpeg4"], ["aac", "mp3", "alac", "ac3", "pcm_s16le", "pcm_s24le"],
            ["-movflags", "+faststart"]),
        new VideoExporter("AVI 동영상", "avi", [Xvid], [new("libmp3lame", 192)],
            ["mpeg4", "mjpeg", "msmpeg4v3"], ["mp3", "ac3", "pcm_s16le"], []),
        new VideoExporter("Windows Media 동영상", "wmv", [Wmv], [new("wmav2", 192)],
            ["wmv1", "wmv2", "wmv3", "vc1"], ["wmav1", "wmav2", "wmapro"], []),
        new VideoExporter("Flash 동영상", "flv", [H264], [new("aac", 160)], ["h264", "flv1"], ["aac", "mp3"], []),
        new VideoExporter("MPEG-TS 동영상", "ts", [H264, H265], [Aac],
            ["h264", "hevc", "mpeg2video"], ["aac", "mp3", "ac3", "mp2"], []),
        new VideoExporter("MPEG-2 동영상", "mpg", [Mpeg2], [new("mp2", 224)],
            ["mpeg2video", "mpeg1video"], ["mp2", "ac3"], ["-f", "vob"]),
        new VideoExporter("3GP 동영상", "3gp", [H264], [new("aac", 128)], ["h264", "h263", "mpeg4"], ["aac", "amr_nb"], []),
        new VideoExporter("Ogg 동영상", "ogv", [Theora], [new("libvorbis", 160)], ["theora"], ["vorbis"], []),
        new VideoExporter("애니메이션 GIF", "gif", [Gif], [], [], null, ["-loop", "0"],
            defaultMaxResolution: 480, defaultFrameRate: 15),
        new VideoExporter("애니메이션 WebP", "webp", [AnimatedWebp], [], [], null, ["-loop", "0"],
            defaultMaxResolution: 480, defaultFrameRate: 15),

        // ── 오디오 ──
        new AudioExporter("MP3 오디오", "mp3", "libmp3lame", [96, 128, 160, 192, 256, 320], 192, ["mp3"],
            "-id3v2_version", "3"), // Windows 탐색기 등 호환성
        new AudioExporter("AAC 오디오 (M4A)", "m4a", "aac", [96, 128, 160, 192, 256, 320], 192, ["aac"],
            "-movflags", "+faststart"),
        new AudioExporter("AAC 오디오 (ADTS)", "aac", "aac", [96, 128, 160, 192, 256, 320], 192, ["aac"]),
        new AudioExporter("Opus 오디오", "opus", "libopus", [64, 96, 128, 160, 192, 256], 128, ["opus"]),
        new AudioExporter("Ogg Vorbis 오디오", "ogg", "libvorbis", [96, 128, 160, 192, 256, 320], 192, ["vorbis"]),
        new AudioExporter("FLAC 오디오 (무손실)", "flac", "flac", [], 0, ["flac"]),
        new AudioExporter("WAV 오디오 (무압축)", "wav", "pcm_s16le", [], 0, ["pcm_s16le"]),
        new AudioExporter("AIFF 오디오 (무압축)", "aiff", "pcm_s16be", [], 0, ["pcm_s16be"]),
        new AudioExporter("WavPack 오디오 (무손실)", "wv", "wavpack", [], 0, ["wavpack"]),
        new AudioExporter("Windows Media 오디오", "wma", "wmav2", [96, 128, 160, 192, 256], 192, ["wmav2"]),
        new AudioExporter("Dolby Digital (AC-3)", "ac3", "ac3", [192, 256, 320, 384, 448, 640], 384, ["ac3"]),
    ];

    /// <summary>Avalonia(Skia)가 직접 디코딩하는 이미지 확장자.</summary>
    public static IReadOnlyList<string> NativeImageExtensions { get; } =
        ["png", "jpg", "jpeg", "jpe", "jfif", "bmp", "dib", "gif", "webp", "ico"];

    /// <summary>ffmpeg로 디코딩하는 이미지 확장자 (FFmpeg 필요).</summary>
    public static IReadOnlyList<string> FFmpegImageExtensions { get; } =
    [
        "tif", "tiff", "tga", "psd", "dds", "avif", "heic", "heif", "jxl", "jp2", "j2k", "qoi", "pcx",
        "exr", "hdr", "ppm", "pgm", "pbm", "pnm", "pam", "pfm", "sgi", "rgb", "ras", "sun",
        "xbm", "xpm", "xwd", "dpx", "fits", "wbmp",
    ];

    /// <summary>미리보기를 지원하는 모든 이미지 확장자.</summary>
    public static IReadOnlyList<string> ImageExtensions { get; } = [..NativeImageExtensions, ..FFmpegImageExtensions];

    /// <summary>FFmpeg로 여는 동영상/오디오 확장자.</summary>
    public static IReadOnlyList<string> MediaExtensions { get; } =
    [
        // 동영상
        "mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "asf", "flv", "f4v", "ts", "m2ts", "mts",
        "mpg", "mpeg", "vob", "3gp", "3g2", "ogv", "mxf", "rm", "rmvb", "divx",
        // 오디오
        "mp3", "m4a", "aac", "wav", "flac", "ogg", "oga", "opus", "weba", "wma", "aif", "aiff", "aifc",
        "ac3", "eac3", "dts", "amr", "ape", "wv", "mka", "mp2", "caf", "au",
    ];
}
