using LiteRec.Interop;

namespace LiteRec.UI;

/// <summary>
/// A thin frame drawn just outside the recorded region so the user can see what is being captured.
/// The window is shaped like a hollow rectangle, so it never covers (or appears in) the recording
/// and does not take focus or intercept clicks inside the region.
/// </summary>
internal sealed class RegionOutlineForm : Form
{
    private const int Thickness = 3;

    public RegionOutlineForm(Rectangle region)
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Red;
        Bounds = Rectangle.Inflate(region, Thickness, Thickness);

        // Cut the middle out: only a Thickness-wide frame remains part of the window.
        var shape = new Region(new Rectangle(0, 0, Width, Height));
        shape.Exclude(new Rectangle(Thickness, Thickness, region.Width, region.Height));
        Region = shape;
    }

    /// <summary>Red while recording, amber while paused.</summary>
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
