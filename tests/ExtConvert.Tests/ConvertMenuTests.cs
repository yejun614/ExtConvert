using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using ExtConvert.Media;
using Xunit;

namespace ExtConvert.Tests;

/// <summary>[변환] 메뉴는 열린 파일로 만들 수 있는 형식만 활성화해야 한다.</summary>
public class ConvertMenuTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    private static MenuItem Menu(Window w, string name) => w.FindControl<MenuItem>(name)!;

    /// <summary>하위 메뉴에서 활성화된 항목의 확장자 목록. 헤더 형식: "PNG 이미지 (.png)".</summary>
    private static List<string> EnabledExtensions(MenuItem submenu) =>
        submenu.Items.OfType<MenuItem>()
            .Where(i => i.IsEnabled)
            .Select(i => ((string)i.Header!).Split("(.")[^1].TrimEnd(')'))
            .ToList();

    private static async Task<MainWindow> OpenAsync(string path)
    {
        var window = new MainWindow();
        window.Show();
        await window.LoadFileAsync(path);
        return window;
    }

    [AvaloniaFact]
    public void NoFile_EverythingDisabled()
    {
        var window = new MainWindow();
        window.Show();

        Assert.True(Menu(window, "ConvertMenu").IsEnabled);
        Assert.False(Menu(window, "ImageConvertMenu").IsEnabled);
        Assert.False(Menu(window, "VideoConvertMenu").IsEnabled);
        Assert.False(Menu(window, "AudioConvertMenu").IsEnabled);
        Assert.False(Menu(window, "SaveAsMenuItem").IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Image_OnlyImageFormatsEnabled()
    {
        var png = Path.Combine(Path.GetTempPath(), $"ExtConvertTests_{Guid.NewGuid():N}.png");
        using (var bitmap = TestImages.Create(32, 32))
            bitmap.Save(png, PngBitmapEncoderOptions.Default);
        try
        {
            var window = await OpenAsync(png);

            Assert.True(Menu(window, "ImageConvertMenu").IsEnabled);
            Assert.Contains("ico", EnabledExtensions(Menu(window, "ImageConvertMenu")));
            Assert.Contains("bmp", EnabledExtensions(Menu(window, "ImageConvertMenu")));
            Assert.False(Menu(window, "VideoConvertMenu").IsEnabled);
            Assert.False(Menu(window, "AudioConvertMenu").IsEnabled);
            Assert.Empty(EnabledExtensions(Menu(window, "VideoConvertMenu")));
            window.Close();
        }
        finally
        {
            File.Delete(png);
        }
    }

    [AvaloniaFact]
    public async Task VideoWithAudio_AllKindsEnabled()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");
        var window = await OpenAsync(fx.VideoWithAudio);

        Assert.Contains("mp4", EnabledExtensions(Menu(window, "VideoConvertMenu")));
        Assert.Contains("mp3", EnabledExtensions(Menu(window, "AudioConvertMenu")));
        Assert.Contains("png", EnabledExtensions(Menu(window, "ImageConvertMenu"))); // 현재 프레임
        Assert.Contains("현재 프레임", (string)Menu(window, "ImageConvertMenu").Header!);
        window.Close();
    }

    [AvaloniaFact]
    public async Task VideoWithoutAudio_AudioFormatsDisabled()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");
        var window = await OpenAsync(fx.VideoOnly);

        Assert.True(Menu(window, "VideoConvertMenu").IsEnabled);
        Assert.False(Menu(window, "AudioConvertMenu").IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AudioOnly_VideoAndImageFormatsDisabled()
    {
        Assert.SkipUnless(FFmpeg.IsAvailable, "ffmpeg/ffprobe가 설치되어 있지 않습니다.");
        var wav = Path.Combine(fx.Dir, "menu_audio.wav");
        await FFmpeg.ConvertAsync(["-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-i", fx.VideoWithAudio, "-vn", wav], null);
        var window = await OpenAsync(wav);

        Assert.False(Menu(window, "ImageConvertMenu").IsEnabled);
        Assert.False(Menu(window, "VideoConvertMenu").IsEnabled);
        Assert.Contains("flac", EnabledExtensions(Menu(window, "AudioConvertMenu")));

        // 파일을 닫으면 다시 모두 비활성화
        window.FindControl<MenuItem>("CloseFileMenuItem")!.RaiseEvent(
            new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.False(Menu(window, "AudioConvertMenu").IsEnabled);
        window.Close();
    }
}
