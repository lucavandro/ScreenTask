# Windows Firewall port indicator

The last status-bar item replaces the former website/copyright link. Application
credits remain in Help/About and the existing source/assembly notices.

| Indicator | Meaning |
| --- | --- |
| Green — Allowed | The inspected Windows Firewall policy allows inbound TCP for the selected address, port and running executable. |
| Red — Blocked | Block-all mode, a matching block rule, or default inbound blocking without an applicable allow rule. |
| Amber — Unknown / Checking | The query is pending, restricted, managed, unsupported, unavailable or timed out. |
| Gray — Off | Windows Firewall is disabled for the selected interface/profile. |
| Gray — Local only | The selected address is loopback; other devices cannot connect to it. |

The tooltip includes the address, TCP port, network profile and reason. Clicking
the item opens Settings & Logs, where the existing explicit administrator action
can create an allow rule. A named rule's mere existence is not reported as proof
that the port is allowed.

## Inspection and refresh

In WebRTC mode both TCP and UDP on the selected port are evaluated. Either
protocol being blocked produces Blocked; a restricted or unknown result remains
Unknown. The explicit Allow Port action adds TCP and UDP rules together only
when WebRTC is selected. Merely selecting the mode never modifies firewall rules.

- Read-only `HNetCfg.FwPolicy2` queries run in the background. No elevation, rule
  creation, network probe or firewall configuration change is performed by the indicator.
- Network List Manager maps the selected adapter to its actual Public, Private or
  Domain profile. Unrelated active adapters are not treated as the selected network.
- Match enabled inbound TCP/any-protocol rules by profile, executable, local port,
  local address, service/package and interface scope. Explicit ordinary block rules
  take precedence over ordinary allows. Profile block-all and disabled/excluded
  interfaces are handled separately.
- Remote address/port restrictions, identity/IPsec requirements, unknown selectors
  and managed policy overrides produce Unknown when a definite decision cannot be
  made. This deliberately favors an uncertain indication over a false green light.
- Refresh on startup, port/interface changes, return to the app, completion of the
  explicit add-rule action, and every ten seconds, including while the broadcast is
  stopped. Port changes invalidate the previous indication immediately.
- Only one COM inspection can run at once. The UI timeout is five seconds; a stuck
  inspection does not queue more workers. Results for an old endpoint or disposed
  form are discarded. The refresh timer belongs to the form's component container.

This is a Windows Firewall **policy indication**, not a reachability guarantee.
It does not test router/AP isolation, third-party security software, separate IPsec
connection-security policies or a real connection from the viewing device. A
server can also be stopped even when its port would be permitted by the firewall.

## Validation

Pure policy tests cover allows, block precedence, block-all, default actions,
disabled firewall, wrong profiles/programs/ports/protocols/services/packages,
interface filters, port ranges, local subnets, restricted rules and loopback.
STA form tests cover stale results after a port change, failure-to-Unknown,
click navigation and disposal without waiting for a pending query.
Direct form renders verify Allowed, Blocked and Unknown in both light and dark
themes at the standard window size, without relying on desktop foreground focus.

A read-only local Windows 11 check reported TCP 7070 Allowed on the selected
Private network. A comparison port reported Unknown due to scoped rules. These
checks did not modify the machine's firewall. Different managed/third-party
configurations still require verification from an actual viewing device.

API references:

- [Windows Firewall policy API](https://learn.microsoft.com/en-us/windows/win32/api/netfw/nn-netfw-inetfwpolicy2)
- [Inbound block-all semantics](https://learn.microsoft.com/en-us/windows/win32/api/netfw/nf-netfw-inetfwpolicy2-get_blockallinboundtraffic)
- [Network adapter identity](https://learn.microsoft.com/en-us/windows/win32/api/netlistmgr/nf-netlistmgr-inetworkconnection-getadapterid)
- [Identity and IPsec rule constraints](https://learn.microsoft.com/en-us/windows/win32/api/netfw/nn-netfw-inetfwrule3)
