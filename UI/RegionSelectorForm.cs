namespace LiteRec.UI;

/// <summary>
/// Translucent overlay covering every monitor. The user drags a rectangle with the mouse; the
/// selected area is shown clear while the rest of the desktop stays dimmed. Esc cancels.
/// </summary>
internal sealed class RegionSelectorForm : Form
{
    private const int MinimumSelection = 64; // smaller areas are not useful and some hardware encoders reject them

    private static readonly Color HoleColor = Color.Fuchsia; // made fully transparent via TransparencyKey

    private readonly string _hint;
    private readonly Font _hintFont = new("Segoe UI", 14f, FontStyle.Bold);
    private readonly Font _sizeFont = new("Segoe UI", 10f, FontStyle.Bold);
    private bool _dragging;
    private Point _dragStart;   // screen coordinates
    private Point _dragCurrent; // screen coordinates

    /// <summary>The chosen rectangle in physical screen pixels, or null if the user cancelled.</summary>
    public Rectangle? Selection { get; private set; }

    public RegionSelectorForm(string hint)
    {
        _hint = hint;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Opacity = 0.5;
        TransparencyKey = HoleColor;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
        RightToLeft = Loc.IsRightToLeft ? RightToLeft.Yes : RightToLeft.No;
    }

    private Rectangle CurrentSelection => Rectangle.FromLTRB(
        Math.Min(_dragStart.X, _dragCurrent.X), Math.Min(_dragStart.Y, _dragCurrent.Y),
        Math.Max(_dragStart.X, _dragCurrent.X), Math.Max(_dragStart.Y, _dragCurrent.Y));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            // Screen coordinates are used throughout so mixed-DPI multi-monitor setups stay exact.
            _dragging = true;
            _dragStart = _dragCurrent = Cursor.Position;
            Capture = true;
            Invalidate();
        }
        else if (e.Button == MouseButtons.Right)
        {
            Cancel();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            _dragCurrent = Cursor.Position;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragging = false;
        Capture = false;
        _dragCurrent = Cursor.Position;
        Rectangle selection = CurrentSelection;

        if (selection.Width >= MinimumSelection && selection.Height >= MinimumSelection)
        {
            Selection = selection;
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            Invalidate(); // too small (probably an accidental click): let the user try again
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            Cancel();
        }
    }

    private void Cancel()
    {
        Selection = null;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;

        if (!_dragging)
        {
            // Instruction text, centered on the primary screen.
            Rectangle primary = RectangleToClient(Screen.PrimaryScreen?.Bounds ?? Bounds);
            TextRenderer.DrawText(g, _hint, _hintFont, primary, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }

        Rectangle selection = RectangleToClient(CurrentSelection);
        using (var hole = new SolidBrush(HoleColor))
        {
            g.FillRectangle(hole, selection);
        }

        using (var border = new Pen(Color.White, 2f))
        {
            g.DrawRectangle(border, selection);
        }

        // Live size read-out just above (or inside, near the top edge of the screen) the selection.
        string size = $"{CurrentSelection.Width} x {CurrentSelection.Height}";
        Size textSize = TextRenderer.MeasureText(g, size, _sizeFont);
        int textY = selection.Top - textSize.Height - 4;
        if (textY < 0)
        {
            textY = selection.Top + 6;
        }

        TextRenderer.DrawText(g, size, _sizeFont, new Point(selection.Left + 2, textY), Color.White, Color.Black, TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hintFont.Dispose();
            _sizeFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
