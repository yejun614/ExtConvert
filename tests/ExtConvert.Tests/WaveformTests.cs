using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ExtConvert.Controls;
using ExtConvert.Media;
using Xunit;

namespace ExtConvert.Tests;

public class WaveformTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private static void RequireFFmpeg() =>
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");

    /// <summary>앞 1초는 무음, 뒤 2초는 사인파(진폭 0.5)인 WAV.</summary>
    private async Task<string> CreateSilenceThenToneAsync()
    {
        var path = Path.Combine(fx.Dir, "silence_tone.wav");
        await FFmpeg.ConvertAsync(
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "aevalsrc='if(gte(t,1),0.5*sin(2*PI*440*t),0)':s=44100:d=3",
            path,
        ], null);
        return path;
    }

    [Fact]
    public async Task Extract_FindsSilenceAndToneAtTheRightPlace()
    {
        RequireFFmpeg();
        var ct = TestContext.Current.CancellationToken;
        var path = await CreateSilenceThenToneAsync();
        var info = await FFmpeg.ProbeAsync(path, ct);

        var reports = 0;
        var waveform = await Waveform.ExtractAsync(path, info.Duration, new SyncProgress(_ => reports++), ct);

        Assert.True(waveform.IsComplete);
        Assert.True(waveform.IsFixedLength);
        Assert.InRange(waveform.Count, Waveform.DefaultBuckets - 2, Waveform.DefaultBuckets);
        Assert.InRange(waveform.Peak, 0.45f, 0.55f);
        Assert.True(reports > 0);

        var n = waveform.TotalBuckets;
        // 앞 1/3 = 무음
        for (var i = 0; i < n / 3 - 10; i++)
            Assert.InRange(waveform.Max(i) - waveform.Min(i), 0f, 0.01f);
        // 뒤 2/3 = 위아래로 꽉 찬 사인파. 3초짜리라 구간 하나(6샘플)가 한 주기(약 18샘플)보다 짧으므로
        // 화면의 막대 하나처럼 여러 구간(20개 = 120샘플)을 묶어서 확인한다
        const int group = 20;
        for (var start = n / 3 + 10; start + group <= waveform.Count; start += group)
        {
            float min = 0, max = 0;
            for (var i = start; i < start + group; i++)
            {
                min = Math.Min(min, waveform.Min(i));
                max = Math.Max(max, waveform.Max(i));
            }
            Assert.InRange(max, 0.4f, 0.55f);
            Assert.InRange(min, -0.55f, -0.4f);
        }
    }

    [Fact]
    public async Task Extract_WithoutDuration_GrowsAsItGoes()
    {
        RequireFFmpeg();
        var path = await CreateSilenceThenToneAsync();
        var waveform = await Waveform.ExtractAsync(path, duration: null, ct: TestContext.Current.CancellationToken);

        Assert.False(waveform.IsFixedLength);
        Assert.InRange(waveform.Count, 295, 305); // 10ms 구간 × 3초
        Assert.Equal(waveform.Count, waveform.TotalBuckets);
    }

    [Fact]
    public async Task Extract_FromVideoWithoutAudio_YieldsNothing()
    {
        RequireFFmpeg();
        // 오디오 트랙이 없으면 ffmpeg가 실패하지만 예외는 InvalidOperationException 또는 빈 결과여야 한다
        var waveform = await Waveform.ExtractAsync(fx.VideoOnly, TimeSpan.FromSeconds(1),
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(0, waveform.Count);
    }

    [Fact]
    public async Task Extract_Cancelled_Throws()
    {
        RequireFFmpeg();
        var path = await CreateSilenceThenToneAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Waveform.ExtractAsync(path, null, null, new CancellationToken(canceled: true)));
    }

    [AvaloniaFact]
    public async Task WaveformView_ClickRaisesSeekAtPointerPosition()
    {
        RequireFFmpeg();
        var path = await CreateSilenceThenToneAsync();
        var view = new WaveformView
        {
            Waveform = await Waveform.ExtractAsync(path, TimeSpan.FromSeconds(3)),
        };
        var window = new Window { Width = 400, Height = 100, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        double? sought = null;
        view.SeekRequested += (_, f) => sought = f;
        window.MouseDown(new Point(100, 50), MouseButton.Left);
        window.MouseMove(new Point(300, 50));
        Assert.Null(sought); // 놓기 전에는 탐색하지 않음
        window.MouseUp(new Point(300, 50), MouseButton.Left);

        Assert.Equal(0.75, sought!.Value, 2);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AudioFile_ShowsWaveformInMainWindow()
    {
        RequireFFmpeg();
        var path = await CreateSilenceThenToneAsync();
        var window = new MainWindow();
        window.Show();
        await window.LoadFileAsync(path);
        await window.WaveformLoading;
        Dispatcher.UIThread.RunJobs();

        var view = window.FindControl<WaveformView>("WaveformView")!;
        Assert.True(view.IsEffectivelyVisible);
        Assert.True(view.Waveform is { IsComplete: true, Count: > 0 });
        Assert.True(view.Bounds.Height > 50);

        // 다른 파일(동영상)을 열면 파형은 사라진다
        await window.LoadFileAsync(fx.VideoWithAudio);
        Assert.False(view.IsEffectivelyVisible);
        Assert.Null(view.Waveform);
        window.Close();
    }

    private sealed class SyncProgress(Action<Waveform> report) : IProgress<Waveform>
    {
        public void Report(Waveform value) => report(value);
    }
}
