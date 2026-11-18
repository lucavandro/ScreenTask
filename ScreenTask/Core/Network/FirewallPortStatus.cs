using System.Net;

namespace ScreenTask.Core.Network;

internal enum FirewallPortState { Allowed, Blocked, Unknown, LocalOnly, Disabled }

internal sealed record FirewallPortStatus(FirewallPortState State, string Detail);
internal sealed record FirewallEndpoint(string Address, int Port, string ApplicationPath, int Protocol = 6, bool IncludeUdp = false);

// Plain snapshots let the policy decision be tested without altering Windows Firewall.
internal sealed record FirewallRuleSnapshot
{
    public bool Enabled { get; init; } = true;
    public bool Inbound { get; init; } = true;
    public bool Allow { get; init; } = true;
    public int Profiles { get; init; } = 7;
    public int Protocol { get; init; } = 6;
    public string Application { get; init; } = "";
    public string Service { get; init; } = "";
    public string Package { get; init; } = "";
    public string LocalPorts { get; init; } = "*";
    public string LocalAddresses { get; init; } = "*";
    public string RemotePorts { get; init; } = "*";
    public string RemoteAddresses { get; init; } = "*";
    public string InterfaceTypes { get; init; } = "All";
    public string[] Interfaces { get; init; } = [];
    public bool SecurityRestricted { get; init; }
}

internal sealed record FirewallPolicySnapshot(int Profile, bool Enabled, bool BlockAll,
    bool DefaultAllow, bool Managed, string InterfaceName, string InterfaceType,
    IReadOnlyList<FirewallRuleSnapshot> Rules);

internal static class FirewallPolicyEvaluator
{
    private enum Match { No, Yes, Uncertain }

    internal static FirewallPortStatus Evaluate(FirewallEndpoint endpoint, FirewallPolicySnapshot policy)
    {
        if (IPAddress.TryParse(endpoint.Address, out var local) && IPAddress.IsLoopback(local))
            return new(FirewallPortState.LocalOnly, "Localhost only: other devices cannot connect to this address.");
        if (policy.Profile is not (1 or 2 or 4) || policy.Managed)
            return new(FirewallPortState.Unknown, "The network profile or managed firewall policy cannot be evaluated reliably.");
        string profile = policy.Profile switch { 1 => "Domain", 2 => "Private", _ => "Public" };
        string protocol = endpoint.Protocol == 17 ? "UDP" : "TCP";
        if (!policy.Enabled)
            return new(FirewallPortState.Disabled, $"Windows Firewall is disabled for this interface/profile ({profile}).");
        if (policy.BlockAll)
            return new(FirewallPortState.Blocked, $"{profile}: all inbound connections are blocked; exceptions are ignored.");

        bool allow = policy.DefaultAllow, possibleAllow = false, possibleBypass = false, block = false, possibleBlock = false;
        foreach (var rule in policy.Rules)
        {
            var match = Matches(rule, endpoint, policy);
            if (match == Match.No) continue;
            if (rule.Allow)
            {
                allow |= match == Match.Yes;
                possibleAllow |= match == Match.Uncertain;
                possibleBypass |= rule.SecurityRestricted;
            }
            else
            {
                block |= match == Match.Yes;
                possibleBlock |= match == Match.Uncertain;
            }
        }
        // Authenticated bypass rules may override a block; do not guess their identity/IPsec checks.
        if (block && !possibleBypass)
            return new(FirewallPortState.Blocked, $"{profile}: a matching inbound block rule takes precedence over ordinary allow rules.");
        if (block || (possibleBlock && (allow || possibleAllow)) || (!allow && possibleAllow))
            return new(FirewallPortState.Unknown, $"{profile}: scoped, authenticated or unsupported rules require verification from the viewer device.");
        return allow
            ? new(FirewallPortState.Allowed, $"{profile}: inbound {protocol} is allowed by the inspected Windows Firewall policy.")
            : new(FirewallPortState.Blocked, $"{profile}: default inbound policy blocks {protocol} and no applicable allow rule was found.");
    }

