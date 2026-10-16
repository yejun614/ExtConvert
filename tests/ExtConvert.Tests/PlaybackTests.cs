using System.Diagnostics;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using ExtConvert.Media;
using ExtConvert.Media.Playback;
using Xunit;

namespace ExtConvert.Tests;

public class PlaybackTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private static void RequireFFmpeg() =>
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");

    private async Task<MediaPlayer> OpenAsync(string path) =>
        new(path, await FFmpeg.ProbeAsync(path, TestContext.Current.CancellationToken));

    /// <summary>UI 루프 대신 약 60Hz로 Tick을 돌린다. 새 프레임 수를 돌려준다.</summary>
    private static async Task<int> RunTicksAsync(MediaPlayer player, TimeSpan duration, Func<bool>? until = null)
    {
        var frames = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration && until?.Invoke() != true)
        {
            if (player.Tick())
                frames++;
            await Task.Delay(16);
        }
        return frames;
    }

    private static unsafe byte[] CenterPixel(WriteableBitmap bitmap)
    {
        using var fb = bitmap.Lock();
        var p = (byte*)fb.Address + fb.Size.Height / 2 * fb.RowBytes + fb.Size.Width / 2 * 4;
        return [p[0], p[1], p[2], p[3]];
    }

    [Theory]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(1080, 1920, 404, 720)] // 세로 영상
    [InlineData(320, 240, 320, 240)]   // 작으면 그대로
    [InlineData(321, 241, 320, 240)]   // 짝수로 맞춤
    public void FitSize_KeepsAspectWithinBounds(int w, int h, int ew, int eh) =>
        Assert.Equal((ew, eh), MediaPlayer.FitSize((w, h), 1280, 720));

    [AvaloniaFact]
    public async Task Play_AdvancesPositionAndRendersFrames()
    {
        RequireFFmpeg();
        using var player = await OpenAsync(fx.VideoWithAudio);
        Assert.NotNull(player.Frame);
        Assert.Equal(new PixelSize(320, 240), player.Frame!.PixelSize);

        player.Play();
        var frames = await RunTicksAsync(player, TimeSpan.FromSeconds(1.5));

        // 15fps 영상을 약 1.5초 재생 (시작 지연 고려)
        Assert.InRange(frames, 10, 25);
        Assert.InRange(player.Position.TotalSeconds, 0.8, 1.7);
        Assert.True(player.IsPlaying);
        Assert.Equal(255, CenterPixel(player.Frame)[3]);

        player.Pause();
        var paused = player.Position;
        await RunTicksAsync(player, TimeSpan.FromSeconds(0.3));
        Assert.Equal(paused.TotalSeconds, player.Position.TotalSeconds, 2);
    }

    [AvaloniaFact]
    public async Task Seek_WhilePaused_ShowsFrameAtNewPosition()
    {
        RequireFFmpeg();
        using var player = await OpenAsync(fx.VideoWithAudio);

        player.Seek(TimeSpan.FromSeconds(2));
        Assert.True(player.NeedsTick);
        var frames = await RunTicksAsync(player, TimeSpan.FromSeconds(3), () => !player.NeedsTick);

        Assert.Equal(1, frames);
        Assert.False(player.IsPlaying);
        Assert.Equal(2.0, player.Position.TotalSeconds, 2);
    }

    [AvaloniaFact]
    public async Task Play_NearEnd_StopsAndMarksEnded()
    {
        RequireFFmpeg();
        using var player = await OpenAsync(fx.VideoWithAudio);

        player.Seek(TimeSpan.FromSeconds(2.3));
        player.Play();
        await RunTicksAsync(player, TimeSpan.FromSeconds(4), () => player.IsEnded);

        Assert.True(player.IsEnded);
        Assert.False(player.IsPlaying);
        Assert.Equal(player.Duration, player.Position);

        // 끝난 뒤 재생하면 처음부터
        player.Play();
        Assert.True(player.Position < TimeSpan.FromSeconds(0.5));
    }

    [AvaloniaFact]
    public async Task Volume_IsClampedAndCanChangeDuringPlayback()
    {
        RequireFFmpeg();
        using var player = await OpenAsync(fx.VideoWithAudio);

        player.Volume = 1.5;
        Assert.Equal(1, player.Volume);
        player.Volume = -1;
        Assert.Equal(0, player.Volume);

        player.Play();
        await RunTicksAsync(player, TimeSpan.FromSeconds(0.3));
        player.Volume = 0.4;
        player.IsMuted = true;
        player.IsMuted = false;
        await RunTicksAsync(player, TimeSpan.FromSeconds(0.3));

        Assert.Equal(0.4, player.Volume);
        Assert.True(player.IsPlaying);
    }

    [AvaloniaFact]
    public async Task VideoWithoutAudio_PlaysOnWallClock()
    {
        RequireFFmpeg();
        using var player = await OpenAsync(fx.VideoOnly);
        Assert.True(player.CanPlay);

        player.Play();
        await RunTicksAsync(player, TimeSpan.FromSeconds(3), () => player.IsEnded);

        Assert.True(player.IsEnded);
    }
}
