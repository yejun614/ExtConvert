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
    /// <summary>확장자(점 유무, 대소문자 무관)로 변환기를 찾는다. "jpeg"는 "jpg"로 취급한다.</summary>
    public static T? FindByExtension<T>(this IEnumerable<T> exporters, string extension) where T : IExporter
    {
        var ext = extension.TrimStart('.');
        if (ext.Equals("jpeg", StringComparison.OrdinalIgnoreCase))
            ext = "jpg";
        return exporters.FirstOrDefault(e => e.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }
}
