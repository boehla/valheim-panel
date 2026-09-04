using System.Diagnostics;
using System.Text;

namespace ValheimPanel;

public class ShellResult {
    public int ExitCode { get; set; }
    public string StdOut { get; set; } = "";
    public string StdErr { get; set; } = "";
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// Thin wrapper around Process. Everything the panel does to the game server
/// goes through here, so there is exactly one place that touches the system.
/// </summary>
public static class Shell {

    public static async Task<ShellResult> RunAsync(string file, IEnumerable<string> args, int timeoutSeconds = 60) {
        ProcessStartInfo psi = new ProcessStartInfo {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach(string a in args) psi.ArgumentList.Add(a);

        StringBuilder stdout = new StringBuilder();
        StringBuilder stderr = new StringBuilder();

        using(Process proc = new Process()) {
            proc.StartInfo = psi;
            proc.OutputDataReceived += (_, e) => { if(e.Data != null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if(e.Data != null) stderr.AppendLine(e.Data); };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using(CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds))) {
                try {
                    await proc.WaitForExitAsync(cts.Token);
                } catch(OperationCanceledException) {
                    try { proc.Kill(true); } catch { }
                    return new ShellResult { ExitCode = -1, StdErr = $"Timeout after {timeoutSeconds}s" };
                }
            }

            return new ShellResult {
                ExitCode = proc.ExitCode,
                StdOut = stdout.ToString(),
                StdErr = stderr.ToString()
            };
        }
    }

    public static Task<ShellResult> SystemctlAsync(params string[] args) {
        return RunAsync("/usr/bin/systemctl", args, 180);
    }

    /// <summary>Reads a single systemd property, e.g. ActiveState or ExecMainStartTimestamp.</summary>
    public static async Task<string> ServicePropertyAsync(string unit, string property) {
        ShellResult res = await SystemctlAsync("show", unit, "--property", property, "--value");
        return res.Ok ? res.StdOut.Trim() : "";
    }
}
