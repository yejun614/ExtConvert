using System.Globalization;
using System.Text.Json;

namespace ExtConvert.Media;

/// <summary>ffprobe가 알려준 미디어 파일 정보 중 미리보기에 필요한 부분.</summary>
public sealed record MediaInfo
{
    public TimeSpan? Duration { get; init; }
    public string? VideoCodec { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? AudioCodec { get; init; }
    public int AudioChannels { get; init; }
    public int SampleRate { get; init; }

    public bool HasVideo => VideoCodec is not null;
    public bool HasAudio => AudioCodec is not null;

    /// <summary>ffprobe -print_format json -show_format -show_streams 출력을 해석한다.</summary>
    public static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var info = new MediaInfo();

        if (root.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var d)
            && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            info = info with { Duration = TimeSpan.FromSeconds(seconds) };

        if (!root.TryGetProperty("streams", out var streams))
            return info;

        foreach (var s in streams.EnumerateArray())
        {
            var type = s.GetStringOrNull("codec_type");
            var codec = s.GetStringOrNull("codec_name");

            // 앨범 아트(attached_pic)는 비디오 트랙으로 보지 않는다
            var isCoverArt = s.TryGetProperty("disposition", out var disp)
                && disp.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1;

            if (type == "video" && !isCoverArt && info.VideoCodec is null)
                info = info with
                {
                    VideoCodec = codec,
                    Width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
                    Height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
                };
            else if (type == "audio" && info.AudioCodec is null)
                info = info with
                {
                    AudioCodec = codec,
                    AudioChannels = s.TryGetProperty("channels", out var c) ? c.GetInt32() : 0,
                    SampleRate = int.TryParse(s.GetStringOrNull("sample_rate"), out var sr) ? sr : 0,
                };
        }

        return info;
    }

    /// <summary>상태 표시줄용 요약 (예: "01:23 · 1920×1080 h264 · aac 2ch 48kHz").</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Duration is { } dur)
            parts.Add(dur.TotalHours >= 1 ? dur.ToString(@"h\:mm\:ss") : dur.ToString(@"mm\:ss"));
        if (HasVideo)
            parts.Add($"{Width}×{Height} {VideoCodec}");
        if (HasAudio)
            parts.Add($"{AudioCodec} {AudioChannels}ch {SampleRate / 1000.0:0.#}kHz");
        return string.Join("  ·  ", parts);
    }
}

internal static class JsonElementExtensions
{
    public static string? GetStringOrNull(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
