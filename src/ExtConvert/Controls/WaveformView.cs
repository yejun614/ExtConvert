using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ExtConvert.Media;

namespace ExtConvert.Controls;

/// <summary>파형에서 고른 구간. 전체 길이에 대한 비율(0~1)이며 항상 Start &lt; End.</summary>
public readonly record struct WaveformSelection(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>
/// 오디오 파형을 막대로 그린다. 재생된 부분은 <see cref="PlayedBrush"/>, 나머지는 <see cref="RemainingBrush"/>.
/// <list type="bullet">
/// <item>기본: 클릭하거나 끌었다 놓으면 그 위치로 <see cref="SeekRequested"/>.</item>
/// <item><see cref="IsSelectionMode"/>: 끌어서 구간을 고르고, 구간 양 끝을 끌어서 조절한다. 클릭은 탐색.</item>
/// </list>
/// </summary>
public sealed class WaveformView : Control
{
    private const double BarWidth = 2;
    private const double BarGap = 1;
    /// <summary>구간 끝을 잡을 수 있는 거리 (픽셀).</summary>
    private const double EdgeGrab = 6;
    /// <summary>이만큼 움직여야 클릭이 아닌 드래그(구간 선택)로 본다.</summary>
    private const double DragThreshold = 4;

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Progress));

    public static readonly StyledProperty<IBrush?> PlayedBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(PlayedBrush), Brushes.DodgerBlue);

    public static readonly StyledProperty<IBrush?> RemainingBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(RemainingBrush), Brushes.Gray);

    public static readonly StyledProperty<bool> IsSelectionModeProperty =
        AvaloniaProperty.Register<WaveformView, bool>(nameof(IsSelectionMode));

    private enum DragMode { None, Seek, Pending, Selecting, StartEdge, EndEdge }

    private Waveform? _waveform;
    private WaveformSelection? _selection;
    private DragMode _drag;
    private double _pressX;
    /// <summary>탐색 드래그 중인 위치 (0~1). 놓기 전까지는 재생 위치 대신 이 위치까지 색칠한다.</summary>
    private double? _seekPreview;

    static WaveformView()
    {
        AffectsRender<WaveformView>(ProgressProperty, PlayedBrushProperty, RemainingBrushProperty, IsSelectionModeProperty);
    }

    // 커서는 플랫폼이 필요하므로 정적 생성자가 아닌 인스턴스에서 만든다
    public WaveformView() => Cursor = new Cursor(StandardCursorType.Hand);

    /// <summary>재생 위치 (0~1).</summary>
    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public IBrush? PlayedBrush
    {
        get => GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public IBrush? RemainingBrush
    {
        get => GetValue(RemainingBrushProperty);
        set => SetValue(RemainingBrushProperty, value);
    }

    /// <summary>구간 선택 모드. 끄면 구간을 그리지 않고 드래그는 탐색으로 동작한다 (구간 값은 유지).</summary>
    public bool IsSelectionMode
    {
        get => GetValue(IsSelectionModeProperty);
        set => SetValue(IsSelectionModeProperty, value);
    }

    /// <summary>표시할 파형. 분석 중에는 같은 인스턴스가 채워지므로 갱신될 때마다 <see cref="InvalidateVisual"/>을 호출한다.</summary>
    public Waveform? Waveform
    {
        get => _waveform;
        set
        {
            _waveform = value;
            InvalidateVisual();
        }
    }

    /// <summary>선택한 구간. 너무 짧거나 범위를 벗어나면 잘라내고, 길이가 0이면 null.</summary>
    public WaveformSelection? Selection
    {
        get => _selection;
        set
        {
            var normalized = value is { } s && Math.Clamp(s.End, 0, 1) - Math.Clamp(s.Start, 0, 1) > 0
                ? new WaveformSelection(Math.Clamp(s.Start, 0, 1), Math.Clamp(s.End, 0, 1))
                : (WaveformSelection?)null;
            if (normalized == _selection)
                return;
            _selection = normalized;
            InvalidateVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>사용자가 고른 탐색 위치 (0~1).</summary>
    public event EventHandler<double>? SeekRequested;

    /// <summary><see cref="Selection"/>이 바뀔 때 (드래그 중에도 계속).</summary>
    public event EventHandler? SelectionChanged;

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 1 || height < 1)
            return;

        // 마우스 이벤트를 받도록 전체 영역을 투명하게 칠한다
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var played = PlayedBrush ?? Brushes.DodgerBlue;
        var remaining = RemainingBrush ?? Brushes.Gray;

        // 선택 구간: 옅은 배경 + 양 끝 손잡이 선
        if (IsSelectionMode && _selection is { } sel)
        {
            var (x0, x1) = (sel.Start * width, sel.End * width);
            using (context.PushOpacity(0.18))
                context.FillRectangle(played, new Rect(x0, 0, x1 - x0, height));
            context.FillRectangle(played, new Rect(x0 - 1, 0, 2, height));
            context.FillRectangle(played, new Rect(x1 - 1, 0, 2, height));
        }

        var mid = height / 2;
        var progressX = Math.Clamp(_seekPreview ?? Progress, 0, 1) * width;

        var wf = _waveform;
        var count = wf?.Count ?? 0;
        var total = wf?.TotalBuckets ?? 0;
        // 조용한 곡도 잘 보이도록 가장 큰 값 기준으로 정규화 (잡음만 있는 곡을 과하게 키우지 않도록 하한)
        var scale = mid * 0.9 / Math.Max(wf?.Peak ?? 1, 0.02f);

        const double step = BarWidth + BarGap;
        for (var x = 0.0; x + BarWidth <= width; x += step)
        {
            var brush = x + BarWidth / 2 <= progressX ? played : remaining;

            var from = (int)(x / width * total);
            var to = Math.Max(from + 1, (int)Math.Ceiling((x + step) / width * total));
            if (total == 0 || from >= count)
            {
                // 아직 분석하지 않은 구간: 가는 기준선
                context.FillRectangle(brush, new Rect(x, mid - 0.5, BarWidth, 1));
                continue;
            }

            float min = 0, max = 0;
            for (var i = from; i < Math.Min(to, count); i++)
            {
                min = Math.Min(min, wf!.Min(i));
                max = Math.Max(max, wf.Max(i));
            }

            var top = mid - max * scale;
            var bottom = mid - min * scale;
            if (bottom - top < 1) // 무음 구간도 선으로는 보이게
            {
                top = mid - 0.5;
                bottom = mid + 0.5;
            }
            context.FillRectangle(brush, new Rect(x, top, BarWidth, bottom - top));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // 파형이 없어도(오디오 없는 동영상의 타임라인) 탐색·구간 선택은 가능하다
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        // 부모(드롭 영역)의 클릭 = 재생/일시정지로 전달되지 않도록 처리 완료 표시
        e.Handled = true;
        e.Pointer.Capture(this);

        var x = e.GetPosition(this).X;
        _pressX = x;
        if (!IsSelectionMode)
        {
            _drag = DragMode.Seek;
            UpdateSeekPreview(x);
        }
        else if (_selection is { } s && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Shift+클릭: 가까운 쪽 끝을 그 위치로 늘리거나 줄인다
            _drag = Fraction(x) < (s.Start + s.End) / 2 ? DragMode.StartEdge : DragMode.EndEdge;
            MoveEdge(x);
        }
        else
        {
            _drag = HitEdge(x) is var edge and not DragMode.None ? edge : DragMode.Pending;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X;
        switch (_drag)
        {
            case DragMode.None:
                // 구간 끝 위에서는 좌우 크기 조절 커서
                Cursor = new Cursor(IsSelectionMode && HitEdge(x) != DragMode.None
                    ? StandardCursorType.SizeWestEast
                    : StandardCursorType.Hand);
                break;
            case DragMode.Seek:
                UpdateSeekPreview(x);
                break;
            case DragMode.Pending when Math.Abs(x - _pressX) >= DragThreshold:
                _drag = DragMode.Selecting;
                goto case DragMode.Selecting;
            case DragMode.Selecting:
                var (a, b) = (Fraction(_pressX), Fraction(x));
                Selection = new WaveformSelection(Math.Min(a, b), Math.Max(a, b));
                break;
            case DragMode.StartEdge or DragMode.EndEdge:
                MoveEdge(x);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var drag = _drag;
        var seekTo = _seekPreview;
        if (drag == DragMode.None)
            return;
        e.Handled = true;
        _drag = DragMode.None;
        _seekPreview = null;
        // 캡처를 풀면 OnPointerCaptureLost가 바로 호출되므로 상태는 그 전에 읽어 둔다
        e.Pointer.Capture(null);

        switch (drag)
        {
            case DragMode.Seek when seekTo is { } fraction:
                SeekRequested?.Invoke(this, fraction);
                InvalidateVisual();
                break;
            case DragMode.Pending:
                // 선택 모드에서 끌지 않고 클릭 = 그 위치로 탐색 (구간은 유지)
                SeekRequested?.Invoke(this, Fraction(_pressX));
                break;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = DragMode.None;
        if (_seekPreview is null)
            return;
        _seekPreview = null;
        InvalidateVisual();
    }

    private double Fraction(double x) => Bounds.Width > 0 ? Math.Clamp(x / Bounds.Width, 0, 1) : 0;

    private DragMode HitEdge(double x)
    {
        if (_selection is not { } s)
            return DragMode.None;
        var (x0, x1) = (s.Start * Bounds.Width, s.End * Bounds.Width);
        var (d0, d1) = (Math.Abs(x - x0), Math.Abs(x - x1));
        if (Math.Min(d0, d1) > EdgeGrab)
            return DragMode.None;
        return d0 <= d1 ? DragMode.StartEdge : DragMode.EndEdge;
    }

    /// <summary>잡고 있는 구간 끝을 옮긴다. 반대쪽 끝을 넘어가면 잡은 끝이 바뀐다.</summary>
    private void MoveEdge(double x)
    {
        if (_selection is not { } s)
            return;
        var f = Fraction(x);
        WaveformSelection next;
        if (_drag == DragMode.StartEdge)
        {
            next = f <= s.End ? new(f, s.End) : new(s.End, f);
            if (f > s.End)
                _drag = DragMode.EndEdge;
        }
        else
        {
            next = f >= s.Start ? new(s.Start, f) : new(f, s.Start);
            if (f < s.Start)
                _drag = DragMode.StartEdge;
        }

        // 두 끝이 겹치면 구간이 사라져(null) 더 끌 수 없으므로 그 직전에서 멈춘다
        if (next.Length > 1e-4)
            Selection = next;
    }

    private void UpdateSeekPreview(double x)
    {
        _seekPreview = Fraction(x);
        InvalidateVisual();
    }
}
