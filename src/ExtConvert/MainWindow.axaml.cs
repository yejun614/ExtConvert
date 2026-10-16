using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ExtConvert.Conversion;
using ExtConvert.Media;
using Path = System.IO.Path;

namespace ExtConvert;

public partial class MainWindow : Window
{
    private const string EmptyStatus = "파일을 끌어다 놓거나 [파일 > 열기]로 선택하세요.";

    /// <summary>화면에 표시 중인 비트맵 (이미지 원본 또는 동영상 썸네일).</summary>
    private Bitmap? _bitmap;
    private string? _sourcePath;
    /// <summary>동영상/오디오 파일을 열었을 때만 non-null.</summary>
    private MediaInfo? _media;
    private CancellationTokenSource? _convertCts;
    /// <summary>파일을 연달아 열 때 늦게 끝난 이전 로드 결과를 버리기 위한 번호.</summary>
    private int _loadVersion;

    private bool IsConverting => _convertCts is not null;

    public MainWindow()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    // ───────────── 파일 불러오기 ─────────────

    public async Task LoadFileAsync(string path)
    {
        if (IsConverting)
        {
            SetStatus("변환 중에는 다른 파일을 열 수 없습니다.");
            return;
        }
        if (!File.Exists(path))
        {
            SetStatus($"파일을 찾을 수 없습니다: {path}");
            return;
        }

        ClearPreview();
        _sourcePath = path;
        var version = ++_loadVersion;
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        Title = $"{name} - Ext Convert";

        try
        {
            if (Exporters.ImageExtensions.Contains(ext))
                await LoadImageAsync(path, name, version);
            else if (Exporters.MediaExtensions.Contains(ext))
                await LoadMediaAsync(path, name, version);
            else
                ShowUnsupported(name, "아직 미리보기를 지원하지 않는 형식입니다.");
        }
        catch (Exception ex) when (version == _loadVersion)
        {
            ShowUnsupported(name, $"파일을 열 수 없습니다. ({ex.Message})");
        }
    }

    private async Task LoadImageAsync(string path, string name, int version)
    {
        // 큰 파일에서도 UI가 멈추지 않도록 디코딩은 백그라운드에서 수행
        var bitmap = await Task.Run(() => new Bitmap(path));
        if (version != _loadVersion)
        {
            bitmap.Dispose();
            return;
        }

        ShowBitmap(bitmap);
        SaveAsMenuItem.IsEnabled = true;
        SetStatus($"{name}  ·  {bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}  ·  {FormatFileSize(path)}");
    }

    private async Task LoadMediaAsync(string path, string name, int version)
    {
        if (!FFmpeg.IsAvailable)
        {
            ShowUnsupported(name, $"동영상/오디오를 열려면 FFmpeg가 필요합니다.\n{FFmpeg.InstallHint}");
            return;
        }

        Placeholder.IsVisible = false;
        SetStatus($"{name} - 미디어 정보를 읽는 중...");

        var info = await FFmpeg.ProbeAsync(path);
        Bitmap? thumbnail = null;
        if (info.HasVideo)
        {
            // 첫 프레임은 검은 화면인 경우가 많아 조금 뒤(10%, 최대 10초) 프레임을 사용
            var at = TimeSpan.FromSeconds(Math.Min((info.Duration?.TotalSeconds ?? 0) * 0.1, 10));
            if (await FFmpeg.ExtractFramePngAsync(path, at) is { } png)
                thumbnail = new Bitmap(new MemoryStream(png));
        }

        if (version != _loadVersion)
        {
            thumbnail?.Dispose();
            return;
        }

        _media = info;
        if (thumbnail is not null)
        {
            ShowBitmap(thumbnail);
        }
        else
        {
            AudioNameText.Text = name;
            AudioPlaceholder.IsVisible = true;
        }

        SaveAsMenuItem.IsEnabled = AvailableMediaExporters().Count > 0;
        SetStatus($"{name}  ·  {info.Summary()}  ·  {FormatFileSize(path)}"
                  + (info.HasAudio ? "" : "  ·  오디오 트랙 없음"));
    }

