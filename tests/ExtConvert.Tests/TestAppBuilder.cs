using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(ExtConvert.Tests.TestAppBuilder))]

namespace ExtConvert.Tests;

public static class TestAppBuilder
{
    // 실제 Skia 렌더러를 사용해야 PNG/JPEG 인코딩과 리사이즈가 동작한다.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
