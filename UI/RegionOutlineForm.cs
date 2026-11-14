using LiteRec.Interop;

namespace LiteRec.UI;

/// <summary>
/// A frame drawn just outside the area that is (or is about to be) recorded: the selected window's
/// content or the custom region. The window is shaped like a hollow rectangle, so it never covers
/// the recorded pixels and does not take focus or intercept clicks inside the area.
///
/// In editable mode (custom region, before recording) the frame is thicker and can be dragged:
/// the edges and corners resize the region, the white grip in the middle of each edge moves it.
/// </summary>
internal sealed class RegionOutlineForm : Form
{
    private const int ThinFrame = 3;
    private const int EditFrame = 7;
    private const int GripLength = 56;          // length of the "move" grip on each edge
    private const int CornerSize = 22;          // length of the corner resize zones along the edges
    private const int MinimumArea = 64;

    private enum Hit { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    private Rectangle _area;      // the recorded area in screen pixels (the frame lies outside it)
    private bool _editable;
    private int _frame = ThinFrame;
    private Hit _dragHit;
    private Point _dragStartCursor;
    private Rectangle _dragStartArea;

    /// <summary>Raised continuously while the user drags the frame, with the new area in screen pixels.</summary>
    public event Action<Rectangle>? AreaChanged;

    /// <summary>Raised once when a drag ends.</summary>
    public event Action? DragCompleted;

    public RegionOutlineForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Red;
        DoubleBuffered = true;
    }

    /// <summary>Moves the frame around <paramref name="area"/> (screen pixels). Cheap when nothing changed.</summary>
    public void SetTarget(Rectangle area, bool editable)
    {
        if (_dragHit != Hit.None)
        {
            return; // the user is dragging: the frame is already where they put it
        }

        int frame = editable ? EditFrame : ThinFrame;
        if (area == _area && editable == _editable)
        {
            return;
        }

        bool shapeChanged = area.Size != _area.Size || frame != _frame;
        _area = area;
        _editable = editable;
        _frame = frame;
        Bounds = Rectangle.Inflate(area, frame, frame);
        Cursor = Cursors.Default;
        if (shapeChanged)
        {
            ApplyShape();
        }

        Invalidate();
    }

    /// <summary>Cuts the middle out: only a frame-wide ring remains part of the window.</summary>
    private void ApplyShape()
    {
        var shape = new Region(new Rectangle(0, 0, _area.Width + 2 * _frame, _area.Height + 2 * _frame));
        shape.Exclude(new Rectangle(_frame, _frame, _area.Width, _area.Height));
        Region = shape;
    }

    /// <summary>Red normally, amber while the recording is paused.</summary>
    public void SetPaused(bool paused) => BackColor = paused ? Color.Orange : Color.Red;

    // ---------------------------------------------------------------------------------------------
    // Hit testing and dragging
    // ---------------------------------------------------------------------------------------------

    /// <summary>Works out which part of the frame a point (client coordinates) is on.</summary>
    private Hit HitTest(Point p)
    {
        if (!_editable)
        {
            return Hit.None;
        }

        int w = Width, h = Height;
        bool left = p.X < _frame, right = p.X >= w - _frame, top = p.Y < _frame, bottom = p.Y >= h - _frame;

        // Corners: the ends of the horizontal edges and of the vertical edges.
        bool nearLeft = p.X < CornerSize, nearRight = p.X >= w - CornerSize, nearTop = p.Y < CornerSize, nearBottom = p.Y >= h - CornerSize;
        if ((top && nearLeft) || (left && nearTop)) return Hit.TopLeft;
        if ((top && nearRight) || (right && nearTop)) return Hit.TopRight;
        if ((bottom && nearLeft) || (left && nearBottom)) return Hit.BottomLeft;
        if ((bottom && nearRight) || (right && nearBottom)) return Hit.BottomRight;

        // The grip in the middle of each edge moves the whole region.
        bool midX = Math.Abs(p.X - w / 2) <= GripLength / 2, midY = Math.Abs(p.Y - h / 2) <= GripLength / 2;
        if (((top || bottom) && midX) || ((left || right) && midY)) return Hit.Move;

        if (left) return Hit.Left;
        if (right) return Hit.Right;
        if (top) return Hit.Top;
        if (bottom) return Hit.Bottom;
        return Hit.None;
    }

