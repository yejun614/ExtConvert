using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ExtConvert.Controls;

/// <summary>
/// 이미지 미리보기 위에 겹쳐서 잘라낼 영역(이미지 픽셀 좌표)을 고르는 레이어.
/// 확대/축소된 미리보기 안이 아니라 바깥에 두므로 손잡이·선 굵기가 배율과 관계없이 일정하다.
/// <list type="bullet">
/// <item>영역 밖에서 끌기: 새 영역</item>
/// <item>영역 안에서 끌기: 이동</item>
/// <item>모서리/변 손잡이 끌기: 크기 조절 (비율 고정 시 모서리만)</item>
/// </list>
/// </summary>
public sealed class CropOverlay : Control
{
    private const double HandleSize = 8;
    private const double HandleGrab = 8;
    private const double DragThreshold = 3;

    private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
    private static readonly IPen OuterPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1);
    private static readonly IPen InnerPen = new Pen(Brushes.White, 1);
    private static readonly IPen GuidePen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);

    /// <summary>
    /// 손잡이 종류. X/Y는 그 축에서 움직이는 쪽 (-1 = 왼쪽/위, +1 = 오른쪽/아래, 0 = 움직이지 않음).
    /// </summary>
    private readonly record struct Handle(int X, int Y)
    {
        public bool IsCorner => X != 0 && Y != 0;
    }

    private enum DragMode { None, Pending, Create, Move, Resize }

    private static readonly Handle[] Handles =
        [new(-1, -1), new(0, -1), new(1, -1), new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0)];

    private Visual? _target;
    private PixelSize _imageSize;
    private PixelRect? _selection;
    private double? _aspectRatio;

    private DragMode _drag;
    private Handle _handle;
    private Point _pressPixel;
    private PixelRect _pressSelection;

    public CropOverlay()
    {
        // 커서는 플랫폼이 필요하므로 정적 생성자가 아닌 인스턴스에서 만든다
        // (정적 생성자에서 실패하면 FitRatio 같은 정적 멤버까지 쓸 수 없게 됨)
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    /// <summary>이미지가 실제로 그려지는 컨트롤. 이 컨트롤의 화면상 위치/크기에 맞춰 그린다.</summary>
    public Visual? Target
    {
        get => _target;
        set
        {
            if (_target is Layoutable old)
                old.LayoutUpdated -= OnTargetLayoutUpdated;
            _target = value;
            if (_target is Layoutable now)
                now.LayoutUpdated += OnTargetLayoutUpdated;
            InvalidateVisual();
        }
    }

    /// <summary>원본 이미지 크기 (픽셀).</summary>
    public PixelSize ImageSize
    {
        get => _imageSize;
        set
        {
            _imageSize = value;
            Selection = _selection; // 새 크기에 맞게 다시 자른다
            InvalidateVisual();
        }
    }

    /// <summary>고른 영역 (이미지 픽셀). 이미지 밖은 잘라내고, 크기가 0이면 null.</summary>
    public PixelRect? Selection
    {
        get => _selection;
        set
        {
            var next = value is { } r ? Clamp(r) : null;
            if (next == _selection)
                return;
            _selection = next;
            InvalidateVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 고정할 가로/세로 비율. 설정하면 지금 영역 안에서 그 비율로 가장 큰 영역(가운데 정렬)으로 바꾼다. null이면 자유.
    /// </summary>
    public double? AspectRatio
    {
        get => _aspectRatio;
        set
        {
            _aspectRatio = value is > 0 ? value : null;
            if (_aspectRatio is { } ratio)
            {
                var within = _selection ?? new PixelRect(_imageSize);
                Selection = FitRatio(within, ratio);
            }
            InvalidateVisual();
        }
    }

    /// <summary><see cref="Selection"/>이 바뀔 때 (끄는 중에도 계속).</summary>
    public event EventHandler? SelectionChanged;

    private void OnTargetLayoutUpdated(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>이 컨트롤 좌표계에서 이미지가 그려진 사각형.</summary>
    private Rect? ImageRect()
    {
        if (_target is null || _imageSize.Width <= 0 || !_target.IsEffectivelyVisible
            || _target.TransformToVisual(this) is not { } m)
            return null;
        var r = new Rect(_target.Bounds.Size).TransformToAABB(m);
        return r.Width > 0 && r.Height > 0 ? r : null;
    }

    private Rect ToScreen(PixelRect p, Rect image)
    {
        var sx = image.Width / _imageSize.Width;
        var sy = image.Height / _imageSize.Height;
        return new Rect(image.X + p.X * sx, image.Y + p.Y * sy, p.Width * sx, p.Height * sy);
    }

    /// <summary>화면 좌표 → 이미지 픽셀 좌표 (이미지 범위로 제한, 소수).</summary>
    private Point ToPixel(Point screen, Rect image) => new(
        Math.Clamp((screen.X - image.X) / image.Width * _imageSize.Width, 0, _imageSize.Width),
        Math.Clamp((screen.Y - image.Y) / image.Height * _imageSize.Height, 0, _imageSize.Height));

    public override void Render(DrawingContext context)
    {
        // 영역 밖을 눌러도 이벤트를 받도록 전체를 투명하게 칠한다
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (ImageRect() is not { } image || _selection is not { } sel)
            return;

        var r = ToScreen(sel, image);

        // 영역 밖 어둡게
        context.FillRectangle(DimBrush, new Rect(image.X, image.Y, image.Width, r.Y - image.Y));
        context.FillRectangle(DimBrush, new Rect(image.X, r.Bottom, image.Width, image.Bottom - r.Bottom));
        context.FillRectangle(DimBrush, new Rect(image.X, r.Y, r.X - image.X, r.Height));
        context.FillRectangle(DimBrush, new Rect(r.Right, r.Y, image.Right - r.Right, r.Height));

        // 끄는 중에는 3분할 안내선
        if (_drag is DragMode.Create or DragMode.Move or DragMode.Resize)
            for (var i = 1; i <= 2; i++)
            {
                var x = r.X + r.Width * i / 3;
                var y = r.Y + r.Height * i / 3;
                context.DrawLine(GuidePen, new Point(x, r.Y), new Point(x, r.Bottom));
                context.DrawLine(GuidePen, new Point(r.X, y), new Point(r.Right, y));
            }

        // 어떤 배경에서도 보이도록 검은 테두리 + 흰 테두리
        context.DrawRectangle(null, OuterPen, r.Inflate(1));
        context.DrawRectangle(null, InnerPen, r);

        foreach (var h in VisibleHandles())
        {
            var c = HandleCenter(h, r);
            var box = new Rect(c.X - HandleSize / 2, c.Y - HandleSize / 2, HandleSize, HandleSize);
            context.DrawRectangle(Brushes.White, OuterPen, box);
        }

        // 크기 표시
        var label = new FormattedText($"{sel.Width} × {sel.Height}", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White);
        var lx = Math.Clamp(r.X + (r.Width - label.Width) / 2 - 4, 0, Math.Max(0, Bounds.Width - label.Width - 8));
        // 아래쪽 손잡이와 겹치지 않게 띄우고, 공간이 없으면 위쪽에 표시
        var ly = r.Bottom + 10 + label.Height + 4 <= Bounds.Height ? r.Bottom + 10 : r.Y - label.Height - 14;
        var labelBox = new Rect(lx, Math.Max(0, ly), label.Width + 8, label.Height + 4);
        context.DrawRectangle(LabelBackground, null, labelBox, 3, 3);
        context.DrawText(label, new Point(labelBox.X + 4, labelBox.Y + 2));
    }

    private IEnumerable<Handle> VisibleHandles() => _aspectRatio is null ? Handles : Handles.Where(h => h.IsCorner);

    private static Point HandleCenter(Handle h, Rect r) => new(
        h.X switch { < 0 => r.X, > 0 => r.Right, _ => r.X + r.Width / 2 },
        h.Y switch { < 0 => r.Y, > 0 => r.Bottom, _ => r.Y + r.Height / 2 });

    private Handle? HitHandle(Point p, Rect image)
    {
        if (_selection is not { } sel)
            return null;
        var r = ToScreen(sel, image);
        foreach (var h in VisibleHandles())
        {
            var c = HandleCenter(h, r);
            if (Math.Abs(p.X - c.X) <= HandleGrab && Math.Abs(p.Y - c.Y) <= HandleGrab)
                return h;
        }
        return null;
    }

    private static StandardCursorType CursorFor(Handle h) => (h.X, h.Y) switch
    {
        (-1, -1) or (1, 1) => StandardCursorType.TopLeftCorner,
        (1, -1) or (-1, 1) => StandardCursorType.TopRightCorner,
        (0, _) => StandardCursorType.SizeNorthSouth,
        _ => StandardCursorType.SizeWestEast,
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || ImageRect() is not { } image)
            return;
        e.Handled = true; // 드롭 영역의 클릭 동작(파일 열기 등)으로 넘어가지 않도록
        e.Pointer.Capture(this);

        var p = e.GetPosition(this);
        _pressPixel = ToPixel(p, image);
        _pressSelection = _selection ?? new PixelRect(_imageSize);

        if (HitHandle(p, image) is { } h)
        {
            _drag = DragMode.Resize;
            _handle = h;
        }
        else if (_selection is { } sel && ToScreen(sel, image).Contains(p))
            _drag = DragMode.Move;
        else
            _drag = DragMode.Pending; // 조금 끌어야 새 영역 (실수로 클릭해도 영역이 사라지지 않게)
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (ImageRect() is not { } image)
            return;
        var p = e.GetPosition(this);
        var px = ToPixel(p, image);

        switch (_drag)
        {
            case DragMode.None:
                Cursor = new Cursor(HitHandle(p, image) is { } h ? CursorFor(h)
                    : _selection is { } sel && ToScreen(sel, image).Contains(p) ? StandardCursorType.SizeAll
                    : StandardCursorType.Cross);
                break;
            case DragMode.Pending:
                var scale = image.Width / _imageSize.Width;
                if (Math.Abs(px.X - _pressPixel.X) * scale < DragThreshold
                    && Math.Abs(px.Y - _pressPixel.Y) * scale < DragThreshold)
                    break;
                _drag = DragMode.Create;
                goto case DragMode.Create;
            case DragMode.Create:
                Selection = FromAnchor(_pressPixel, px, cornerOnly: true);
                break;
            case DragMode.Move:
                var dx = (int)Math.Round(px.X - _pressPixel.X);
                var dy = (int)Math.Round(px.Y - _pressPixel.Y);
                var s = _pressSelection;
                Selection = new PixelRect(
                    Math.Clamp(s.X + dx, 0, _imageSize.Width - s.Width),
                    Math.Clamp(s.Y + dy, 0, _imageSize.Height - s.Height),
                    s.Width, s.Height);
                break;
            case DragMode.Resize:
                Selection = Resize(_pressSelection, _handle, px);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragMode.None)
            return;
        e.Handled = true;
        _drag = DragMode.None;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = DragMode.None;
        InvalidateVisual();
    }

    /// <summary>손잡이를 끈 위치로 영역 크기를 바꾼다. 반대쪽 모서리/변이 고정점.</summary>
    private PixelRect Resize(PixelRect start, Handle h, Point px)
    {
        var anchor = new Point(h.X < 0 ? start.Right : start.X, h.Y < 0 ? start.Bottom : start.Y);
        if (h.IsCorner)
            return FromAnchor(anchor, px, cornerOnly: true);

        // 변 손잡이 (비율 자유일 때만): 한 축만 움직인다
        var x0 = h.X == 0 ? start.X : anchor.X;
        var x1 = h.X == 0 ? start.Right : px.X;
        var y0 = h.Y == 0 ? start.Y : anchor.Y;
        var y1 = h.Y == 0 ? start.Bottom : px.Y;
        return Round(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }

    /// <summary>고정점에서 포인터까지의 사각형. 비율이 고정되어 있으면 그 비율을 유지하고 이미지 안으로 줄인다.</summary>
    private PixelRect FromAnchor(Point anchor, Point px, bool cornerOnly)
    {
        var dirX = px.X >= anchor.X ? 1 : -1;
        var dirY = px.Y >= anchor.Y ? 1 : -1;
        var w = Math.Abs(px.X - anchor.X);
        var h = Math.Abs(px.Y - anchor.Y);

        if (_aspectRatio is { } ratio && cornerOnly)
        {
            // 더 많이 끈 쪽 기준으로 비율을 맞추고, 이미지를 벗어나면 비율을 유지한 채 줄인다
            if (h == 0 || w / h > ratio) h = w / ratio; else w = h * ratio;
            var maxW = dirX > 0 ? _imageSize.Width - anchor.X : anchor.X;
            var maxH = dirY > 0 ? _imageSize.Height - anchor.Y : anchor.Y;
            if (w > maxW) { w = maxW; h = w / ratio; }
            if (h > maxH) { h = maxH; w = h * ratio; }
        }

        var x1 = anchor.X + dirX * w;
        var y1 = anchor.Y + dirY * h;
        return Round(Math.Min(anchor.X, x1), Math.Min(anchor.Y, y1), Math.Max(anchor.X, x1), Math.Max(anchor.Y, y1));
    }

    private static PixelRect Round(double left, double top, double right, double bottom)
    {
        var l = (int)Math.Round(left);
        var t = (int)Math.Round(top);
        return new PixelRect(l, t, Math.Max(1, (int)Math.Round(right) - l), Math.Max(1, (int)Math.Round(bottom) - t));
    }

    /// <summary><paramref name="within"/> 안에 들어가는 가장 큰 <paramref name="ratio"/> 비율 사각형 (가운데 정렬).</summary>
    internal static PixelRect FitRatio(PixelRect within, double ratio)
    {
        var w = (double)within.Width;
        var h = w / ratio;
        if (h > within.Height)
        {
            h = within.Height;
            w = h * ratio;
        }
        var iw = Math.Max(1, (int)Math.Round(w));
        var ih = Math.Max(1, (int)Math.Round(h));
        return new PixelRect(within.X + (within.Width - iw) / 2, within.Y + (within.Height - ih) / 2, iw, ih);
    }

    private PixelRect? Clamp(PixelRect r)
    {
        if (_imageSize.Width <= 0 || _imageSize.Height <= 0)
            return null;
        var x = Math.Clamp(r.X, 0, _imageSize.Width - 1);
        var y = Math.Clamp(r.Y, 0, _imageSize.Height - 1);
        var w = Math.Min(r.Width, _imageSize.Width - x);
        var h = Math.Min(r.Height, _imageSize.Height - y);
        return w > 0 && h > 0 ? new PixelRect(x, y, w, h) : null;
    }
}
