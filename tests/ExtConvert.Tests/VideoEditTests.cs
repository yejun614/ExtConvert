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

/// <summary>동영상: 시작/끝 구간 + 화면 영역을 골라서 저장/변환.</summary>
public class VideoEditTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private static void RequireFFmpeg() =>
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");

    private static string VideoFilter(IReadOnlyList<string> args) => args[args.ToList().IndexOf("-vf") + 1];

    [Fact]
    public void Crop_IsFirstFilterAndRoundedToEvenForYuv420()
    {
        var info = new MediaInfo { VideoCodec = "h264", Width = 320, Height = 240, FrameRate = 15, AudioCodec = "aac" };
        var crop = new CropRect(11, 21, 101, 61);

        var mp4 = Exporters.Media.FindByExtension("mp4")!;
        var args = mp4.BuildArguments("in.mp4", "out.mp4", info, new MediaExportOptions { Crop = crop, CopyStreams = true });
        Assert.StartsWith("crop=100:60:11:21", VideoFilter(args));
        // 화면을 자르면 재인코딩 없이 저장할 수 없다 (오디오는 원래대로 인코딩)
        Assert.NotEqual("copy", args[args.ToList().IndexOf("-c:v") + 1]);

        var gif = Exporters.Media.FindByExtension("gif")!; // GIF는 홀수 크기도 가능
        Assert.StartsWith("crop=101:61:11:21", VideoFilter(gif.BuildArguments("in.mp4", "out.gif", info,
            new MediaExportOptions { Crop = crop })));

        // 오디오 출력은 영역을 무시한다
        var mp3 = Exporters.Media.FindByExtension("mp3")!;
        Assert.DoesNotContain("-vf", mp3.BuildArguments("in.mp4", "out.mp3", info, new MediaExportOptions { Crop = crop }));
    }

    [Fact]
    public async Task RangeAndCrop_ProduceTrimmedCroppedVideo()
    {
        RequireFFmpeg();
        var ct = TestContext.Current.CancellationToken;
        var source = await FFmpeg.ProbeAsync(fx.VideoWithAudio, ct);
        var output = Path.Combine(fx.Dir, "edited.mp4");
        var options = new MediaExportOptions
        {
            Range = new MediaRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5)),
            Crop = new CropRect(10, 20, 101, 61),
        };

        await FFmpeg.ConvertAsync(Exporters.Media.FindByExtension("mp4")!
            .BuildArguments(fx.VideoWithAudio, output, source, options), options.Range!.Value.Length, null, ct);

        var result = await FFmpeg.ProbeAsync(output, ct);
        Assert.Equal((100, 60), (result.Width, result.Height));
        Assert.InRange(result.Duration!.Value.TotalSeconds, 1.4, 1.65);
        Assert.True(result.HasAudio);
    }

    [Fact]
    public async Task Range_ExtractsAudioPartFromVideo()
    {
        RequireFFmpeg();
        var ct = TestContext.Current.CancellationToken;
        var source = await FFmpeg.ProbeAsync(fx.VideoWithAudio, ct);
        var output = Path.Combine(fx.Dir, "part.m4a");
        var options = new MediaExportOptions { Range = new MediaRange(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2)) };

        await FFmpeg.ConvertAsync(Exporters.Media.FindByExtension("m4a")!
            .BuildArguments(fx.VideoWithAudio, output, source, options), TimeSpan.FromSeconds(2), null, ct);

        var result = await FFmpeg.ProbeAsync(output, ct);
        Assert.False(result.HasVideo);
        Assert.InRange(result.Duration!.Value.TotalSeconds, 1.9, 2.15);
    }

    [AvaloniaFact]
    public void Timeline_SelectsEvenWithoutWaveform()
    {
        // 오디오 없는 동영상의 타임라인은 파형이 없지만 구간은 고를 수 있어야 한다
        var view = new WaveformView { IsSelectionMode = true };
        var window = new Window { Width = 400, Height = 44, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.MouseDown(new Point(100, 20), MouseButton.Left);
        window.MouseMove(new Point(200, 20));
        window.MouseMove(new Point(300, 20));
        window.MouseUp(new Point(300, 20), MouseButton.Left);

        Assert.Equal(0.25, view.Selection!.Value.Start, 2);
        Assert.Equal(0.75, view.Selection!.Value.End, 2);
        window.Close();
    }

    [AvaloniaFact]
    public async Task MainWindow_VideoOffersRangeAndCropTogether()
    {
        RequireFFmpeg();
        var window = new MainWindow { Width = 900, Height = 700 };
        window.Show();
        await window.LoadFileAsync(fx.VideoWithAudio);

        var rangeToggle = window.FindControl<ToggleButton>("RangeModeButton")!;
        var cropToggle = window.FindControl<ToggleButton>("CropModeButton")!;
        var timeline = window.FindControl<WaveformView>("TimelineView")!;
        var overlay = window.FindControl<CropOverlay>("CropOverlay")!;
        var saveAs = window.FindControl<MenuItem>("SaveAsMenuItem")!;

        // 기본: 두 모드 모두 꺼져 있고(전체 변환) 버튼만 보인다
        Assert.True(window.FindControl<Control>("EditBar")!.IsVisible);
        Assert.True(rangeToggle.IsVisible);
        Assert.True(cropToggle.IsVisible);
        Assert.False(timeline.IsVisible);
        Assert.False(overlay.IsVisible);
        Assert.Equal("다른 이름으로 저장(_A)...", saveAs.Header);

        // 구간 선택: 재생 바 위에 타임라인이 나타나고, 오디오 트랙의 파형을 분석한다
        rangeToggle.IsChecked = true;
        Assert.True(timeline.IsVisible);
        Assert.True(timeline.IsSelectionMode);
        Assert.Equal(new WaveformSelection(0, 1), timeline.Selection);
        await window.WaveformLoading;
        Dispatcher.UIThread.RunJobs(); // 마지막 진행 보고(UI 스레드로 전달됨)까지 반영
        Assert.True(timeline.Waveform is { IsComplete: true, Count: > 0 });

        timeline.Selection = new WaveformSelection(0.5, 0.75);
        Assert.Equal("선택 구간을 다른 이름으로 저장(_A)...", saveAs.Header);

        // [ 버튼: 현재 위치(0초)를 시작으로
        window.FindControl<Button>("SetRangeStartButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new WaveformSelection(0, 0.75), timeline.Selection);

        // 영역 선택도 함께: 좌표계는 동영상 표시 크기
        cropToggle.IsChecked = true;
        Assert.True(overlay.IsVisible);
        Assert.Equal(new PixelRect(0, 0, 320, 240), overlay.Selection);
        overlay.Selection = new PixelRect(0, 0, 160, 120);
        Assert.Equal("선택 구간·영역을 다른 이름으로 저장(_A)...", saveAs.Header);
        Assert.Equal("변환 - 선택 구간·영역(_V)", window.FindControl<MenuItem>("ConvertMenu")!.Header);

        // 구간만 끄면 영역만 남는다
        rangeToggle.IsChecked = false;
        Assert.False(timeline.IsVisible);
        Assert.Equal("선택 영역을 다른 이름으로 저장(_A)...", saveAs.Header);

        // 오디오 파일을 열면 영역 선택 버튼은 사라지고, 동영상 타임라인은 초기화
        var wav = Path.Combine(fx.Dir, "video_edit_audio.wav");
        await FFmpeg.ConvertAsync(["-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-i", fx.VideoWithAudio, "-vn", wav], null);
        await window.LoadFileAsync(wav);
        Assert.True(rangeToggle.IsVisible);
        Assert.False(cropToggle.IsVisible);
        Assert.False(timeline.IsSelectionMode);
        Assert.Null(timeline.Selection);
        window.Close();
    }
}