    private static Cursor CursorFor(Hit hit) => hit switch
    {
        Hit.Move => Cursors.SizeAll,
        Hit.Left or Hit.Right => Cursors.SizeWE,
        Hit.Top or Hit.Bottom => Cursors.SizeNS,
        Hit.TopLeft or Hit.BottomRight => Cursors.SizeNWSE,
        Hit.TopRight or Hit.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Default,
    };

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Hit hit = e.Button == MouseButtons.Left ? HitTest(e.Location) : Hit.None;
        if (hit == Hit.None)
        {
            return;
        }

        _dragHit = hit;
        _dragStartCursor = Cursor.Position; // screen coordinates keep the drag exact across monitors and DPI
        _dragStartArea = _area;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragHit == Hit.None)
        {
            Cursor = CursorFor(HitTest(e.Location));
            return;
        }

        Point now = Cursor.Position;
        int dx = now.X - _dragStartCursor.X, dy = now.Y - _dragStartCursor.Y;
        Rectangle start = _dragStartArea;
        int left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;

        switch (_dragHit)
        {
            case Hit.Move:
                left += dx; right += dx; top += dy; bottom += dy;
                break;
            case Hit.Left: left += dx; break;
            case Hit.Right: right += dx; break;
            case Hit.Top: top += dy; break;
            case Hit.Bottom: bottom += dy; break;
            case Hit.TopLeft: left += dx; top += dy; break;
            case Hit.TopRight: right += dx; top += dy; break;
            case Hit.BottomLeft: left += dx; bottom += dy; break;
            case Hit.BottomRight: right += dx; bottom += dy; break;
        }

        // Keep a usable minimum size (the opposite edge stays put) and stay on the desktop.
        if (_dragHit != Hit.Move)
        {
            if (right - left < MinimumArea) { if (_dragHit is Hit.Left or Hit.TopLeft or Hit.BottomLeft) left = right - MinimumArea; else right = left + MinimumArea; }
            if (bottom - top < MinimumArea) { if (_dragHit is Hit.Top or Hit.TopLeft or Hit.TopRight) top = bottom - MinimumArea; else bottom = top + MinimumArea; }
        }

        Rectangle desktop = SystemInformation.VirtualScreen;
        Rectangle area = Rectangle.FromLTRB(left, top, right, bottom);
        if (_dragHit == Hit.Move)
        {
            area.X = Math.Clamp(area.X, desktop.Left, desktop.Right - area.Width);
            area.Y = Math.Clamp(area.Y, desktop.Top, desktop.Bottom - area.Height);
        }
        else
        {
            area = Rectangle.Intersect(area, desktop);
            if (area.Width < MinimumArea || area.Height < MinimumArea)
            {
                return;
            }
        }

        if (area == _area)
        {
            return;
        }

        Hit active = _dragHit;
        _dragHit = Hit.None; // let SetTarget apply the change
        SetTarget(area, editable: true);
        _dragHit = active;
        AreaChanged?.Invoke(area);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragHit != Hit.None)
        {
            _dragHit = Hit.None;
            Capture = false;
            DragCompleted?.Invoke();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_editable)
        {
            return;
        }

        // White grips on the four edges show where the region can be dragged.
        using var grip = new SolidBrush(Color.White);
        int w = Width, h = Height;
        e.Graphics.FillRectangle(grip, (w - GripLength) / 2, 1, GripLength, _frame - 2);
        e.Graphics.FillRectangle(grip, (w - GripLength) / 2, h - _frame + 1, GripLength, _frame - 2);
        e.Graphics.FillRectangle(grip, 1, (h - GripLength) / 2, _frame - 2, GripLength);
        e.Graphics.FillRectangle(grip, w - _frame + 1, (h - GripLength) / 2, _frame - 2, GripLength);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= (int)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
            return cp;
        }
    }
}