    private async Task OpenWithDialogAsync()
    {
        if (IsConverting)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "파일 열기",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("지원하는 모든 파일")
                    { Patterns = [..Exporters.ImageExtensions.Concat(Exporters.MediaExtensions).Select(e => $"*.{e}")] },
                new FilePickerFileType("이미지 파일") { Patterns = [..Exporters.ImageExtensions.Select(e => $"*.{e}")] },
                new FilePickerFileType("동영상/오디오 파일") { Patterns = [..Exporters.MediaExtensions.Select(e => $"*.{e}")] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await LoadFileAsync(path);
    }

    // ───────────── 다른 이름으로 저장 ─────────────

    private async Task SaveAsAsync()
    {
        if (IsConverting || _sourcePath is null)
            return;

        if (_media is not null)
            await SaveMediaAsAsync(_sourcePath, _media);
        else if (_bitmap is not null)
            await SaveImageAsAsync(_sourcePath, _bitmap);
    }

    private async Task SaveImageAsAsync(string sourcePath, Bitmap bitmap)
    {
        if (await PickSaveTargetAsync(Exporters.Images, sourcePath) is not (var file, var exporter))
            return;

        var options = new ExportOptions();
        if (exporter is IcoExporter)
        {
            var sizes = await new IcoOptionsWindow(bitmap.PixelSize).ShowDialog<int[]?>(this);
            if (sizes is null)
                return;
            options = options with { IcoSizes = sizes };
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek)
                stream.SetLength(0);
            exporter.Export(bitmap, stream, options);
            SetStatus($"저장 완료: {file.TryGetLocalPath() ?? file.Name}");
        }
        catch (Exception ex)
        {
            SetStatus($"저장 실패: {ex.Message}");
        }
    }

    private async Task SaveMediaAsAsync(string sourcePath, MediaInfo media)
    {
        if (await PickSaveTargetAsync(AvailableMediaExporters(), sourcePath) is not (var file, var exporter))
            return;

        // ffmpeg는 경로로 출력하므로 로컬 파일이어야 한다 (데스크톱에서는 항상 로컬)
        if (file.TryGetLocalPath() is not { } outputPath)
        {
            SetStatus("저장 실패: 로컬 경로를 확인할 수 없습니다.");
            return;
        }
        if (PathsEqual(outputPath, sourcePath))
        {
            SetStatus("저장 실패: 원본 파일에 덮어쓸 수 없습니다. 다른 이름을 선택하세요.");
            return;
        }

        var options = new MediaExportOptions();
        if (exporter is Mp3Exporter)
        {
            if (await new Mp3OptionsWindow().ShowDialog<MediaExportOptions?>(this) is not { } chosen)
                return;
            options = chosen;
        }

        var outputName = Path.GetFileName(outputPath);
        using var cts = new CancellationTokenSource();
        _convertCts = cts;
        SetConverting(true, indeterminate: media.Duration is null);
        SetStatus($"{outputName} 변환 중...");

        // Progress<T>는 UI 스레드에서 생성했으므로 콜백도 UI 스레드에서 실행된다
        var progress = new Progress<double>(p =>
        {
            ConvertProgress.Value = p;
            SetStatus($"{outputName} 변환 중... {p:P0}");
        });

        try
        {
            await FFmpeg.ConvertAsync(exporter.BuildArguments(sourcePath, outputPath, options),
                media.Duration, progress, cts.Token);
            SetStatus($"저장 완료: {outputPath}");
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            SetStatus("변환을 취소했습니다.");
        }
        catch (Exception ex)
        {
            TryDelete(outputPath);
            SetStatus($"저장 실패: {ex.Message}");
        }
        finally
        {
            _convertCts = null;
            SetConverting(false);
        }
    }

