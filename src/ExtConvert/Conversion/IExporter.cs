namespace ExtConvert.Conversion;

/// <summary>"다른 이름으로 저장"에 나타나는 출력 형식 하나.</summary>
public interface IExporter
{
    /// <summary>저장 대화상자에 표시될 이름 (예: "Windows 아이콘").</summary>
    string DisplayName { get; }

    /// <summary>점(.) 없는 기본 확장자 (예: "ico").</summary>
    string Extension { get; }
}

public static class ExporterExtensions
{
    /// <summary>같은 형식의 다른 확장자 → 변환기의 기본 확장자.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpeg"] = "jpg", ["jpe"] = "jpg", ["jfif"] = "jpg",
        ["tiff"] = "tif", ["aif"] = "aiff", ["mpeg"] = "mpg", ["m4v"] = "mp4",
    };

    /// <summary>확장자(점 유무, 대소문자 무관)로 변환기를 찾는다. "jpeg"는 "jpg"처럼 별칭도 인식한다.</summary>
    public static T? FindByExtension<T>(this IEnumerable<T> exporters, string extension) where T : IExporter
    {
        var ext = extension.TrimStart('.');
        if (Aliases.TryGetValue(ext, out var canonical))
            ext = canonical;
        return exporters.FirstOrDefault(e => e.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }
}
