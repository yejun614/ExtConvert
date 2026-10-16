namespace ExtConvert.Media;

/// <summary>첫 번째 오디오 트랙을 LAME으로 인코딩해 MP3로 저장한다. 비디오는 버린다.</summary>
public sealed class Mp3Exporter : IMediaExporter
{
    public static readonly int[] Bitrates = [96, 128, 160, 192, 256, 320];

    public string DisplayName => "MP3 오디오";
    public string Extension => "mp3";
    public bool RequiresAudio => true;

    public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, MediaExportOptions options) =>
    [
        "-hide_banner", "-nostdin", "-y",
        "-i", inputPath,
        "-map", "0:a:0", "-vn",
        "-c:a", "libmp3lame", "-b:a", $"{options.AudioBitrateKbps}k",
        "-id3v2_version", "3", // Windows 탐색기 등 호환성
        "-progress", "pipe:1", "-nostats",
        outputPath,
    ];
}