    /// <summary>저장 대화상자를 띄우고, 선택한 확장자에 맞는 변환기를 함께 돌려준다.</summary>
    private async Task<(IStorageFile File, T Exporter)?> PickSaveTargetAsync<T>(IReadOnlyList<T> exporters,
        string sourcePath) where T : IExporter
    {
        if (exporters.Count == 0)
            return null;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "다른 이름으로 저장",
            SuggestedFileName = Path.GetFileNameWithoutExtension(sourcePath),
            DefaultExtension = exporters[0].Extension,
            FileTypeChoices = exporters
                .Select(e => new FilePickerFileType($"{e.DisplayName} (*.{e.Extension})") { Patterns = [$"*.{e.Extension}"] })
                .ToList(),
            ShowOverwritePrompt = true,
        });
        if (file is null)
            return null;

        return (file, exporters.FindByExtension(Path.GetExtension(file.Name)) ?? exporters[0]);
    }

    private List<IMediaExporter> AvailableMediaExporters() =>
        _media is null ? [] : Exporters.Media.Where(e => !e.RequiresAudio || _media.HasAudio).ToList();

    // ───────────── 드래그 앤 드롭 ─────────────

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        UpdateDragEffects(e);
        if (e.DragEffects != DragDropEffects.None)
            DropBorder.Stroke = Brushes.DodgerBlue;
    }

    private void OnDragOver(object? sender, DragEventArgs e) => UpdateDragEffects(e);

    private void OnDragLeave(object? sender, DragEventArgs e) => ResetDropBorder();

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        ResetDropBorder();
        var path = e.DataTransfer.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .FirstOrDefault(p => p is not null);
        if (path is not null)
            await LoadFileAsync(path);
    }

    private void UpdateDragEffects(DragEventArgs e) =>
        e.DragEffects = !IsConverting && e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    private void ResetDropBorder() => DropBorder.ClearValue(Shape.StrokeProperty);

    // ───────────── 메뉴 / 입력 ─────────────

    private async void OnOpenClick(object? sender, RoutedEventArgs e) => await OpenWithDialogAsync();
    private async void OnSaveAsClick(object? sender, RoutedEventArgs e) => await SaveAsAsync();
    private void OnCloseFileClick(object? sender, RoutedEventArgs e) => ResetToEmpty();
    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();
    private void OnCancelConvertClick(object? sender, RoutedEventArgs e) => _convertCts?.Cancel();

    private async void OnDropAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_sourcePath is null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            await OpenWithDialogAsync();
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // macOS는 Cmd(Meta) 키를 사용
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!ctrl)
            return;

        if (e.Key == Key.O)
        {
            e.Handled = true;
            await OpenWithDialogAsync();
        }
        else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            await SaveAsAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // 변환 중에 창을 닫으면 ffmpeg 프로세스도 종료
        _convertCts?.Cancel();
        base.OnClosed(e);
    }

    // ───────────── 화면 상태 ─────────────

    private void ShowBitmap(Bitmap bitmap)
    {
        _bitmap = bitmap;
        PreviewImage.Source = bitmap;
        PreviewFrame.IsVisible = true;
        Placeholder.IsVisible = false;
    }

    private void ClearPreview()
    {
        PreviewImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _media = null;
        PreviewFrame.IsVisible = false;
        AudioPlaceholder.IsVisible = false;
        UnsupportedText.IsVisible = false;
        SaveAsMenuItem.IsEnabled = false;
    }

    private void ResetToEmpty()
    {
        if (IsConverting)
            return;

        _loadVersion++;
        ClearPreview();
        _sourcePath = null;
        Placeholder.IsVisible = true;
        Title = "Ext Convert";
        SetStatus(EmptyStatus);
    }

    private void SetConverting(bool converting, bool indeterminate = false)
    {
        ConvertProgress.Value = 0;
        ConvertProgress.IsIndeterminate = indeterminate;
        ConvertProgress.IsVisible = converting;
        CancelConvertButton.IsVisible = converting;
        OpenMenuItem.IsEnabled = !converting;
        CloseFileMenuItem.IsEnabled = !converting;
        SaveAsMenuItem.IsEnabled = !converting;
    }

    private void ShowUnsupported(string fileName, string reason)
    {
        Placeholder.IsVisible = false;
        UnsupportedText.Text = $"{fileName}\n\n{reason}";
        UnsupportedText.IsVisible = true;
        SetStatus($"{fileName} - {reason.Replace('\n', ' ')}");
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string FormatFileSize(string path) => new FileInfo(path).Length switch
    {
        < 1024 and var b => $"{b} B",
        < 1024 * 1024 and var b => $"{b / 1024.0:0.#} KB",
        var b => $"{b / (1024.0 * 1024):0.#} MB",
    };
}
