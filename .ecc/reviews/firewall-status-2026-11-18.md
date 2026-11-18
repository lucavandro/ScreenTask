# Firewall indicator review — 2026-11-18

The requested status-bar replacement is implemented with read-only background
Windows Firewall inspection, endpoint invalidation and explicit uncertainty.

| Axis | Score | Evidence / remaining limit |
| --- | --- | --- |
| Accuracy | 4 | Policy and STA UI cases pass; a real read-only Windows 11 probe found 7070 Allowed on Private. Policy inspection is explicitly not a remote connection test. |
| Completeness | 4 | Covers current port/interface, idle/live refresh, stale results, timeouts, loopback, disabled firewall, scoped rules and managed-policy uncertainty. Windows 10 and every enterprise policy variant were not exercised on separate machines. |
| Clarity | 4 | Text and color identify the state; tooltip gives endpoint, profile, cause and scope. No reliance on color alone. |
| Actionability | 5 | Clicking the indicator opens the existing firewall settings controls; source, documentation and a self-contained portable are supplied. |
| Conciseness | 4 | Inspector, pure policy evaluator and form behavior are separated. Conservative scope handling adds code but avoids claiming a mere named rule proves permission. |

Overall: 4.2/5.

Validation: 94 .NET regressions, 3 JS decoder tests, Release builds with zero
warnings/errors. Direct form renders checked Allowed/Blocked/Unknown in both
light and dark themes. Initial foreground captures were discarded/overwritten
because they showed another window; final UI verification uses DrawToBitmap.
No firewall rules were created or changed during the checks.

Highest-value follow-up if requested: verify a real viewer connection on the
user's network, especially with managed policies or third-party filters.
Self-check: the user receives the requested indicator and can see uncertainty
instead of a false assurance of network reachability.