    private static Match Matches(FirewallRuleSnapshot rule, FirewallEndpoint endpoint, FirewallPolicySnapshot policy)
    {
        if (!rule.Enabled || !rule.Inbound || (rule.Profiles & policy.Profile) == 0 ||
            (rule.Protocol != endpoint.Protocol && rule.Protocol != 256) || !string.IsNullOrWhiteSpace(rule.Service) || !string.IsNullOrWhiteSpace(rule.Package))
            return Match.No;
        if (!string.IsNullOrWhiteSpace(rule.Application) && rule.Application != "*" &&
            !string.Equals(Environment.ExpandEnvironmentVariables(rule.Application), endpoint.ApplicationPath, StringComparison.OrdinalIgnoreCase))
            return Match.No;
        var port = rule.Protocol == 256 ? Match.Yes : MatchPort(rule.LocalPorts, endpoint.Port);
        var address = MatchAddress(rule.LocalAddresses, endpoint.Address);
        if (port == Match.No || address == Match.No) return Match.No;
        if (rule.Interfaces.Length > 0 && !rule.Interfaces.Contains(policy.InterfaceName, StringComparer.OrdinalIgnoreCase)) return Match.No;
        if (!rule.InterfaceTypes.Equals("All", StringComparison.OrdinalIgnoreCase) &&
            !rule.InterfaceTypes.Split(',').Any(t => t.Trim().Equals(policy.InterfaceType, StringComparison.OrdinalIgnoreCase)))
            return string.IsNullOrEmpty(policy.InterfaceType) ? Match.Uncertain : Match.No;
        return port == Match.Uncertain || address == Match.Uncertain || rule.SecurityRestricted ||
            !IsAny(rule.RemoteAddresses) || (rule.Protocol != 256 && !IsAny(rule.RemotePorts)) ? Match.Uncertain : Match.Yes;
    }

    internal static FirewallPortStatus CombineTcpUdp(FirewallPortStatus tcp, FirewallPortStatus udp)
    {
        FirewallPortState state = tcp.State == FirewallPortState.Blocked || udp.State == FirewallPortState.Blocked ? FirewallPortState.Blocked
            : tcp.State == FirewallPortState.Unknown || udp.State == FirewallPortState.Unknown ? FirewallPortState.Unknown
            : tcp.State == FirewallPortState.LocalOnly && udp.State == FirewallPortState.LocalOnly ? FirewallPortState.LocalOnly
            : tcp.State == FirewallPortState.Disabled && udp.State == FirewallPortState.Disabled ? FirewallPortState.Disabled
            : FirewallPortState.Allowed;
        return new(state, $"TCP: {tcp.Detail}\nUDP: {udp.Detail}");
    }

    private static bool IsAny(string value) => value.Trim() == "*";

    private static Match MatchPort(string value, int port)
    {
        if (IsAny(value)) return Match.Yes;
        bool uncertain = false;
        foreach (string item in value.Split(','))
        {
            string[] range = item.Trim().Split('-');
            if (range.Length == 1 && int.TryParse(range[0], out int single))
            {
                if (single == port) return Match.Yes;
            }
            else if (range.Length == 2 && int.TryParse(range[0], out int first) && int.TryParse(range[1], out int last))
            {
                if (port >= first && port <= last) return Match.Yes;
            }
            else uncertain = true;
        }
        return uncertain ? Match.Uncertain : Match.No;
    }

    private static Match MatchAddress(string value, string address)
    {
        if (IsAny(value)) return Match.Yes;
        if (!IPAddress.TryParse(address, out var local)) return Match.Uncertain;
        bool uncertain = false;
        foreach (string raw in value.Split(','))
        {
            string item = raw.Trim();
            if (IPAddress.TryParse(item, out var ip))
            {
                if (local.Equals(ip)) return Match.Yes;
            }
            else if (IPNetwork.TryParse(item, out var subnet))
            {
                if (subnet.Contains(local)) return Match.Yes;
            }
            else uncertain = true; // Includes address ranges and Windows-specific keywords.
        }
        return uncertain ? Match.Uncertain : Match.No;
    }
}
