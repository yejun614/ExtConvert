using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ExtConvert.Controls;
using ExtConvert.Conversion;
using ExtConvert.Media;
using Xunit;

namespace ExtConvert.Tests;

/// <summary>이미지 영역 선택 모드: 고른 영역만 저장/변환.</summary>
public class CropTests(MediaFixture fx) : IClassFixture<MediaFixture>
{
    [AvaloniaFact]
    public void Crop_CopiesExactPixels()
    {
        using var source = TestImages.Create(40, 10, transparentLeft: true); // 왼쪽 20px 투명, 오른쪽 빨강
        using var right = ImageOps.Crop(source, new PixelRect(25, 2, 10, 5));
        using var left = ImageOps.Crop(source, new PixelRect(0, 0, 10, 10));

        Assert.Equal(new PixelSize(10, 5), right.PixelSize);
        Assert.Equal([0, 0, 255, 255], TestImages.Pixel(right, 0, 0));
        Assert.Equal(0, TestImages.Pixel(left, 9, 9)[3]);
    }

    /// <summary>72 DPI, 40×10. 왼쪽 절반 투명, 오른쪽 절반 빨강. DIP 크기(53.3×13.3)가 픽셀 크기와 다르다.</summary>
    private static WriteableBitmap Create72Dpi()
    {
        var source = new WriteableBitmap(new PixelSize(40, 10), new Vector(72, 72),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = source.Lock();
        var row = new byte[fb.RowBytes];
        for (var x = 20; x < 40; x++)
            (row[x * 4 + 2], row[x * 4 + 3]) = ((byte)255, (byte)255);
        for (var y = 0; y < 10; y++)
            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
        return source;
    }

    [AvaloniaFact]
    public void Crop_UsesPixelsEvenWhenImageDpiIsNot96()
    {
        using var source = Create72Dpi();
        using var edge = ImageOps.Crop(source, new PixelRect(18, 0, 4, 1)); // 투명 2px + 빨강 2px
        Assert.Equal(0, TestImages.Pixel(edge, 1, 0)[3]);
        Assert.Equal(255, TestImages.Pixel(edge, 2, 0)[3]);
    }

    /// <summary>JPEG/ICO 저장에 쓰는 RenderFit도 DPI와 관계없이 이미지 전체를 그려야 한다 (예전에는 오른쪽 아래가 잘렸음).</summary>
    [AvaloniaFact]
    public void RenderFit_UsesPixelsEvenWhenImageDpiIsNot96()
    {
        using var source = Create72Dpi();
        using var fit = ImageOps.RenderFit(source, 40, 10, Avalonia.Media.Colors.White);
        Assert.Equal([0, 0, 255, 255], TestImages.Pixel(fit, 39, 9));
        Assert.Equal([255, 255, 255, 255], TestImages.Pixel(fit, 0, 0));
    }

    [Theory]
    [InlineData(0, 0, 200, 100, 1.0, 50, 0, 100, 100)]
    [InlineData(0, 0, 200, 100, 16 / 9.0, 11, 0, 178, 100)]
    [InlineData(10, 10, 90, 160, 4 / 3.0, 10, 56, 90, 68)]
    public void FitRatio_LargestCenteredRect(int x, int y, int w, int h, double ratio, int ex, int ey, int ew, int eh) =>
        Assert.Equal(new PixelRect(ex, ey, ew, eh), CropOverlay.FitRatio(new PixelRect(x, y, w, h), ratio));

    // ───── 마우스 동작: 200×100 이미지를 400×200으로 확대해 표시 (화면 2px = 이미지 1px) ─────

    private static (Window Window, CropOverlay Overlay) ShowOverlay()
    {
        var bitmap = TestImages.Create(200, 100);
        var image = new Image
        {
            Source = bitmap, Width = 400, Height = 200, Stretch = Avalonia.Media.Stretch.Fill,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        var overlay = new CropOverlay { Target = image, ImageSize = bitmap.PixelSize };
        var window = new Window { Width = 400, Height = 200, Content = new Panel { Children = { image, overlay } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, overlay);
    }

    private static void Drag(Window w, Point from, Point to)
    {
        w.MouseDown(from, MouseButton.Left);
        w.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        w.MouseMove(to);
        w.MouseUp(to, MouseButton.Left);
    }

    [AvaloniaFact]
    public void Overlay_MoveResizeAndCreate()
    {
        var (window, overlay) = ShowOverlay();
        overlay.Selection = new PixelRect(50, 25, 100, 50); // 화면 (100,50)-(300,150)

        Drag(window, new Point(200, 100), new Point(240, 120)); // 안쪽 끌기 = 이동
        Assert.Equal(new PixelRect(70, 35, 100, 50), overlay.Selection);

        Drag(window, new Point(340, 170), new Point(380, 190)); // 오른쪽 아래 손잡이 = 크기 조절
        Assert.Equal(new PixelRect(70, 35, 120, 60), overlay.Selection);

        Drag(window, new Point(140, 130), new Point(100, 130)); // 왼쪽 변 손잡이(세로 가운데) = 가로만
        Assert.Equal(new PixelRect(50, 35, 140, 60), overlay.Selection);

        Drag(window, new Point(60, 40), new Point(20, 20)); // 바깥 끌기 = 새 영역 (반대 방향도 가능)
        Assert.Equal(new PixelRect(10, 10, 20, 10), overlay.Selection);

        window.MouseDown(new Point(380, 190), MouseButton.Left); // 바깥 클릭만으로는 영역이 사라지지 않음
        window.MouseUp(new Point(380, 190), MouseButton.Left);
        Assert.Equal(new PixelRect(10, 10, 20, 10), overlay.Selection);

        Drag(window, new Point(300, 100), new Point(600, 400)); // 이미지 밖까지 끌어도 이미지 안으로 제한
        Assert.Equal(new PixelRect(150, 50, 50, 50), overlay.Selection);
        window.Close();
    }

    [AvaloniaFact]
    public void Overlay_AspectRatioIsKept()
    {
        var (window, overlay) = ShowOverlay();
        overlay.Selection = new PixelRect(10, 10, 20, 10);

        overlay.AspectRatio = 1; // 지금 영역 안에서 가장 큰 정사각형
        Assert.Equal(new PixelRect(15, 10, 10, 10), overlay.Selection);

        Drag(window, new Point(20, 140), new Point(100, 160)); // 가로로 많이 끌면 세로도 같이
        Assert.Equal(new PixelRect(10, 70, 30, 30), overlay.Selection); // 아래쪽 이미지 끝(100)에서 멈춤
        window.Close();
    }

    [AvaloniaFact]
    public void Overlay_SelectionIsClampedToImage()
    {
        var (window, overlay) = ShowOverlay();
        overlay.Selection = new PixelRect(190, 90, 50, 50);
        Assert.Equal(new PixelRect(190, 90, 10, 10), overlay.Selection);
        overlay.Selection = new PixelRect(300, 0, 10, 10);
        Assert.Equal(new PixelRect(199, 0, 1, 10), overlay.Selection);
        window.Close();
    }

    // ───── 메인 창 ─────

    [AvaloniaFact]
    public async Task MainWindow_CropModeIsOffByDefaultAndTogglesSaveTarget()
    {
        var png = Path.Combine(Path.GetTempPath(), $"ExtConvertTests_{Guid.NewGuid():N}.png");
        using (var bitmap = TestImages.Create(64, 32))
            bitmap.Save(png, PngBitmapEncoderOptions.Default);
        try
        {
            var window = new MainWindow { Width = 800, Height = 600 };
            window.Show();
            await window.LoadFileAsync(png);

            var toggle = window.FindControl<ToggleButton>("CropModeButton")!;
            var overlay = window.FindControl<CropOverlay>("CropOverlay")!;
            var fields = window.FindControl<Control>("CropFields")!;
            var saveAs = window.FindControl<MenuItem>("SaveAsMenuItem")!;
            var menuToggle = window.FindControl<MenuItem>("CropModeMenuItem")!;

            // 기본: 전체 변환
            Assert.True(window.FindControl<Control>("EditBar")!.IsVisible);
            Assert.False(window.FindControl<Control>("RangeModeButton")!.IsVisible); // 이미지에는 구간이 없다
            Assert.False(toggle.IsChecked);
            Assert.False(overlay.IsVisible);
            Assert.True(menuToggle.IsEnabled);

            // 켜면 전체가 선택된 상태로 시작
            toggle.IsChecked = true;
            Assert.True(overlay.IsVisible);
            Assert.True(fields.IsVisible);
            Assert.Equal(new PixelRect(0, 0, 64, 32), overlay.Selection);
            Assert.Equal("다른 이름으로 저장(_A)...", saveAs.Header);

            // 영역을 줄이면 저장 메뉴가 바뀌고 칸에 값이 들어간다
            overlay.Selection = new PixelRect(8, 4, 32, 16);
            Assert.StartsWith("선택 영역", (string)saveAs.Header!);
            Assert.Equal(32, window.FindControl<NumericUpDown>("CropWidthBox")!.Value);

            // 칸에 입력하면 영역에 반영 (이미지 밖은 잘림)
            window.FindControl<NumericUpDown>("CropWidthBox")!.Value = 100;
            Assert.Equal(new PixelRect(8, 4, 56, 16), overlay.Selection);
            Assert.Equal(56, window.FindControl<NumericUpDown>("CropWidthBox")!.Value);

            // 비율 고정
            window.FindControl<ComboBox>("CropRatioBox")!.SelectedIndex = 2; // 1:1
            Assert.Equal(overlay.Selection!.Value.Width, overlay.Selection!.Value.Height);

            // [변환] 메뉴의 모드 항목으로도 끄고 켤 수 있다
            Assert.True(menuToggle.IsChecked);
            toggle.IsChecked = false;
            Assert.False(overlay.IsVisible);
            Assert.False(menuToggle.IsChecked);
            Assert.Equal("다른 이름으로 저장(_A)...", saveAs.Header);

            // 다른 파일(동영상)을 열면 모드와 영역 초기화, 좌표계는 동영상 크기로
            if (FFmpeg.IsAvailable)
            {
                toggle.IsChecked = true;
                await window.LoadFileAsync(fx.VideoWithAudio);
                Assert.False(toggle.IsChecked);
                Assert.Null(overlay.Selection);
                Assert.Equal(new PixelSize(320, 240), overlay.ImageSize);
            }
            window.Close();
        }
        finally
        {
            File.Delete(png);
        }
    }
}
