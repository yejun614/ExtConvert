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

    [AvaloniaFact]
    public async Task MediaOptionsWindow_OpensForEveryExporter()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");
        var info = await FFmpeg.ProbeAsync(fx.VideoWithAudio);

        foreach (var exporter in Conversion.Exporters.Media.Where(e => e.IsAvailable))
        {
            var dialog = new MediaOptionsWindow(exporter, info);
            dialog.Show();
            Dispatcher.UIThread.RunJobs();

            // 동영상 옵션은 동영상 변환기에서만, 원본(320×240)보다 큰 해상도는 목록에 없음
            var resolution = dialog.FindControl<ComboBox>("ResolutionBox")!;
            Assert.Equal(exporter.RequiresVideo, dialog.FindControl<Grid>("ResolutionRow")!.IsVisible);
            Assert.DoesNotContain("480p", resolution.Items.Cast<string>());
            Assert.Equal(exporter.CanCopy(info), dialog.FindControl<CheckBox>("CopyBox")!.IsVisible);
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public async Task AnimatedGif_OpensAsPlayableMedia()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");
        var gif = Path.Combine(fx.Dir, "ui_anim.gif");
        await FFmpeg.ConvertAsync(["-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", fx.VideoOnly, gif], null);

        var window = new MainWindow();
        window.Show();
        await window.LoadFileAsync(gif);

        Assert.True(window.FindControl<Grid>("PlayerBar")!.IsVisible);
        Assert.True(window.FindControl<MenuItem>("SaveAsMenuItem")!.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FFmpegOnlyImage_OpensForPreview()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");
        var tif = Path.Combine(fx.Dir, "ui_image.tif");
        await FFmpeg.ConvertAsync(
            ["-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", fx.VideoOnly, "-frames:v", "1", "-update", "1", tif], null);

        var window = new MainWindow();
        window.Show();
        await window.LoadFileAsync(tif);

        Assert.True(window.FindControl<Control>("PreviewFrame")!.IsVisible);
        Assert.False(window.FindControl<Grid>("PlayerBar")!.IsVisible);
        Assert.Contains("160×120", window.FindControl<TextBlock>("StatusText")!.Text);
        window.Close();
    }
}
