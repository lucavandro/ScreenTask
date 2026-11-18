using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ScreenTask.Core.Network
{
    public static class FirewallHelper
    {
        public static async Task<bool> RuleExistsAsync(int port, bool udp = false)
        {
            if (port is < 1 or > 65535) return false;
            string ruleName = udp ? $"Screen Task UDP On Port {port}" : $"Screen Task On Port {port}";
            return await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = System.IO.Path.Combine(Environment.SystemDirectory, "netsh.exe"),
                        Arguments = $"advfirewall firewall show rule name=\"{ruleName}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        var output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit();
                        return output.Contains(ruleName, StringComparison.Ordinal);
                    }
                }
                catch { }
                return false;
            });
        }

        // The application calls this only after the user explicitly clicks Allow Port.
        public static async Task<bool> TryAddRuleAsync(int port, bool includeUdp = false)
        {
            if (port is < 1 or > 65535) return false;
            return await Task.Run(() =>
            {
                string script = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ScreenTask-firewall-{Guid.NewGuid():N}.txt");
                try
                {
                    string commands = BuildRuleScript(port, includeUdp);
                    System.IO.File.WriteAllText(script, commands, System.Text.Encoding.ASCII);
                    var psi = new ProcessStartInfo
                    {
                        FileName = System.IO.Path.Combine(Environment.SystemDirectory, "netsh.exe"),
                        Arguments = $"-f \"{script}\"",
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using var process = Process.Start(psi);
                    process?.WaitForExit();
                    return process?.ExitCode == 0;
                }
                catch { return false; }
                finally { try { System.IO.File.Delete(script); } catch { } }
            });
        }

        internal static string BuildRuleScript(int port, bool includeUdp)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
            string commands = $"advfirewall firewall add rule name=\"Screen Task On Port {port}\" dir=in action=allow protocol=tcp localport={port}\n";
            if (includeUdp) commands += $"advfirewall firewall add rule name=\"Screen Task UDP On Port {port}\" dir=in action=allow protocol=udp localport={port}\n";
            return commands;
        }
    }
}
