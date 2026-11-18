using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace ScreenTask.Core.Network;

/// <summary>Read-only Windows policy inspection. Never opens ports, elevates or probes the network.</summary>
internal static class FirewallPortInspector
{
    private static readonly SemaphoreSlim InspectionSlot = new(1, 1);

    internal static Task<FirewallPortStatus> CheckAsync(FirewallEndpoint endpoint)
    {
        if (IPAddress.TryParse(endpoint.Address, out var address) && IPAddress.IsLoopback(address))
            return Task.FromResult(new FirewallPortStatus(FirewallPortState.LocalOnly,
                "Localhost only: other devices cannot connect to this address."));
        // A timed-out COM call must not create a queue of additional probes.
        if (!InspectionSlot.Wait(0)) return Task.FromResult(new FirewallPortStatus(
            FirewallPortState.Unknown, "A Windows Firewall inspection is still pending."));
        return Task.Run(() =>
        {
            try { return Check(endpoint); }
            finally { InspectionSlot.Release(); }
        });
    }

    private static FirewallPortStatus Check(FirewallEndpoint endpoint)
    {
        object? policyObject = null, rulesObject = null;
        try
        {
            if (endpoint.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(endpoint.ApplicationPath))
                return new(FirewallPortState.Unknown, "The listening address, port or application path is unavailable.");
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == endpoint.Address));
            if (adapter == null) return new(FirewallPortState.Unknown, "The selected network interface is no longer available.");
            int profile = GetProfile(adapter.Id);
            policyObject = CreateCom("HNetCfg.FwPolicy2");
            dynamic policy = policyObject;
            if (profile == 0 || (((int)policy.CurrentProfileTypes & profile) == 0))
                return new(FirewallPortState.Unknown, "The active firewall profile for this network interface is unavailable.");
            bool managed = (int)policy.LocalPolicyModifyState != 0;
            bool enabled = policy.FirewallEnabled[profile];
            object excluded = policy.ExcludedInterfaces[profile];
            if (Strings(excluded).Contains(adapter.Name, StringComparer.OrdinalIgnoreCase)) enabled = false;
            var rules = new List<FirewallRuleSnapshot>();
            if (enabled && !managed && !(bool)policy.BlockAllInboundTraffic[profile])
            {
                rulesObject = policy.Rules;
                foreach (object item in (IEnumerable)rulesObject)
                {
                    try
                    {
                        dynamic r = item;
                        if (!(bool)r.Enabled || (int)r.Direction != 1 || (((int)r.Profiles & profile) == 0)) continue;
                        int protocol = r.Protocol;
                        if (protocol != endpoint.Protocol && protocol != 256) continue;
                        string package = "";
                        bool securityRestricted;
                        try
                        {
                            package = r.LocalAppPackageId ?? "";
                            securityRestricted = (int)r.SecureFlags != 0 ||
                                !string.IsNullOrEmpty((string?)r.LocalUserAuthorizedList) ||
                                !string.IsNullOrEmpty((string?)r.LocalUserOwner) ||
                                !string.IsNullOrEmpty((string?)r.RemoteUserAuthorizedList) ||
                                !string.IsNullOrEmpty((string?)r.RemoteMachineAuthorizedList);
                        }
                        catch { securityRestricted = true; }
                        rules.Add(new FirewallRuleSnapshot
                        {
                            Allow = (int)r.Action == 1, Protocol = protocol, Profiles = r.Profiles,
                            Application = r.ApplicationName ?? "", Service = r.ServiceName ?? "", Package = package,
                            LocalPorts = protocol == 256 ? "*" : r.LocalPorts ?? "*",
                            RemotePorts = protocol == 256 ? "*" : r.RemotePorts ?? "*",
                            LocalAddresses = r.LocalAddresses ?? "*", RemoteAddresses = r.RemoteAddresses ?? "*",
                            Interfaces = Strings(r.Interfaces), InterfaceTypes = r.InterfaceTypes ?? "All",
                            SecurityRestricted = securityRestricted
                        });
                    }
                    finally { Release(item); }
                }
            }
            string type = adapter.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wireless",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or
                    NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT => "LAN",
                NetworkInterfaceType.Ppp => "RemoteAccess",
                _ => ""
            };
            return FirewallPolicyEvaluator.Evaluate(endpoint, new(profile, enabled,
                (bool)policy.BlockAllInboundTraffic[profile], (int)policy.DefaultInboundAction[profile] == 1,
                managed, adapter.Name, type, rules));
        }
        catch (Exception ex)
        {
            return new(FirewallPortState.Unknown, $"Windows Firewall status could not be read ({ex.GetType().Name}).");
        }
        finally { Release(rulesObject); Release(policyObject); }
    }

    private static int GetProfile(string adapterId)
    {
        object? manager = null, connections = null;
        try
        {
            manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), true)
                ?? throw new InvalidOperationException("Network List Manager unavailable"));
            if (manager == null) return 0;
            connections = ((dynamic)manager).GetNetworkConnections();
            var profiles = new HashSet<int>();
            foreach (object item in (IEnumerable)connections)
            {
                object? network = null;
                try
                {
                    var connection = (INetworkConnection)item;
                    if (connection.GetAdapterId() != Guid.Parse(adapterId) || !connection.IsConnected) continue;
                    network = connection.GetNetwork();
                    int category = ((dynamic)network).GetCategory();
                    profiles.Add(category switch { 0 => 4, 1 => 2, 2 => 1, _ => 0 });
                }
                finally { Release(network); Release(item); }
            }
            return profiles.Count == 1 ? profiles.Single() : 0;
        }
        finally { Release(connections); Release(manager); }
    }

    private static object CreateCom(string name) => Activator.CreateInstance(
        Type.GetTypeFromProgID(name, true) ?? throw new InvalidOperationException(name + " unavailable"))
        ?? throw new InvalidOperationException(name + " unavailable");

    private static string[] Strings(object? value) => value is Array array
        ? array.Cast<object>().Select(Convert.ToString).OfType<string>().ToArray() : [];

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    // Early binding is required: IDispatch cannot marshal the GUID return value.
    // Method order follows the Windows SDK's dual INetworkConnection interface.
    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork();
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
        int GetDomainType();
    }
}
