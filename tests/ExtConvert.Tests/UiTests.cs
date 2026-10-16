using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ExtConvert.Media;
using Xunit;

namespace ExtConvert.Tests;

public class UiTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private static Rect BoundsInWindow(Visual v, Window w) =>
        new Rect(v.Bounds.Size).TransformToAABB(v.TransformToVisual(w)!.Value);

    /// <summary>
    /// 창 크기가 소수점 배율이 되는 경우에도 미리보기 배경이 이미지 밖으로 삐져나오지 않아야 한다
    /// (예전에는 Border와 Uniform Image의 반올림 차이로 가장자리에 1px 체커보드 줄이 보였음).
    /// </summary>
    [AvaloniaFact]
    public async Task Preview_BackgroundNeverExceedsImageWhenResized()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");
        var window = new MainWindow();
        window.Show();
        await window.LoadFileAsync(fx.VideoWithAudio);

        var background = window.FindControl<Border>("PreviewBackground")!;
        var image = window.FindControl<Image>("PreviewImage")!;

        foreach (var (w, h) in new[] { (480, 420), (503, 437), (777, 555), (401, 333), (1013, 611) })
        {
            window.Width = w;
            window.Height = h;
            Dispatcher.UIThread.RunJobs();

            var bg = BoundsInWindow(background, window);
            var img = BoundsInWindow(image, window);
            Assert.True(bg.Width > 0 && bg.Height > 0);
            Assert.Equal(bg.X, img.X, 3);
            Assert.Equal(bg.Y, img.Y, 3);
            Assert.Equal(bg.Width, img.Width, 3);
            Assert.Equal(bg.Height, img.Height, 3);
        }

        window.Close();
    }
}
