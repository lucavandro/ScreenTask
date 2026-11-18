using System.Drawing;
using System.Windows.Forms;

namespace ScreenTask.UI;

/// <summary>Keeps native tab navigation while filling .NET 9's unthemed tab chrome.</summary>
internal sealed class ThemedTabControl : TabControl
{
    public ThemedTabControl()
    {
        if (DesktopTheme.IsDark) DrawMode = TabDrawMode.OwnerDrawFixed;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (DesktopTheme.IsDark) DrawTab(e.Graphics, e.Index);
        else base.OnDrawItem(e);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (!DesktopTheme.IsDark || IsDisposed || !IsHandleCreated) return;
        const int WmPaint = 0x000F, WmPrint = 0x0317, WmPrintClient = 0x0318;
        if (m.Msg == WmPaint)
        {
            using var graphics = Graphics.FromHwnd(Handle);
            DrawChrome(graphics);
        }
        else if ((m.Msg == WmPrint || m.Msg == WmPrintClient) && m.WParam != IntPtr.Zero)
        {
            using var graphics = Graphics.FromHdc(m.WParam);
            DrawChrome(graphics);
        }
    }

    private void DrawChrome(Graphics graphics)
    {
        Rectangle page = DisplayRectangle;
        using var background = new SolidBrush(SystemColors.Control);
        graphics.FillRectangle(background, 0, 0, Width, page.Top);
        graphics.FillRectangle(background, 0, page.Top, page.Left, Height - page.Top);
        graphics.FillRectangle(background, page.Right, page.Top, Width - page.Right, Height - page.Top);
        graphics.FillRectangle(background, 0, page.Bottom, Width, Height - page.Bottom);
        for (int i = 0; i < TabCount; i++) DrawTab(graphics, i);
    }

    private void DrawTab(Graphics graphics, int index)
    {
        if (index < 0 || index >= TabCount) return;
        Rectangle bounds = GetTabRect(index);
        bool selected = index == SelectedIndex;
        using var background = new SolidBrush(selected ? SystemColors.Window : SystemColors.Control);
        graphics.FillRectangle(background, bounds);
        if (selected)
        {
            using var accent = new SolidBrush(DesktopTheme.Link);
            graphics.FillRectangle(accent, bounds.Left + 3, bounds.Bottom - 3, bounds.Width - 6, 2);
        }
        TextRenderer.DrawText(graphics, TabPages[index].Text, Font, bounds,
            SystemColors.ControlText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (selected && Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(bounds, -4, -4));
    }
}
