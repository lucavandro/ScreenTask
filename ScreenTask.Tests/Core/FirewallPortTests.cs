using ScreenTask.Core.Network;

namespace ScreenTask.Tests.Core;

public class FirewallPortTests
{
    private static readonly FirewallEndpoint Endpoint = new("192.168.1.10", 7070, @"C:\ScreenTask\ScreenTask.exe");
    private static readonly FirewallRuleSnapshot AllowPort = new() { LocalPorts = "7070" };
    private static FirewallPolicySnapshot Policy(params FirewallRuleSnapshot[] rules) => new(2, true, false, false, false, "Wi-Fi", "Wireless", rules);

    [Fact]
    public void MatchingAllowAndDefaultBlock_AreDistinguished()
    {
        Assert.Equal(FirewallPortState.Blocked, FirewallPolicyEvaluator.Evaluate(Endpoint, Policy()).State);
        Assert.Equal(FirewallPortState.Allowed, FirewallPolicyEvaluator.Evaluate(Endpoint, Policy(AllowPort)).State);
        Assert.Equal(FirewallPortState.Allowed, FirewallPolicyEvaluator.Evaluate(Endpoint, Policy() with { DefaultAllow = true }).State);
    }

    [Fact]
    public void ExplicitBlockAndBlockAll_OverrideOrdinaryAllows()
    {
        Assert.Equal(FirewallPortState.Blocked, FirewallPolicyEvaluator.Evaluate(Endpoint,
            Policy(AllowPort, AllowPort with { Allow = false })).State);
        Assert.Equal(FirewallPortState.Blocked, FirewallPolicyEvaluator.Evaluate(Endpoint,
            Policy(AllowPort with { RemoteAddresses = "LocalSubnet" }, AllowPort with { Allow = false })).State);
        Assert.Equal(FirewallPortState.Blocked, FirewallPolicyEvaluator.Evaluate(Endpoint,
            Policy(AllowPort) with { BlockAll = true }).State);
    }

    [Fact]
    public void WrongApplicationProfilePortProtocolAndService_DoNotOpenPort()
    {
        var rules = new[]
        {
            AllowPort with { Enabled = false }, AllowPort with { Inbound = false },
            AllowPort with { Profiles = 4 }, AllowPort with { LocalPorts = "9090" },
            AllowPort with { Protocol = 17 }, AllowPort with { Application = @"C:\Other.exe" },
            AllowPort with { Service = "OtherService" }, AllowPort with { Package = "S-1-15-2-123" },
            AllowPort with { InterfaceTypes = "LAN" }, AllowPort with { Interfaces = ["Ethernet"] },
            AllowPort with { LocalAddresses = "10.1.1.1" }
        };
        foreach (var rule in rules)
            Assert.Equal(FirewallPortState.Blocked, FirewallPolicyEvaluator.Evaluate(Endpoint, Policy(rule)).State);
    }

    [Fact]
    public void PortRangesAddressSubnetsAndApplicationScope_MatchSelectedEndpoint()
    {
        var rule = AllowPort with
        {
            LocalPorts = "80,7000-7090", LocalAddresses = "192.168.1.0/24", Interfaces = ["wi-fi"],
            Application = Endpoint.ApplicationPath.ToUpperInvariant(), InterfaceTypes = "Wireless,LAN"
        };
        Assert.Equal(FirewallPortState.Allowed, FirewallPolicyEvaluator.Evaluate(Endpoint, Policy(rule)).State);
    }

    [Fact]
    public void RestrictedRulesAndPolicyOverrides_NeverReportUnconditionalAllow()
    {
        foreach (var rule in new[]
        {
            AllowPort with { RemoteAddresses = "LocalSubnet" }, AllowPort with { RemotePorts = "50000-60000" },
            AllowPort with { LocalPorts = "RPC" }, AllowPort with { LocalAddresses = "DHCP" },
            AllowPort with { SecurityRestricted = true }
        })
            Assert.Equal(FirewallPortState.Unknown, FirewallPolicyEvaluator.Evaluate(Endpoint, Policy(rule)).State);
        Assert.Equal(FirewallPortState.Unknown, FirewallPolicyEvaluator.Evaluate(Endpoint, Policy(AllowPort) with { Managed = true }).State);
        Assert.Equal(FirewallPortState.Unknown, FirewallPolicyEvaluator.Evaluate(Endpoint, Policy(AllowPort) with { Profile = 6 }).State);
        Assert.Equal(FirewallPortState.Unknown, FirewallPolicyEvaluator.Evaluate(Endpoint,
            Policy(AllowPort, AllowPort with { Allow = false, RemoteAddresses = "192.168.1.50" })).State);
        Assert.Equal(FirewallPortState.Unknown, FirewallPolicyEvaluator.Evaluate(Endpoint,
            Policy(AllowPort with { Allow = false }, AllowPort with { SecurityRestricted = true })).State);
    }

    [Fact]
    public void DisabledFirewall_IsExplicitAndNotReportedBlocked()
    {
        Assert.Equal(FirewallPortState.Disabled, FirewallPolicyEvaluator.Evaluate(Endpoint,
            Policy() with { Enabled = false, BlockAll = true }).State);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task Loopback_IsLocalOnly_WithoutRequiringPolicyAccess(string address)
    {
        var endpoint = new FirewallEndpoint(address, 7070, "");
        Assert.Equal(FirewallPortState.LocalOnly, (await FirewallPortInspector.CheckAsync(endpoint)).State);
        Assert.Equal(FirewallPortState.LocalOnly, FirewallPolicyEvaluator.Evaluate(endpoint, Policy()).State);
    }
}
