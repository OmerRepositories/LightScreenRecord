using LiteRec.Interop;

namespace LiteRec.UI;

/// <summary>
/// A thin frame drawn just outside the area that is (or is about to be) recorded: the selected
/// window's content or the custom region. The window is shaped like a hollow rectangle, so it never
/// covers the recorded pixels and does not take focus or intercept clicks inside the area.
/// </summary>
internal sealed class RegionOutlineForm : Form
{
    private const int Thickness = 3;
    private Rectangle _target;

    public RegionOutlineForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Red;
    }

    /// <summary>Moves the frame around <paramref name="area"/> (screen pixels). Cheap when nothing changed.</summary>
    public void SetTarget(Rectangle area)
    {
        if (area == _target)
        {
            return;
        }

        bool sizeChanged = area.Size != _target.Size;
        _target = area;
        Bounds = Rectangle.Inflate(area, Thickness, Thickness);

        if (sizeChanged)
        {
            // Cut the middle out: only a Thickness-wide frame remains part of the window.
            var shape = new Region(new Rectangle(0, 0, area.Width + 2 * Thickness, area.Height + 2 * Thickness));
            shape.Exclude(new Rectangle(Thickness, Thickness, area.Width, area.Height));
            Region = shape;
        }
    }

    /// <summary>Red normally, amber while the recording is paused.</summary>
    public void SetPaused(bool paused) => BackColor = paused ? Color.Orange : Color.Red;

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
