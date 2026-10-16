using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using ExtConvert.Media;
using Xunit;

namespace ExtConvert.Tests;

/// <summary>ffmpeg 내장 테스트 소스(lavfi)로 테스트용 미디어 파일을 만든다.</summary>
public sealed class MediaFixture : IAsyncLifetime
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "ExtConvertTests_" + Guid.NewGuid().ToString("N"));
    public string VideoWithAudio => Path.Combine(Dir, "with_audio.mp4");
    public string VideoOnly => Path.Combine(Dir, "video_only.mp4");

    public async ValueTask InitializeAsync()
    {
        if (!FFmpeg.IsAvailable)
            return;

        Directory.CreateDirectory(Dir);
        await FFmpeg.ConvertAsync(
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=duration=3:size=320x240:rate=15",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest",
            VideoWithAudio,
        ], null);
        await FFmpeg.ConvertAsync(
        [
            "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=duration=1:size=160x120:rate=15",
            "-c:v", "libx264", "-pix_fmt", "yuv420p",
            VideoOnly,
        ], null);
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}

public class MediaTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private static void RequireFFmpeg() =>
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");

    [Fact]
    public async Task Probe_ReadsVideoAndAudioInfo()
    {
        RequireFFmpeg();
        var info = await FFmpeg.ProbeAsync(fx.VideoWithAudio, TestContext.Current.CancellationToken);

        Assert.True(info.HasVideo);
        Assert.True(info.HasAudio);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal((320, 240), (info.Width, info.Height));
        Assert.Equal("aac", info.AudioCodec);
        Assert.InRange(info.Duration!.Value.TotalSeconds, 2.8, 3.3);
    }

    [AvaloniaFact]
    public async Task ExtractFrame_ReturnsDecodablePng()
    {
        RequireFFmpeg();
        var png = await FFmpeg.ExtractFramePngAsync(fx.VideoWithAudio, TimeSpan.FromSeconds(1));

        Assert.NotNull(png);
        using var bitmap = new Bitmap(new MemoryStream(png));
        Assert.Equal(new PixelSize(320, 240), bitmap.PixelSize);
    }

    [Fact]
    public async Task Mp3_ConvertsAudioTrackAndReportsProgress()
    {
        RequireFFmpeg();
        var ct = TestContext.Current.CancellationToken;
        var output = Path.Combine(fx.Dir, "out.mp3");
        var source = await FFmpeg.ProbeAsync(fx.VideoWithAudio, ct);

        var reports = new List<double>();
        var progress = new SyncProgress(reports.Add);
        var args = new Mp3Exporter().BuildArguments(fx.VideoWithAudio, output,
            new MediaExportOptions { AudioBitrateKbps = 128 });
        await FFmpeg.ConvertAsync(args, source.Duration, progress, ct);

        var result = await FFmpeg.ProbeAsync(output, ct);
        Assert.Equal("mp3", result.AudioCodec);
        Assert.False(result.HasVideo);
        Assert.InRange(result.Duration!.Value.TotalSeconds, 2.8, 3.3);
        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1]);
    }

    [Fact]
    public async Task Mp3_FromVideoWithoutAudio_FailsWithFFmpegError()
    {
        RequireFFmpeg();
        var output = Path.Combine(fx.Dir, "no_audio.mp3");
        var args = new Mp3Exporter().BuildArguments(fx.VideoOnly, output, new MediaExportOptions());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => FFmpeg.ConvertAsync(args, null, null, TestContext.Current.CancellationToken));
        Assert.StartsWith("ffmpeg 오류:", ex.Message);
    }

    [Fact]
    public async Task Convert_WhenCancelled_ThrowsOperationCanceled()
    {
        RequireFFmpeg();
        var output = Path.Combine(fx.Dir, "cancelled.mp3");
        var args = new Mp3Exporter().BuildArguments(fx.VideoWithAudio, output, new MediaExportOptions());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FFmpeg.ConvertAsync(args, null, null, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void MediaInfo_IgnoresAlbumArtAsVideo()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "audio", "codec_name": "mp3", "channels": 2, "sample_rate": "44100" },
                { "codec_type": "video", "codec_name": "mjpeg", "width": 500, "height": 500,
                  "disposition": { "attached_pic": 1 } }
              ],
              "format": { "duration": "65.5" }
            }
            """;

        var info = MediaInfo.Parse(json);
        Assert.False(info.HasVideo);
        Assert.True(info.HasAudio);
        Assert.Equal(TimeSpan.FromSeconds(65.5), info.Duration);
        Assert.Equal("01:05  ·  mp3 2ch 44.1kHz", info.Summary());
    }

    /// <summary>Progress&lt;T&gt;와 달리 콜백을 즉시 동기 호출해서 순서와 완료 시점이 확정된다.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
