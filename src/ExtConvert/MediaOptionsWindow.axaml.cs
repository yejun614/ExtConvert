using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ExtConvert.Media;

namespace ExtConvert;

/// <summary>
/// 동영상/오디오 저장 옵션. 변환기와 원본에 해당하는 항목만 보여준다
/// (오디오: 비트레이트, 동영상: 코덱·화질·해상도·프레임 레이트, 호환되면 재인코딩 없이 저장).
/// </summary>
public partial class MediaOptionsWindow : Window
{
    private static readonly int[] Resolutions = [2160, 1440, 1080, 720, 480, 360, 240];
    private static readonly double[] FrameRates = [60, 30, 24, 15, 10];
    private static readonly (VideoQuality Value, string Label)[] Qualities =
        [(VideoQuality.High, "높음"), (VideoQuality.Medium, "보통"), (VideoQuality.Low, "낮음 (작은 파일)")];

    private readonly IMediaExporter _exporter;
    private readonly IReadOnlyList<VideoCodecOption> _codecs;
    private readonly List<int?> _resolutions = [];
    private readonly List<double?> _frameRates = [];
    private readonly bool _trimmed;

    // XAML 디자이너용
    public MediaOptionsWindow() : this(Conversion.Exporters.Media[0], new MediaInfo()) { }

    /// <param name="allowCopy">false면 재인코딩 없이 저장을 숨긴다 (화면 영역을 자를 때).</param>
    /// <param name="trimmed">구간만 저장하는지. 재인코딩 없이 자르면 키프레임 단위가 된다는 안내에 사용.</param>
    public MediaOptionsWindow(IMediaExporter exporter, MediaInfo source, bool allowCopy = true, bool trimmed = false)
    {
        InitializeComponent();
        _exporter = exporter;
        _codecs = exporter.VideoCodecs;
        Title = $"{exporter.DisplayName} 저장 옵션";

        _trimmed = trimmed;
        CopyBox.IsVisible = allowCopy && exporter.CanCopy(source);
        CopyBox.IsChecked = CopyBox.IsVisible;

        var bitrates = exporter.AudioBitrates;
        BitrateRow.IsVisible = bitrates.Count > 0;
        BitrateBox.ItemsSource = bitrates.Select(b => $"{b} kbps").ToArray();
        BitrateBox.SelectedIndex = Math.Max(0, bitrates.ToList().IndexOf(exporter.DefaultAudioBitrate));

        var video = exporter.RequiresVideo;
        CodecRow.IsVisible = _codecs.Count > 1;
        CodecBox.ItemsSource = _codecs.Select(c => c.Name).ToArray();
        CodecBox.SelectedIndex = 0;

        QualityRow.IsVisible = video;
        QualityBox.ItemsSource = Qualities.Select(q => q.Label).ToArray();
        QualityBox.SelectedIndex = 1;

        // 원본보다 큰 해상도/프레임 레이트는 의미가 없으므로 목록에서 뺀다
        var (w, h) = source.DisplaySize;
        var shortSide = Math.Min(w, h);
        var resLabels = new List<string> { shortSide > 0 ? $"원본 ({w}×{h})" : "원본" };
        _resolutions.Add(null);
        foreach (var r in Resolutions.Where(r => shortSide <= 0 || r < shortSide))
        {
            _resolutions.Add(r);
            resLabels.Add($"{r}p");
        }
        ResolutionRow.IsVisible = video;
        ResolutionBox.ItemsSource = resLabels;
        ResolutionBox.SelectedIndex = Math.Max(0, _resolutions.IndexOf(exporter.DefaultMaxResolution));

        var fpsLabels = new List<string>
            { source.FrameRate > 0 ? $"원본 ({source.FrameRate.ToString("0.##", CultureInfo.InvariantCulture)} fps)" : "원본" };
        _frameRates.Add(null);
        foreach (var f in FrameRates.Where(f => source.FrameRate <= 0 || f < source.FrameRate - 0.5))
        {
            _frameRates.Add(f);
            fpsLabels.Add($"{f} fps");
        }
        FrameRateRow.IsVisible = video;
        FrameRateBox.ItemsSource = fpsLabels;
        FrameRateBox.SelectedIndex = Math.Max(0, _frameRates.IndexOf(exporter.DefaultFrameRate));

        UpdateEnabled();
    }

    /// <summary>보여줄 옵션이 하나라도 있는지. 없으면(예: WAV) 대화상자 없이 바로 저장한다.</summary>
    public static bool HasOptions(IMediaExporter exporter, MediaInfo source, bool allowCopy = true) =>
        exporter.RequiresVideo || exporter.AudioBitrates.Count > 0 || (allowCopy && exporter.CanCopy(source));

    private void OnCopyChanged(object? sender, RoutedEventArgs e) => UpdateEnabled();

    private void UpdateEnabled()
    {
        var copy = CopyBox.IsChecked == true;
        OptionsPanel.IsEnabled = !copy;
        HintText.Text = copy
            ? _trimmed && _exporter.RequiresVideo
                ? "재인코딩 없이 자르면 시작 위치가 가까운 키프레임으로 맞춰져 조금 앞에서 시작할 수 있습니다. 정확히 자르려면 체크를 해제하세요."
                : "아래 설정은 재인코딩할 때만 적용됩니다."
            : _exporter.RequiresVideo && _exporter.DefaultFrameRate is not null
                ? "해상도·프레임 레이트를 낮출수록 파일이 작아집니다."
                : "";
        HintText.IsVisible = HintText.Text.Length > 0;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var bitrates = _exporter.AudioBitrates;
        Close(new MediaExportOptions
        {
            CopyStreams = CopyBox.IsVisible && CopyBox.IsChecked == true,
            AudioBitrateKbps = bitrates.Count > 0 ? bitrates[BitrateBox.SelectedIndex] : null,
            VideoCodec = _codecs.Count > 0 ? _codecs[Math.Max(0, CodecBox.SelectedIndex)].Name : null,
            Quality = Qualities[Math.Max(0, QualityBox.SelectedIndex)].Value,
            MaxResolution = _resolutions[Math.Max(0, ResolutionBox.SelectedIndex)],
            FrameRate = _frameRates[Math.Max(0, FrameRateBox.SelectedIndex)],
        });
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
