using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ExtConvert.Controls;
using ExtConvert.Conversion;
using ExtConvert.Media;
using Xunit;

namespace ExtConvert.Tests;

/// <summary>구간 선택 모드: 파형에서 고른 구간만 저장/변환.</summary>
public class RangeTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private static void RequireFFmpeg() =>
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");

    /// <summary>앞 1초는 무음, 뒤 2초는 사인파(진폭 0.5)인 3초짜리 WAV.</summary>
    private async Task<string> CreateSilenceThenToneAsync()
    {
        var path = Path.Combine(fx.Dir, "range_source.wav");
        if (!File.Exists(path))
            await FFmpeg.ConvertAsync(
            [
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "aevalsrc='if(gte(t,1),0.5*sin(2*PI*440*t),0)':s=44100:d=3",
                path,
            ], null);
        return path;
    }

    [Theory]
    [InlineData("45", 45)]
    [InlineData("1:23.4", 83.4)]
    [InlineData("01:02:03", 3723)]
    [InlineData(" 0:05,5 ", 5.5)]
    public void TryParseTime_AcceptsCommonForms(string text, double seconds)
    {
        Assert.True(MainWindow.TryParseTime(text, out var t));
        Assert.Equal(seconds, t.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1:2:3:4")]
    [InlineData("1.5:00")]
    public void TryParseTime_RejectsInvalid(string text) =>
        Assert.False(MainWindow.TryParseTime(text, out _));

    [Fact]
    public void Range_IsPlacedBeforeInput()
    {
        var mp3 = Exporters.Media.FindByExtension("mp3")!;
        var args = mp3.BuildArguments("in.wav", "out.mp3", new MediaInfo(), new MediaExportOptions
        {
            Range = new MediaRange(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.25)),
        });

        var input = args.ToList().IndexOf("-i");
        Assert.Equal(["-ss", "1.5", "-t", "2.25"], args.Skip(input - 4).Take(4));
    }

    [Theory]
    [InlineData("mp3", false)]
    [InlineData("flac", false)]
    [InlineData("wav", true)] // 같은 코덱(pcm_s16le)이면 재인코딩 없이 잘라내기
    public async Task Range_SavesOnlyThatPart(string ext, bool copy)
    {
        RequireFFmpeg();
        var ct = TestContext.Current.CancellationToken;
        var source = await CreateSilenceThenToneAsync();
        var info = await FFmpeg.ProbeAsync(source, ct);
        var exporter = Exporters.Media.FindByExtension(ext)!;
        var output = Path.Combine(fx.Dir, $"range_out.{ext}");

        // 1.0~2.5초: 무음이 끝나고 소리가 시작되는 지점부터 1.5초
        var range = new MediaRange(TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.5));
        await FFmpeg.ConvertAsync(exporter.BuildArguments(source, output, info,
            new MediaExportOptions { Range = range, CopyStreams = copy }), range.Length, null, ct);

        var result = await FFmpeg.ProbeAsync(output, ct);
        Assert.InRange(result.Duration!.Value.TotalSeconds, 1.45, 1.6);

        // 잘라낸 파일은 처음부터 소리가 나야 한다 (앞쪽 무음 구간이 빠졌는지)
        var waveform = await Waveform.ExtractAsync(output, result.Duration, ct: ct);
        var head = Enumerable.Range(0, waveform.Count / 10).Max(i => waveform.Max(i));
        Assert.True(head > 0.3f, $"앞부분 최대값 {head}");
    }

    // ───── 파형 위 마우스 동작 ─────

    private static async Task<(Window Window, WaveformView View)> ShowWaveformAsync(string path)
    {
        var view = new WaveformView
        {
            Waveform = await Waveform.ExtractAsync(path, TimeSpan.FromSeconds(3)),
            IsSelectionMode = true,
        };
        var window = new Window { Width = 400, Height = 100, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static void Drag(Window w, double from, double to, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        w.MouseDown(new Point(from, 50), MouseButton.Left, modifiers);
        w.MouseMove(new Point((from + to) / 2, 50), modifiers);
        w.MouseMove(new Point(to, 50), modifiers);
        w.MouseUp(new Point(to, 50), MouseButton.Left, modifiers);
    }

    [AvaloniaFact]
    public async Task SelectionMode_DragSelectsAndEdgesAdjust()
    {
        RequireFFmpeg();
        var (window, view) = await ShowWaveformAsync(await CreateSilenceThenToneAsync());
        double? sought = null;
        view.SeekRequested += (_, f) => sought = f;

        Drag(window, 300, 100); // 오른쪽에서 왼쪽으로 끌어도 Start < End
        Assert.Equal(0.25, view.Selection!.Value.Start, 2);
        Assert.Equal(0.75, view.Selection!.Value.End, 2);
        Assert.Null(sought); // 드래그는 탐색이 아님

        Drag(window, 300, 350); // 오른쪽 끝을 잡아서 늘리기
        Assert.Equal(0.875, view.Selection!.Value.End, 2);

        Drag(window, 100, 380); // 왼쪽 끝을 오른쪽 끝 너머로 → 끝이 뒤바뀜
        Assert.Equal(0.875, view.Selection!.Value.Start, 2);
        Assert.Equal(0.95, view.Selection!.Value.End, 2);

        window.MouseDown(new Point(200, 50), MouseButton.Left); // 끌지 않고 클릭 = 탐색, 구간 유지
        window.MouseUp(new Point(200, 50), MouseButton.Left);
        Assert.Equal(0.5, sought!.Value, 2);
        Assert.Equal(0.875, view.Selection!.Value.Start, 2);

        window.Close();
    }

    [AvaloniaFact]
    public async Task NormalMode_DragSeeksInsteadOfSelecting()
    {
        RequireFFmpeg();
        var (window, view) = await ShowWaveformAsync(await CreateSilenceThenToneAsync());
        view.IsSelectionMode = false;
        double? sought = null;
        view.SeekRequested += (_, f) => sought = f;

        Drag(window, 100, 300);
        Assert.Null(view.Selection);
        Assert.Equal(0.75, sought!.Value, 2);
        window.Close();
    }

    // ───── 메인 창 ─────

    [AvaloniaFact]
    public async Task MainWindow_RangeModeIsOffByDefaultAndTogglesSaveTarget()
    {
        RequireFFmpeg();
        var window = new MainWindow();
        window.Show();
        await window.LoadFileAsync(await CreateSilenceThenToneAsync());
        await window.WaveformLoading;

        var toggle = window.FindControl<ToggleButton>("RangeModeButton")!;
        var fields = window.FindControl<Control>("RangeFields")!;
        var view = window.FindControl<WaveformView>("WaveformView")!;
        var saveAs = window.FindControl<MenuItem>("SaveAsMenuItem")!;
        var menuToggle = window.FindControl<MenuItem>("RangeModeMenuItem")!;

        // 기본: 전체 변환
        Assert.True(toggle.IsEnabled);
        Assert.False(toggle.IsChecked);
        Assert.False(fields.IsVisible);
        Assert.Equal("다른 이름으로 저장(_A)...", saveAs.Header);

        // 켜면 전체가 선택된 상태로 시작 (아직은 전체 저장과 같음)
        toggle.IsChecked = true;
        Assert.True(fields.IsVisible);
        Assert.True(view.IsSelectionMode);
        Assert.Equal(new WaveformSelection(0, 1), view.Selection);
        Assert.True(menuToggle.IsChecked);
        Assert.Equal("다른 이름으로 저장(_A)...", saveAs.Header);

        // 구간을 줄이면 저장 메뉴가 "선택 구간" 저장으로 바뀌고 시작/끝 칸이 따라온다
        view.Selection = new WaveformSelection(0.25, 0.5);
        Assert.StartsWith("선택 구간", (string)saveAs.Header!);
        Assert.Equal("00:00.7", window.FindControl<TextBox>("RangeStartBox")!.Text);
        Assert.Equal("00:01.5", window.FindControl<TextBox>("RangeEndBox")!.Text);

        // 끝 칸에 시각을 입력하면 구간에 반영
        var endBox = window.FindControl<TextBox>("RangeEndBox")!;
        endBox.Text = "0:02.4";
        endBox.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Assert.Equal(0.8, view.Selection!.Value.End, 3);
        Assert.Equal("길이 00:01.6", window.FindControl<TextBlock>("RangeLengthText")!.Text);

        // 끄면 다시 전체 변환 (구간은 기억)
        toggle.IsChecked = false;
        Assert.False(fields.IsVisible);
        Assert.False(view.IsSelectionMode);
        Assert.Equal("다른 이름으로 저장(_A)...", saveAs.Header);

        // 다른 파일을 열면 모드와 구간 초기화 (동영상도 구간 선택 가능)
        toggle.IsChecked = true;
        await window.LoadFileAsync(fx.VideoWithAudio);
        Assert.False(toggle.IsChecked);
        Assert.Null(view.Selection);
        Assert.True(menuToggle.IsEnabled);
        window.Close();
    }
}
