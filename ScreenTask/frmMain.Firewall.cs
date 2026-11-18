using System.Drawing;
using System.Windows.Forms;
using ScreenTask.Core.Network;
using ScreenTask.UI;

namespace ScreenTask;

public partial class frmMain
{
    private bool _firewallCheckRunning;
    private bool _firewallRefreshPending;
    private FirewallEndpoint? _lastFirewallEndpoint;
    internal Func<FirewallEndpoint, Task<FirewallPortStatus>> FirewallStatusProvider { get; set; } = FirewallPortInspector.CheckAsync;

    private void InitializeFirewallIndicator()
    {
        // Owned by the form's component container; it also runs while broadcasting is stopped.
        var timer = new System.Windows.Forms.Timer(components) { Interval = 10000 };
        timer.Tick += (s, e) => _ = RefreshFirewallStatusAsync();
        Load += (s, e) => { timer.Start(); _ = RefreshFirewallStatusAsync(); };
        Activated += (s, e) => _ = RefreshFirewallStatusAsync();
        statusLabelFirewall.Click += (s, e) => { SelectTab(2); _ = RefreshFirewallStatusAsync(); };
    }

    private FirewallEndpoint? GetFirewallEndpoint() => comboIPs.SelectedIndex >= 0 && comboIPs.SelectedIndex < _adapters.Count
        ? new(_adapters[comboIPs.SelectedIndex].IpAddress, (int)numPort.Value, Environment.ProcessPath ?? "") : null;

    internal async Task RefreshFirewallStatusAsync()
    {
        if (_isLoadingSettings || !IsHandleCreated || IsDisposed || Disposing || _isShuttingDown) return;
        var selected = GetFirewallEndpoint();
        if (selected == null) return;
        if (selected != _lastFirewallEndpoint)
        {
            _lastFirewallEndpoint = selected;
            statusLabelFirewall.Text = $"● Firewall: Checking ({selected.Port})";
            statusLabelFirewall.ForeColor = DesktopTheme.Warning;
            statusLabelFirewall.ToolTipText = $"Checking Windows Firewall for {selected.Address}:{selected.Port}...";
            statusLabelFirewall.AccessibleDescription = statusLabelFirewall.ToolTipText;
        }
        if (_firewallCheckRunning) { _firewallRefreshPending = true; return; }
        _firewallCheckRunning = true;
        try
        {
            do
            {
                _firewallRefreshPending = false;
                var endpoint = GetFirewallEndpoint();
                if (endpoint == null) return;
                FirewallPortStatus status;
                try { status = await FirewallStatusProvider(endpoint).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { status = new(FirewallPortState.Unknown, "Windows Firewall inspection timed out."); }
                catch { status = new(FirewallPortState.Unknown, "Windows Firewall status could not be read."); }
                if (IsDisposed || Disposing || _isShuttingDown) return;
                if (endpoint != GetFirewallEndpoint()) { _firewallRefreshPending = true; continue; }
                ShowFirewallStatus(endpoint, status);
            } while (_firewallRefreshPending);
        }
        finally { _firewallCheckRunning = false; }
    }

    internal void ShowFirewallStatus(FirewallEndpoint endpoint, FirewallPortStatus status)
    {
        string caption = status.State switch
        {
            FirewallPortState.Allowed => "Allowed",
            FirewallPortState.Blocked => "Blocked",
            FirewallPortState.LocalOnly => "Local only",
            FirewallPortState.Disabled => "Off",
            _ => "Unknown"
        };
        statusLabelFirewall.Text = $"● Firewall: {caption} ({endpoint.Port})";
        statusLabelFirewall.ForeColor = SystemInformation.HighContrast ? SystemColors.ControlText : status.State switch
        {
            FirewallPortState.Allowed => DesktopTheme.Success,
            FirewallPortState.Blocked => DesktopTheme.IsDark ? Color.FromArgb(255, 148, 140) : Color.FromArgb(180, 35, 35),
            FirewallPortState.Unknown => DesktopTheme.Warning,
            _ => SystemColors.GrayText
        };
        statusLabelFirewall.ToolTipText = $"{endpoint.Address}:{endpoint.Port} (TCP)\n{status.Detail}\n" +
            "Windows Firewall policy only; Wi-Fi/router and third-party filters are not tested. Click for firewall settings.";
        statusLabelFirewall.AccessibleDescription = statusLabelFirewall.ToolTipText;
    }
}
