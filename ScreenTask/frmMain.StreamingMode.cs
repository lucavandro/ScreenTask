using System.Drawing;
using System.Windows.Forms;

namespace ScreenTask;

public partial class frmMain
{
    // InitializeStreamingMode assigns this in the constructor before settings or event handlers use it.
    internal ComboBox comboStreamingMode = null!;

    private void InitializeStreamingMode()
    {
        tableQualityStartup.SuspendLayout();
        tableQualityStartup.RowCount = 6;
        float transportHeight = comboFps.PreferredHeight + comboFps.Margin.Vertical + 8;
        tableQualityStartup.RowStyles.Insert(2, new RowStyle(SizeType.Absolute, transportHeight));
        tableSettings.RowStyles[0].Height += transportHeight;
        tableQualityStartup.SetRow(cbAutoStart, 3);
        tableQualityStartup.SetRow(cbMiniStart, 4);
        tableQualityStartup.SetRow(cbCloseToTray, 5);
        var label = new Label
        {
            Text = "Video transport:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = lblFpsDesc.Margin,
            Name = "lblStreamingMode"
        };
        comboStreamingMode = new ComboBox
        {
            Name = "comboStreamingMode",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Font = comboFps.Font,
            Margin = comboFps.Margin
        };
        comboStreamingMode.Items.AddRange(["JPEG / WebSocket (compatible)", "WebRTC / H.264 (lower bandwidth)"]);
        comboStreamingMode.SelectedIndex = 0;
        comboStreamingMode.SelectedIndexChanged += (s, e) =>
        {
            _webServer.PreferWebRtc = comboStreamingMode.SelectedIndex == 1;
            qualitySlider.Enabled = !_webServer.PreferWebRtc;
            lblQuality.Enabled = qualitySlider.Enabled;
            SaveSettings();
            _ = RefreshFirewallStatusAsync();
        };
        tableQualityStartup.Controls.Add(label, 0, 2);
        tableQualityStartup.Controls.Add(comboStreamingMode, 1, 2);
        toolTipMain.SetToolTip(comboStreamingMode,
            "WebRTC uses H.264 video (8 Mbps target) over UDP on the selected port. Audio stays on the current channel. Viewers fall back to JPEG when unavailable. Stop broadcasting to change mode.");
        toolTipMain.SetToolTip(qualitySlider, "JPEG compression quality. WebRTC uses its own H.264 bitrate target; this value is retained for JPEG fallback.");
        tableQualityStartup.ResumeLayout(true);
    }
}
