using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ExtConvert;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            // 명령줄 인자로 파일을 넘기면 바로 연다 (예: "연결 프로그램"으로 실행).
            if (desktop.Args is { Length: > 0 } args)
                window.Opened += async (_, _) => await window.LoadFileAsync(args[0]);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
