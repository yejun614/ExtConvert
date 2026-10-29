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
    /// <summary>초당 프레임 수 (알 수 없으면 0).</summary>
    public double FrameRate { get; init; }
    /// <summary>표시 회전 각도 (휴대폰 세로 영상 등). ffmpeg는 디코딩 시 자동으로 회전시킨다.</summary>
    public int Rotation { get; init; }
    /// <summary>비디오 프레임 수 (컨테이너가 알려줄 때만, 모르면 0). 애니메이션 GIF 판별에 사용.</summary>
    public int FrameCount { get; init; }
    public string? AudioCodec { get; init; }
    public int AudioChannels { get; init; }
    public int SampleRate { get; init; }

    public bool HasVideo => VideoCodec is not null;
    public bool HasAudio => AudioCodec is not null;

    /// <summary>회전을 반영한 실제 표시 크기.</summary>
    public (int Width, int Height) DisplaySize =>
        Math.Abs(Rotation % 180) == 90 ? (Height, Width) : (Width, Height);

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
                    FrameRate = ParseRate(s.GetStringOrNull("avg_frame_rate")) is > 0 and var avg
                        ? avg
                        : ParseRate(s.GetStringOrNull("r_frame_rate")),
                    Rotation = ParseRotation(s),
                    FrameCount = int.TryParse(s.GetStringOrNull("nb_frames"), out var frames) ? frames : 0,
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

    /// <summary>"30000/1001" 같은 분수 문자열.</summary>
    private static double ParseRate(string? rate)
    {
        if (rate?.Split('/') is not [var n, var d]) return 0;
        return double.TryParse(n, CultureInfo.InvariantCulture, out var num)
               && double.TryParse(d, CultureInfo.InvariantCulture, out var den) && den > 0
            ? num / den
            : 0;
    }

    private static int ParseRotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var list))
            foreach (var sd in list.EnumerateArray())
                if (sd.TryGetProperty("rotation", out var r) && r.TryGetInt32(out var deg))
                    return deg;
        // 오래된 ffprobe는 tags.rotate에 넣는다
        if (stream.TryGetProperty("tags", out var tags)
            && int.TryParse(tags.GetStringOrNull("rotate"), out var legacy))
            return legacy;
        return 0;
    }

    /// <summary>상태 표시줄용 요약 (예: "01:23 · 1920×1080 h264 · aac 2ch 48kHz").</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Duration is { } dur)
            parts.Add(dur.TotalHours >= 1 ? dur.ToString(@"h\:mm\:ss") : dur.ToString(@"mm\:ss"));
        if (HasVideo)
            parts.Add($"{DisplaySize.Width}×{DisplaySize.Height} {VideoCodec}");
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
