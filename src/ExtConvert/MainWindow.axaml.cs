using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ExtConvert.Controls;
using ExtConvert.Conversion;
using ExtConvert.Media;
using ExtConvert.Media.Playback;
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

    private MediaPlayer? _player;
    /// <summary>화면에 썸네일 대신 재생기 프레임을 표시 중인지.</summary>
    private bool _showingPlayerFrame;
    private bool _playbackLoopRunning;
    private bool _seekDragging;
    private bool _updatingSeekSlider;
    private bool _updatingVolumeSlider;
    /// <summary>볼륨(0~1)과 음소거 상태. 다른 파일을 열어도 유지된다.</summary>
    private double _volume = 1;
    private bool _muted;
    /// <summary>[변환] 메뉴의 형식별 항목. 파일을 열거나 닫을 때 활성 상태를 다시 계산한다.</summary>
    private readonly List<(MenuItem Item, IExporter Exporter)> _convertItems = [];
    /// <summary>오디오 파형 분석 취소용. 다른 파일을 열거나 닫으면 취소한다.</summary>
    private CancellationTokenSource? _waveformCts;

    /// <summary>직전 화면 갱신 때의 재생 위치. 구간 끝을 "지나가는" 순간을 알아내기 위함.</summary>
    private TimeSpan _lastTickPosition;
    private bool _updatingRangeMode;
    private bool _updatingCropMode;
    private bool _updatingCropFields;

    /// <summary>[영역 선택]의 비율 고정 선택지. null이면 자유 비율.</summary>
    private static readonly (string Label, Func<PixelSize, double?> Ratio)[] CropRatios =
    [
        ("자유 비율", _ => null),
        ("원본 비율", s => (double)s.Width / s.Height),
        ("1:1", _ => 1),
        ("4:3", _ => 4 / 3.0),
        ("3:2", _ => 3 / 2.0),
        ("16:9", _ => 16 / 9.0),
        ("3:4", _ => 3 / 4.0),
        ("2:3", _ => 2 / 3.0),
        ("9:16", _ => 9 / 16.0),
    ];

    /// <summary>진행 중인 파형 분석 (테스트에서 완료를 기다리기 위함).</summary>
    internal Task WaveformLoading { get; private set; } = Task.CompletedTask;

    private bool IsConverting => _convertCts is not null;

    public MainWindow()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 슬라이더 썸이 이벤트를 처리하므로 Tunnel + handledEventsToo로 드래그 시작/끝을 감지
        SeekSlider.AddHandler(PointerPressedEvent, (_, _) => _seekDragging = true,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent, (_, _) => EndSeekDrag(),
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, (_, _) => EndSeekDrag(),
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        SeekSlider.ValueChanged += OnSeekSliderValueChanged;
        VolumeSlider.ValueChanged += OnVolumeSliderValueChanged;

        foreach (var timeline in new[] { WaveformView, TimelineView })
        {
            timeline.SeekRequested += (_, fraction) =>
            {
                if (_player?.Duration is { } d)
                    SeekTo(d * fraction);
            };
            timeline.SelectionChanged += (_, _) => UpdateRangeUi();
        }

        CropOverlay.Target = PreviewImage;
        CropOverlay.SelectionChanged += (_, _) => UpdateCropUi();
        CropRatioBox.ItemsSource = CropRatios.Select(r => r.Label).ToArray();
        CropRatioBox.SelectedIndex = 0;
        foreach (var box in new[] { CropXBox, CropYBox, CropWidthBox, CropHeightBox })
            box.ValueChanged += OnCropFieldChanged;

        BuildConvertMenu();
        UpdateMenuState();
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
            if (ext == "gif" && await TryProbeAnimationAsync(path) is { } animated)
            {
                // 애니메이션 GIF는 동영상처럼 재생하고 MP4/WebM/GIF 등으로 저장
                if (version == _loadVersion)
                    await LoadMediaAsync(path, name, version, animated);
            }
            else if (Exporters.NativeImageExtensions.Contains(ext))
                await LoadImageAsync(path, name, version);
            else if (Exporters.FFmpegImageExtensions.Contains(ext))
            {
                if (FFmpeg.IsAvailable)
                    await LoadImageAsync(path, name, version, useFFmpeg: true);
                else
                    ShowUnsupported(name, $"이 이미지 형식을 열려면 FFmpeg가 필요합니다.\n{FFmpeg.InstallHint}");
            }
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

    /// <summary>프레임이 여러 개인 애니메이션 이미지면 그 정보를, 아니면(또는 ffmpeg가 없으면) null.</summary>
    private static async Task<MediaInfo?> TryProbeAnimationAsync(string path)
    {
        if (!FFmpeg.IsAvailable)
            return null;
        try
        {
            return await FFmpeg.ProbeAsync(path) is { FrameCount: > 1 } info ? info : null;
        }
        catch (InvalidOperationException)
        {
            return null; // ffprobe가 못 읽으면 일반 이미지로 시도
        }
    }

    private async Task LoadImageAsync(string path, string name, int version, bool useFFmpeg = false)
    {
        Bitmap bitmap;
        if (useFFmpeg)
        {
            SetStatus($"{name} - 이미지를 읽는 중...");
            bitmap = new Bitmap(new MemoryStream(await FFmpeg.DecodeImagePngAsync(path)));
        }
        else
        {
            try
            {
                // 큰 파일에서도 UI가 멈추지 않도록 디코딩은 백그라운드에서 수행
                bitmap = await Task.Run(() => new Bitmap(path));
            }
            catch (Exception) when (FFmpeg.IsAvailable)
            {
                // 확장자는 익숙하지만 Skia가 모르는 변형이면 ffmpeg로 재시도
                bitmap = new Bitmap(new MemoryStream(await FFmpeg.DecodeImagePngAsync(path)));
            }
        }
        if (version != _loadVersion)
        {
            bitmap.Dispose();
            return;
        }

        ShowBitmap(bitmap);
        SetupCrop(bitmap.PixelSize);
        UpdateMenuState();
        SetStatus($"{name}  ·  {bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}  ·  {FormatFileSize(path)}");
    }

    private async Task LoadMediaAsync(string path, string name, int version, MediaInfo? probed = null)
    {
        if (!FFmpeg.IsAvailable)
        {
            ShowUnsupported(name, $"동영상/오디오를 열려면 FFmpeg가 필요합니다.\n{FFmpeg.InstallHint}");
            return;
        }

        Placeholder.IsVisible = false;
        SetStatus($"{name} - 미디어 정보를 읽는 중...");

        var info = probed ?? await FFmpeg.ProbeAsync(path);
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
            // 영상은 불투명하므로 체커보드 없이, 창이 크면 확대해서 보여준다
            ShowBitmap(thumbnail, isVideo: true);
            var (w, h) = info.DisplaySize;
            SetupCrop(new PixelSize(w, h));
        }
        else
        {
            AudioNameText.Text = name;
            AudioPlaceholder.IsVisible = true;
            if (info.HasAudio)
                WaveformLoading = LoadWaveformAsync(path, info, WaveformView);
        }

        // 볼륨/음소거는 파일을 바꿔도 유지
        _player = new MediaPlayer(path, info) { Volume = _volume, IsMuted = _muted };
        SetupPlayerBar(_player);

        UpdateMenuState();
        SetStatus($"{name}  ·  {info.Summary()}  ·  {FormatFileSize(path)}"
                  + (info.HasAudio ? "" : "  ·  오디오 트랙 없음")
                  + (_player.IsAudioOutputAvailable ? "" : "  ·  오디오 출력 장치를 사용할 수 없음"));
    }

    /// <summary>오디오 파형을 분석하면서 앞쪽부터 점진적으로 그린다. 실패해도 재생에는 영향 없음.</summary>
    private async Task LoadWaveformAsync(string path, MediaInfo info, WaveformView target)
    {
        var cts = new CancellationTokenSource();
        _waveformCts = cts;
        // Progress<T>는 UI 스레드에서 만들었으므로 콜백도 UI 스레드에서 실행된다
        var progress = new Progress<Waveform>(w =>
        {
            if (!cts.IsCancellationRequested)
                target.Waveform = w;
        });

        try
        {
            await Waveform.ExtractAsync(path, info.Duration, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            // 파형만 못 그릴 뿐 재생은 가능하므로 기준선만 남긴다
        }
    }

    // ───────────── 재생 ─────────────

    private void SetupPlayerBar(MediaPlayer player)
    {
        PlayerBar.IsVisible = true;
        PlayButton.IsEnabled = player.CanPlay;
        MuteButton.IsEnabled = _media?.HasAudio == true && player.IsAudioOutputAvailable;
        VolumeSlider.IsEnabled = MuteButton.IsEnabled;
        SeekSlider.IsEnabled = player.CanPlay && player.Duration is not null;
        SeekSlider.Maximum = Math.Max(player.Duration?.TotalSeconds ?? 1, 0.001);
        UpdatePlayerBar();
    }

    private void TogglePlay()
    {
        if (_player is not { CanPlay: true } player)
            return;
        // 구간 선택 모드에서 구간 밖(또는 끝)에서 재생하면 구간 처음부터
        if (!player.IsPlaying && SelectedRange is { } range
            && (player.Position < range.Start || player.Position >= range.End - TimeSpan.FromMilliseconds(50)))
            player.Seek(range.Start);
        player.TogglePlay();
        _lastTickPosition = player.Position;
        EnsurePlaybackLoop();
        UpdatePlayerBar();
    }

    private void SeekTo(TimeSpan position)
    {
        if (_player is not { CanPlay: true } player)
            return;
        player.Seek(position);
        EnsurePlaybackLoop();
        UpdatePlayerBar();
    }

    private void SeekBy(double seconds)
    {
        if (_player is not null)
            SeekTo(_player.Position + TimeSpan.FromSeconds(seconds));
    }

    private void ToggleMute()
    {
        if (_player is null || !MuteButton.IsEnabled)
            return;
        if (_muted || _volume == 0)
        {
            _muted = false;
            // 볼륨 0에서 음소거를 풀면 여전히 들리지 않으므로 절반으로 되돌린다
            if (_volume == 0)
                _volume = 0.5;
        }
        else
        {
            _muted = true;
        }
        _player.Volume = _volume;
        _player.IsMuted = _muted;
        UpdatePlayerBar();
    }

    private void SetVolume(double volume)
    {
        _volume = Math.Clamp(volume, 0, MediaPlayer.MaxVolume);
        // 볼륨을 조절하면 음소거를 해제 (일반적인 플레이어 동작)
        _muted = false;
        if (_player is not null)
        {
            _player.Volume = _volume;
            _player.IsMuted = false;
        }
        UpdatePlayerBar();
    }

    private void ChangeVolumeBy(double delta)
    {
        if (VolumeSlider.IsEnabled)
            SetVolume(_volume + delta);
    }

    private void OnVolumeSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingVolumeSlider)
            return;
        // 100% 근처는 정확히 100%로 맞춘다 (원본 음량으로 돌아가기 쉽게)
        var percent = Math.Abs(e.NewValue - 100) < 4 ? 100 : e.NewValue;
        SetVolume(percent / 100);
    }

    /// <summary>재생 중(또는 탐색 후 첫 프레임 대기 중)일 때만 화면 갱신마다 Tick을 호출한다.</summary>
    private void EnsurePlaybackLoop()
    {
        if (_playbackLoopRunning || _player is null)
            return;
        _playbackLoopRunning = true;
        RequestAnimationFrame(OnPlaybackFrame);
    }

    private void OnPlaybackFrame(TimeSpan _)
    {
        if (_player is not { } player)
        {
            _playbackLoopRunning = false;
            return;
        }

        if (player.Tick())
        {
            if (!_showingPlayerFrame)
            {
                // 첫 프레임이 나오면 썸네일 대신 재생기 비트맵을 표시
                PreviewImage.Source = player.Frame;
                PreviewBackground.Background = null;
                PreviewFrame.StretchDirection = StretchDirection.Both;
                PreviewFrame.IsVisible = true;
                _showingPlayerFrame = true;
            }
            PreviewImage.InvalidateVisual();
        }

        // 구간 선택 모드: 재생이 구간 끝을 지나가면 멈추고 구간 처음으로 (끝 뒤로 직접 탐색한 경우는 제외)
        var position = player.Position;
        if (player.IsPlaying && SelectedRange is { } range && _lastTickPosition < range.End && position >= range.End)
        {
            player.Pause();
            player.Seek(range.Start);
            position = range.Start;
        }
        _lastTickPosition = position;
        UpdatePlayerBar();

        if (player.NeedsTick)
            RequestAnimationFrame(OnPlaybackFrame);
        else
            _playbackLoopRunning = false;
    }

    private void UpdatePlayerBar()
    {
        if (_player is not { } player)
            return;

        PlayIcon.Data = (Geometry)Resources[player.IsPlaying ? "PauseGeometry" : "PlayGeometry"]!;
        var silent = _muted || _volume == 0;
        MuteIcon.Data = (Geometry)Resources[silent ? "MutedGeometry" : "VolumeGeometry"]!;
        _updatingVolumeSlider = true;
        VolumeSlider.Value = _muted ? 0 : _volume * 100;
        _updatingVolumeSlider = false;
        VolumeText.Text = _muted ? "음소거" : $"{Math.Round(_volume * 100)}%";
        // 원본보다 크게 낼 때는 강조색으로 표시 (소리가 찢어질 수 있다는 신호).
        // 평소에는 ClearValue로 테마 색을 되살린다 (null을 넣으면 글자가 아예 안 보임)
        if (!_muted && _volume > 1
            && this.TryFindResource("SystemControlHighlightAccentBrush", ActualThemeVariant, out var accent))
            VolumeText.Foreground = accent as IBrush;
        else
            VolumeText.ClearValue(TextBlock.ForegroundProperty);

        // 드래그 중에는 슬라이더 위치의 시간을 미리 보여준다
        var shown = _seekDragging ? TimeSpan.FromSeconds(SeekSlider.Value) : player.Position;
        TimeText.Text = player.Duration is { } d ? $"{FormatTime(shown)} / {FormatTime(d)}" : FormatTime(shown);

        if (player.Duration is { TotalSeconds: > 0 } total)
            Timeline.Progress = shown / total;

        if (!_seekDragging)
        {
            _updatingSeekSlider = true;
            SeekSlider.Value = player.Position.TotalSeconds;
            _updatingSeekSlider = false;
        }
    }

    private void OnSeekSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeekSlider)
            return;
        if (_seekDragging)
            UpdatePlayerBar(); // 시간 표시만 갱신, 실제 탐색은 놓을 때
        else
            SeekTo(TimeSpan.FromSeconds(e.NewValue));
    }

    private void EndSeekDrag()
    {
        if (!_seekDragging)
            return;
        _seekDragging = false;
        SeekTo(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

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
        if (await PickSaveTargetAsync(AvailableImageExporters(), sourcePath) is not (var file, var exporter))
            return;
        await ExportSourceImageAsync(file, exporter, bitmap);
    }

    /// <summary>열린 이미지를 저장한다. 영역 선택 모드면 고른 영역만 잘라서 저장.</summary>
    private async Task ExportSourceImageAsync(IStorageFile file, IImageExporter exporter, Bitmap bitmap)
    {
        if (SelectedCrop is not { } crop)
        {
            await ExportImageAsync(file, exporter, bitmap);
            return;
        }
        using var cropped = ImageOps.Crop(bitmap, crop);
        await ExportImageAsync(file, exporter, cropped, $"영역 {crop.Width}×{crop.Height}");
    }

    /// <summary>옵션을 묻고(ICO) 이미지를 저장한다. ffmpeg 기반 형식은 오래 걸릴 수 있어 취소 버튼을 보여준다.</summary>
    private async Task ExportImageAsync(IStorageFile file, IImageExporter exporter, Bitmap bitmap, string? note = null)
    {
        var options = new ExportOptions();
        if (exporter is IcoExporter)
        {
            var sizes = await new IcoOptionsWindow(bitmap.PixelSize).ShowDialog<int[]?>(this);
            if (sizes is null)
                return;
            options = options with { IcoSizes = sizes };
        }

        var outputName = file.TryGetLocalPath() ?? file.Name;
        using var cts = new CancellationTokenSource();
        _convertCts = cts;
        SetConverting(true, indeterminate: true);
        SetStatus($"{Path.GetFileName(outputName)} 저장 중...");
        var succeeded = false;
        try
        {
            await using (var stream = await file.OpenWriteAsync())
            {
                if (stream.CanSeek)
                    stream.SetLength(0);
                await exporter.ExportAsync(bitmap, stream, options, cts.Token);
            }
            succeeded = true;
            SetStatus(note is null ? $"저장 완료: {outputName}" : $"저장 완료 ({note}): {outputName}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("저장을 취소했습니다.");
        }
        catch (Exception ex)
        {
            SetStatus($"저장 실패: {ex.Message}");
        }
        finally
        {
            _convertCts = null;
            SetConverting(false);
            // 실패하면 비어 있거나 잘린 파일이 남지 않도록 지운다
            if (!succeeded && file.TryGetLocalPath() is { } local)
                TryDelete(local);
        }
    }

    private async Task SaveMediaAsAsync(string sourcePath, MediaInfo media)
    {
        var mediaExporters = AvailableMediaExporters();
        // 동영상은 현재 프레임을 이미지로도 저장할 수 있다 (GIF/WebP처럼 확장자가 겹치면 애니메이션 쪽 우선)
        var frameExporters = media.HasVideo
            ? AvailableImageExporters().Where(i => mediaExporters.FindByExtension(i.Extension) is null).ToList()
            : [];
        IReadOnlyList<IExporter> all = [..mediaExporters, ..frameExporters];

        if (await PickSaveTargetAsync(all, sourcePath, FrameLabel) is not (var file, var picked))
            return;

        if (picked is IImageExporter frameExporter)
            await SaveCurrentFrameAsync(file, frameExporter, sourcePath);
        else
            await ConvertMediaAsync(file, (IMediaExporter)picked, sourcePath, media);
    }

    /// <summary>[변환] 메뉴: 고른 형식 하나로만 저장 대화상자를 띄우고 변환한다.</summary>
    private async Task ConvertToAsync(IExporter exporter)
    {
        if (IsConverting || _sourcePath is not { } sourcePath)
            return;

        if (await PickSaveTargetAsync([exporter], sourcePath, FrameLabel) is not (var file, _))
            return;

        switch (exporter)
        {
            case IMediaExporter media when _media is not null:
                await ConvertMediaAsync(file, media, sourcePath, _media);
                break;
            case IImageExporter image when _media is not null:
                await SaveCurrentFrameAsync(file, image, sourcePath);
                break;
            case IImageExporter image when _bitmap is not null:
                await ExportSourceImageAsync(file, image, _bitmap);
                break;
        }
    }

    /// <summary>동영상을 열었을 때 이미지 형식은 "현재 프레임" 저장임을 표시한다.</summary>
    private string FrameLabel(IExporter e) =>
        e is IImageExporter && _media is not null ? $"{e.DisplayName} - 현재 프레임" : e.DisplayName;

    private async Task ConvertMediaAsync(IStorageFile file, IMediaExporter exporter, string sourcePath, MediaInfo media)
    {
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

        // 구간/영역 선택 모드면 그 부분만 (옵션 창을 띄우기 전 값으로 고정). 영역은 동영상 출력에만 의미가 있다.
        var range = SelectedRange;
        var crop = exporter.RequiresVideo ? SelectedCrop : null;

        // 옵션 창의 해상도 목록이 잘라낸 크기 기준이 되도록 원본 정보를 바꿔서 넘긴다
        var shown = crop is { } c0 ? media with { Width = c0.Width, Height = c0.Height, Rotation = 0 } : media;
        var options = new MediaExportOptions();
        if (MediaOptionsWindow.HasOptions(exporter, shown, allowCopy: crop is null))
        {
            var dialog = new MediaOptionsWindow(exporter, shown, allowCopy: crop is null, trimmed: range is not null);
            if (await dialog.ShowDialog<MediaExportOptions?>(this) is not { } chosen)
                return;
            options = chosen;
        }
        options = options with
        {
            Range = range,
            Crop = crop is { } c ? new CropRect(c.X, c.Y, c.Width, c.Height) : null,
        };
        var duration = range?.Length ?? media.Duration;

        var outputName = Path.GetFileName(outputPath);
        using var cts = new CancellationTokenSource();
        _convertCts = cts;
        SetConverting(true, indeterminate: duration is null);
        SetStatus($"{outputName} 변환 중...");

        // Progress<T>는 UI 스레드에서 생성했으므로 콜백도 UI 스레드에서 실행된다
        var progress = new Progress<double>(p =>
        {
            ConvertProgress.Value = p;
            SetStatus($"{outputName} 변환 중... {p:P0}");
        });

        try
        {
            await FFmpeg.ConvertAsync(exporter.BuildArguments(sourcePath, outputPath, media, options),
                duration, progress, cts.Token);
            var parts = new List<string>();
            if (range is { } r)
                parts.Add($"구간 {FormatPrecise(r.Start)} ~ {FormatPrecise(r.End)}");
            if (crop is { } cr)
                parts.Add($"영역 {cr.Width}×{cr.Height}");
            SetStatus(parts.Count > 0 ? $"저장 완료 ({string.Join(", ", parts)}): {outputPath}" : $"저장 완료: {outputPath}");
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

    /// <summary>재생 위치의 프레임을 원본 해상도로 추출해 이미지로 저장한다.</summary>
    private async Task SaveCurrentFrameAsync(IStorageFile file, IImageExporter exporter, string sourcePath)
    {
        if (_player is { IsPlaying: true })
            TogglePlay();
        var position = _player?.Position ?? TimeSpan.Zero;
        SetStatus("프레임을 추출하는 중...");

        byte[]? png;
        try
        {
            png = await FFmpeg.ExtractFramePngAsync(sourcePath, position, maxWidth: null);
        }
        catch (Exception ex)
        {
            SetStatus($"저장 실패: {ex.Message}");
            return;
        }
        if (png is null)
        {
            SetStatus("저장 실패: 프레임을 추출하지 못했습니다.");
            return;
        }

        using var frame = new Bitmap(new MemoryStream(png));
        await ExportSourceImageAsync(file, exporter, frame);
    }

    /// <summary>저장 대화상자를 띄우고, 선택한 확장자에 맞는 변환기를 함께 돌려준다.</summary>
    private async Task<(IStorageFile File, T Exporter)?> PickSaveTargetAsync<T>(IReadOnlyList<T> exporters,
        string sourcePath, Func<T, string>? label = null) where T : IExporter
    {
        if (exporters.Count == 0)
            return null;

        // 구간을 저장할 때는 이름에 구간을 붙여 원본과 구분 (예: song_0012-0045)
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var range = SelectedRange;
        var crop = SelectedCrop;
        static string Compact(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"hhmmss") : t.ToString(@"mmss");

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = SelectionLabel() is { } part ? $"선택 {part} 저장" : "다른 이름으로 저장",
            // 예: song_0012-0045, photo_640x480, clip_0012-0045_640x360
            SuggestedFileName = name
                                + (range is { } r ? $"_{Compact(r.Start)}-{Compact(r.End)}" : "")
                                + (crop is { } c ? $"_{c.Width}x{c.Height}" : ""),
            DefaultExtension = exporters[0].Extension,
            FileTypeChoices = exporters
                .Select(e => new FilePickerFileType($"{label?.Invoke(e) ?? e.DisplayName} (*.{e.Extension})")
                    { Patterns = [$"*.{e.Extension}"] })
                .ToList(),
            ShowOverwritePrompt = true,
        });
        if (file is null)
            return null;

        return (file, exporters.FindByExtension(Path.GetExtension(file.Name)) ?? exporters[0]);
    }

    private List<IMediaExporter> AvailableMediaExporters() =>
        _media is null
            ? []
            : Exporters.Media
                .Where(e => (!e.RequiresAudio || _media.HasAudio) && (!e.RequiresVideo || _media.HasVideo) && e.IsAvailable)
                .ToList();

    private static List<IImageExporter> AvailableImageExporters() =>
        Exporters.Images.Where(e => e.IsAvailable).ToList();

    // ───────────── 변환 메뉴 ─────────────

    private void BuildConvertMenu()
    {
        void Add(MenuItem parent, IExporter exporter)
        {
            var item = new MenuItem { Header = $"{exporter.DisplayName} (.{exporter.Extension})" };
            ToolTip.SetShowOnDisabled(item, true); // 인코더가 없어 비활성화된 이유를 보여주기 위함
            item.Click += async (_, _) => await ConvertToAsync(exporter);
            parent.Items.Add(item);
            _convertItems.Add((item, exporter));
        }

        foreach (var e in Exporters.Images)
            Add(ImageConvertMenu, e);
        foreach (var e in Exporters.Media.Where(e => e.RequiresVideo))
            Add(VideoConvertMenu, e);
        foreach (var e in Exporters.Media.Where(e => !e.RequiresVideo))
            Add(AudioConvertMenu, e);
    }

    /// <summary>열린 파일에서 이 형식으로 변환할 수 있는지 (ffmpeg 인코더 유무는 제외).</summary>
    private bool CanConvertTo(IExporter exporter) => exporter switch
    {
        // 이미지 → 이미지, 동영상 → 현재 프레임 이미지
        IImageExporter => _media is null ? _bitmap is not null : _media.HasVideo,
        IMediaExporter m => _media is not null
                            && (!m.RequiresAudio || _media.HasAudio)
                            && (!m.RequiresVideo || _media.HasVideo),
        _ => false,
    };

    private static bool IsAvailable(IExporter exporter) => exporter switch
    {
        IImageExporter i => i.IsAvailable,
        IMediaExporter m => m.IsAvailable,
        _ => false,
    };

    /// <summary>[다른 이름으로 저장]과 [변환] 메뉴의 활성 상태를 열린 파일·변환 중 여부에 맞춘다.</summary>
    private void UpdateMenuState()
    {
        var anyEnabled = false;
        foreach (var (item, exporter) in _convertItems)
        {
            var applicable = CanConvertTo(exporter);
            // 열린 파일과 관계없는 형식은 인코더 확인(ffmpeg 실행)을 하지 않는다
            var available = applicable && IsAvailable(exporter);
            item.IsEnabled = !IsConverting && available;
            ToolTip.SetTip(item, applicable && !available ? "설치된 ffmpeg에 이 형식의 인코더가 없습니다." : null);
            anyEnabled |= item.IsEnabled;
        }

        foreach (var submenu in new[] { ImageConvertMenu, VideoConvertMenu, AudioConvertMenu })
            submenu.IsEnabled = submenu.Items.OfType<MenuItem>().Any(i => i.IsEnabled);
        ImageConvertMenu.Header = _media is not null ? "이미지 - 현재 프레임(_I)" : "이미지(_I)";

        SaveAsMenuItem.IsEnabled = anyEnabled;

        // 구간 선택: 길이를 아는 오디오·동영상 / 영역 선택: 이미지·동영상
        var rangeApplies = _media is { } m && (m.HasAudio || m.HasVideo) && _player?.Duration is { TotalSeconds: > 0 };
        var cropApplies = (_media is null && _bitmap is not null) || (_media?.HasVideo == true && _bitmap is not null);
        var canRange = !IsConverting && rangeApplies;
        RangeModeButton.IsEnabled = canRange;

        CropModeButton.IsEnabled = cropApplies && !IsConverting;

        // 열린 파일에 맞는 버튼만 편집 바에 보인다 (동영상은 둘 다)
        RangeModeButton.IsVisible = rangeApplies;
        CropModeButton.IsVisible = cropApplies;
        EditBar.IsVisible = rangeApplies || cropApplies;

        RangeModeMenuItem.IsEnabled = canRange;
        RangeModeMenuItem.IsChecked = IsRangeMode;
        CropModeMenuItem.IsEnabled = CropModeButton.IsEnabled;
        CropModeMenuItem.IsChecked = IsCropMode;

        var part = SelectionLabel();
        SaveAsMenuItem.Header = part is null ? "다른 이름으로 저장(_A)..." : $"선택 {part}을 다른 이름으로 저장(_A)...";
        ConvertMenu.Header = part is null ? "변환(_V)" : $"변환 - 선택 {part}(_V)";
    }

    /// <summary>저장/변환에 적용될 선택: "구간", "영역", "구간·영역" 또는 null(전체).</summary>
    private string? SelectionLabel()
    {
        var parts = new List<string>();
        if (SelectedRange is not null)
            parts.Add("구간");
        if (SelectedCrop is not null)
            parts.Add("영역");
        return parts.Count > 0 ? string.Join("·", parts) : null;
    }

    // ───────────── 구간 선택 ─────────────

    private bool IsRangeMode => RangeModeButton.IsChecked == true;

    /// <summary>구간 선택 모드에서 고른 구간. 모드가 꺼져 있거나 전체가 선택되어 있으면 null (= 전체 변환).</summary>
    private MediaRange? SelectedRange =>
        IsRangeMode && Timeline.Selection is { } s && _player?.Duration is { } d && (s.Start > 0 || s.End < 1)
            ? new MediaRange(d * s.Start, d * s.Length)
            : null;

    private void OnRangeModeChanged(object? sender, RoutedEventArgs e) => SetRangeMode(RangeModeButton.IsChecked == true);

    private void OnRangeModeMenuClick(object? sender, RoutedEventArgs e) => SetRangeMode(!IsRangeMode);

    private void OnCropModeMenuClick(object? sender, RoutedEventArgs e) => SetCropMode(!IsCropMode);

    /// <summary>구간을 고르는 타임라인: 오디오는 큰 파형, 동영상은 재생 바 위의 얇은 타임라인.</summary>
    private WaveformView Timeline => _media?.HasVideo == true ? TimelineView : WaveformView;

    /// <summary>
    /// 구간 선택 모드를 켜고 끈다. 처음 켜면 전체가 선택된 상태에서 시작하므로 양 끝을 끌어 줄이거나 새로 끌어서 고른다.
    /// 끄면 전체 변환으로 돌아가며, 고른 구간은 기억했다가 다시 켤 때 보여준다.
    /// </summary>
    private void SetRangeMode(bool on, bool quiet = false)
    {
        if (_updatingRangeMode)
            return;
        _updatingRangeMode = true;
        RangeModeButton.IsChecked = on;
        _updatingRangeMode = false;

        // 두 타임라인 모두 명시적으로 설정 (파일을 닫는 중에는 _media가 이미 null이라 Timeline만으로는 부족)
        var video = _media?.HasVideo == true;
        WaveformView.IsSelectionMode = on && !video;
        TimelineView.IsSelectionMode = on && video;
        var timeline = Timeline;
        if (on && timeline.Selection is null)
            timeline.Selection = new WaveformSelection(0, 1);

        // 동영상은 구간 선택을 켤 때만 타임라인을 보여주고, 오디오 트랙이 있으면 그때 파형을 분석한다
        TimelineView.IsVisible = on && video;
        if (on && video && _media!.HasAudio && TimelineView.Waveform is null && _sourcePath is { } path
            && WaveformLoading.IsCompleted)
            WaveformLoading = LoadWaveformAsync(path, _media, TimelineView);
        UpdateRangeUi(refreshFocused: true);

        if (!quiet)
            SetStatus(on
                ? "구간 선택: 파형/타임라인을 끌어서 고르거나 양 끝을 끌어 조절하세요 ([ ] 또는 I/O로 현재 위치 지정). 저장/변환하면 이 구간만 저장됩니다."
                : "구간 선택을 껐습니다. 저장/변환하면 전체가 저장됩니다.");
    }

    private void UpdateRangeUi(bool refreshFocused = false)
    {
        RangeFields.IsVisible = IsRangeMode;
        if (IsRangeMode && Timeline.Selection is { } s && _player?.Duration is { } d)
        {
            var (start, end) = (d * s.Start, d * s.End);
            // 입력 중인 칸은 덮어쓰지 않는다
            if (refreshFocused || !RangeStartBox.IsFocused)
                RangeStartBox.Text = FormatPrecise(start);
            if (refreshFocused || !RangeEndBox.IsFocused)
                RangeEndBox.Text = FormatPrecise(end);
            RangeLengthText.Text = $"길이 {FormatPrecise(end - start)}";
        }
        UpdateMenuState();
    }

    private void OnSetRangeStartClick(object? sender, RoutedEventArgs e) => SetRangeEdgeToCurrent(start: true);
    private void OnSetRangeEndClick(object? sender, RoutedEventArgs e) => SetRangeEdgeToCurrent(start: false);

    /// <summary>현재 재생 위치를 구간의 시작 또는 끝으로. 반대쪽 끝을 넘으면 그쪽은 처음/끝으로 넓힌다.</summary>
    private void SetRangeEdgeToCurrent(bool start)
    {
        if (!IsRangeMode || _player?.Duration is not { TotalSeconds: > 0 } d || Timeline.Selection is not { } s)
            return;
        var f = Math.Clamp(_player.Position / d, 0, 1);
        var next = start
            ? new WaveformSelection(f, f < s.End ? s.End : 1)
            : new WaveformSelection(f > s.Start ? s.Start : 0, f);
        if (next.Length > 0)
            Timeline.Selection = next;
    }

    private void OnRangeBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape) || sender is not TextBox box)
            return;
        e.Handled = true;
        if (e.Key == Key.Enter)
            CommitRangeBox(box);
        else
            UpdateRangeUi(refreshFocused: true); // 입력 취소
        Focus(); // 칸에서 빠져나와 Space 등 재생 단축키가 다시 동작하도록
    }

    private void OnRangeBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
            CommitRangeBox(box);
    }

    /// <summary>시작/끝 칸에 입력한 시각을 구간에 반영한다. 잘못된 값이면 원래 값으로 되돌린다.</summary>
    private void CommitRangeBox(TextBox box)
    {
        if (_player?.Duration is { TotalSeconds: > 0 } d && Timeline.Selection is { } s
            && TryParseTime(box.Text, out var t))
        {
            var f = Math.Clamp(t / d, 0, 1);
            var next = box == RangeStartBox ? new WaveformSelection(f, s.End) : new WaveformSelection(s.Start, f);
            if (next.Length > 0)
                Timeline.Selection = next;
        }
        UpdateRangeUi(refreshFocused: true);
    }

    // ───────────── 이미지 영역 선택 ─────────────

    private bool IsCropMode => CropModeButton.IsChecked == true;

    /// <summary>영역 선택 모드에서 고른 영역 (이미지 픽셀). 모드가 꺼져 있거나 전체가 선택되어 있으면 null (= 전체 변환).</summary>
    private PixelRect? SelectedCrop =>
        IsCropMode && CropOverlay.Selection is { } s && s != new PixelRect(CropOverlay.ImageSize) ? s : null;

    private void OnCropModeChanged(object? sender, RoutedEventArgs e) => SetCropMode(CropModeButton.IsChecked == true);

    /// <summary>영역 선택 좌표계(이미지 픽셀 / 동영상 표시 크기)와 입력 칸 범위를 맞춘다.</summary>
    private void SetupCrop(PixelSize size)
    {
        CropOverlay.ImageSize = size;
        CropXBox.Maximum = size.Width - 1;
        CropYBox.Maximum = size.Height - 1;
        CropWidthBox.Maximum = size.Width;
        CropHeightBox.Maximum = size.Height;
    }

    /// <summary>
    /// 영역 선택 모드를 켜고 끈다. 처음 켜면 이미지 전체가 선택된 상태에서 시작한다.
    /// 끄면 전체 변환으로 돌아가며, 고른 영역은 기억했다가 다시 켤 때 보여준다.
    /// </summary>
    private void SetCropMode(bool on, bool quiet = false)
    {
        if (_updatingCropMode)
            return;
        _updatingCropMode = true;
        CropModeButton.IsChecked = on;
        _updatingCropMode = false;

        CropOverlay.IsVisible = on;
        if (on && CropOverlay.Selection is null)
            CropOverlay.Selection = new PixelRect(CropOverlay.ImageSize);
        UpdateCropUi();

        if (!quiet)
            SetStatus(on
                ? "영역 선택: 화면에서 끌어서 고르거나 테두리·손잡이를 끌어 조절하세요. 저장/변환하면 이 영역만 저장됩니다."
                : "영역 선택을 껐습니다. 저장/변환하면 화면 전체가 저장됩니다.");
    }

    private void UpdateCropUi()
    {
        CropFields.IsVisible = IsCropMode;
        if (CropOverlay.Selection is { } s)
        {
            _updatingCropFields = true;
            CropXBox.Value = s.X;
            CropYBox.Value = s.Y;
            CropWidthBox.Value = s.Width;
            CropHeightBox.Value = s.Height;
            _updatingCropFields = false;
        }
        UpdateMenuState();
    }

    /// <summary>X/Y/너비/높이 칸에 입력한 값을 영역에 반영. 비율이 고정되어 있으면 너비↔높이를 맞춘다.</summary>
    private void OnCropFieldChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_updatingCropFields || CropOverlay.Selection is null)
            return;

        int Read(NumericUpDown box) => (int)Math.Round(box.Value ?? 0);
        var (x, y, w, h) = (Read(CropXBox), Read(CropYBox), Read(CropWidthBox), Read(CropHeightBox));
        if (CropOverlay.AspectRatio is { } ratio)
        {
            if (sender == CropWidthBox)
                h = Math.Max(1, (int)Math.Round(w / ratio));
            else if (sender == CropHeightBox)
                w = Math.Max(1, (int)Math.Round(h * ratio));
        }

        CropOverlay.Selection = new PixelRect(x, y, Math.Max(1, w), Math.Max(1, h));
        UpdateCropUi(); // 이미지 밖으로 나가서 잘린 값을 칸에 다시 보여준다
    }

    private void OnCropRatioChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = Math.Max(0, CropRatioBox.SelectedIndex);
        CropOverlay.AspectRatio = CropRatios[index].Ratio(CropOverlay.ImageSize);
    }

    /// <summary>"1:23.4", "01:02:03", "45.5"처럼 [시:]분:초 또는 초만 입력한 시각을 읽는다.</summary>
    internal static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        var parts = (text ?? "").Trim().Replace(',', '.').Split(':');
        if (parts.Length is < 1 or > 3)
            return false;

        // 마지막 칸은 소수 초, 앞 칸(시·분)은 0 이상의 정수
        if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var total))
            return false;
        var multiplier = 60.0;
        for (var i = parts.Length - 2; i >= 0; i--, multiplier *= 60)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var unit))
                return false;
            total += unit * multiplier;
        }
        time = TimeSpan.FromSeconds(total);
        return true;
    }

    /// <summary>구간 표시용: 0.1초까지 (예: "01:23.4").</summary>
    private static string FormatPrecise(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"mm\:ss\.f");

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
    private void OnPlayClick(object? sender, RoutedEventArgs e) => TogglePlay();
    private void OnMuteClick(object? sender, RoutedEventArgs e) => ToggleMute();

    private async void OnDropAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (_player is not null)
            TogglePlay();
        else if (_sourcePath is null)
            await OpenWithDialogAsync();
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // macOS는 Cmd(Meta) 키를 사용
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!ctrl)
        {
            // 구간 시작/끝 칸에 입력 중일 때는 Space·화살표를 재생 단축키로 쓰지 않는다
            if (_player is null || e.KeyModifiers != KeyModifiers.None || e.Source is TextBox)
                return;
            switch (e.Key)
            {
                case Key.Space: TogglePlay(); break;
                case Key.Left: SeekBy(-5); break;
                case Key.Right: SeekBy(5); break;
                case Key.M: ToggleMute(); break;
                case Key.I when IsRangeMode: SetRangeEdgeToCurrent(start: true); break;
                case Key.O when IsRangeMode: SetRangeEdgeToCurrent(start: false); break;
                case Key.Up: ChangeVolumeBy(0.1); break;
                case Key.Down: ChangeVolumeBy(-0.1); break;
                default: return;
            }
            e.Handled = true;
            return;
        }

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
        _waveformCts?.Cancel();
        _player?.Dispose();
        _player = null;
        base.OnClosed(e);
    }

    // ───────────── 화면 상태 ─────────────

    private void ShowBitmap(Bitmap bitmap, bool isVideo = false)
    {
        _bitmap = bitmap;
        PreviewImage.Source = bitmap;
        PreviewBackground.Background = isVideo ? null : (IBrush)Resources["CheckerBrush"]!;
        PreviewFrame.StretchDirection = isVideo ? StretchDirection.Both : StretchDirection.DownOnly;
        PreviewFrame.IsVisible = true;
        Placeholder.IsVisible = false;
    }

    private void ClearPreview()
    {
        PreviewImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _media = null;
        _player?.Dispose();
        _player = null;
        _waveformCts?.Cancel();
        _waveformCts = null;
        foreach (var timeline in new[] { WaveformView, TimelineView })
        {
            timeline.Waveform = null;
            timeline.Progress = 0;
            timeline.Selection = null;
        }
        TimelineView.IsVisible = false;
        SetRangeMode(false, quiet: true);
        _lastTickPosition = TimeSpan.Zero;
        _showingPlayerFrame = false;
        _seekDragging = false;
        PlayerBar.IsVisible = false;
        PreviewFrame.IsVisible = false;
        AudioPlaceholder.IsVisible = false;
        EditBar.IsVisible = false;
        CropOverlay.Selection = null;
        CropRatioBox.SelectedIndex = 0;
        SetCropMode(false, quiet: true);
        UnsupportedText.IsVisible = false;
        UpdateMenuState();
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
        UpdateMenuState();
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
