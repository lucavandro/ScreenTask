using System.Drawing;
using System.Windows.Forms;

namespace ScreenTask.UI;

/// <summary>Uses the Windows app theme at startup without changing Windows preferences.</summary>
internal static class DesktopTheme
{
    internal static void Initialize()
    {
        // .NET 9's native WinForms dark palette is experimental. Keep its use isolated
        // here; Windows 10 and High Contrast retain their supported system palette.
        // SetColorMode must run before any windows are created. A Windows theme
        // change takes effect on the next app launch (the .NET 9 API limitation).
#pragma warning disable WFO5001
        Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001
    }

    internal static bool IsDark => !SystemInformation.HighContrast && SystemColors.Control.GetBrightness() < 0.5f;
    internal static Color Link => SystemInformation.HighContrast ? SystemColors.HotTrack
        : IsDark ? Color.FromArgb(139, 193, 255) : Color.FromArgb(20, 90, 170);
    internal static Color Success => SystemInformation.HighContrast ? SystemColors.ControlText
        : IsDark ? Color.FromArgb(135, 218, 157) : Color.FromArgb(20, 110, 50);
    internal static Color Warning => SystemInformation.HighContrast ? SystemColors.ControlText
        : IsDark ? Color.FromArgb(255, 186, 138) : Color.FromArgb(180, 60, 20);

    internal static void ApplyLinks(ToolStrip strip)
    {
        foreach (ToolStripItem item in strip.Items)
        {
            if (item is ToolStripStatusLabel { IsLink: true } link)
            {
                link.LinkColor = Link;
                link.ActiveLinkColor = Link;
                link.VisitedLinkColor = Link;
            }
        }
    }

    internal static void Apply(Control root)
    {
        // .NET 9 does not color read-only edit backgrounds or the status-strip
        // gradient consistently. Explicit colors keep their text readable.
        if (IsDark)
        {
            if (root is TabPage page)
            {
                page.UseVisualStyleBackColor = false;
                page.BackColor = SystemColors.Control;
            }
            if (root is TextBoxBase { ReadOnly: true })
            {
                root.BackColor = SystemColors.Window;
                root.ForeColor = SystemColors.WindowText;
            }
            if (root is StatusStrip status)
            {
                status.BackColor = SystemColors.Control;
                status.ForeColor = SystemColors.ControlText;
                status.Renderer = new ToolStripProfessionalRenderer(new StatusColors());
            }
        }
        foreach (Control child in root.Controls) Apply(child);
    }

    private sealed class StatusColors : ProfessionalColorTable
    {
        public override Color StatusStripGradientBegin => SystemColors.Control;
        public override Color StatusStripGradientEnd => SystemColors.Control;
    }
}
