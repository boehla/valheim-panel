using System.Collections.Concurrent;

namespace ValheimPanel;

public class JobState {
    public string Name { get; set; } = "";
    public bool Running { get; set; }
    public bool Failed { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public List<string> Log { get; set; } = new List<string>();
}

/// <summary>
/// Exactly one long-running job at a time. A SteamCMD update and a panel
/// self-update must never overlap, and the UI only needs to poll one thing.
/// </summary>
public static class JobRunner {

    static readonly ConcurrentQueue<string> logLines = new ConcurrentQueue<string>();
    static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
    static JobState current = new JobState();

    public static bool IsBusy => current.Running;

    public static JobState Snapshot() {
        return new JobState {
            Name = current.Name,
            Running = current.Running,
            Failed = current.Failed,
            StartedAt = current.StartedAt,
            FinishedAt = current.FinishedAt,
            Log = logLines.ToList()
        };
    }

    public static bool TryStart(string name, Func<Action<string>, Task> work) {
        if(!gate.Wait(0)) return false;

        logLines.Clear();
        current = new JobState { Name = name, Running = true, StartedAt = DateTime.Now };

        _ = Task.Run(async () => {
            try {
                await work(append);
            } catch(Exception ex) {
                append($"FEHLER: {ex.Message}");
                current.Failed = true;
            } finally {
                current.Running = false;
                current.FinishedAt = DateTime.Now;
                gate.Release();
            }
        });
        return true;
    }

    static void append(string line) {
        logLines.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
        while(logLines.Count > 500) logLines.TryDequeue(out _);
    }
}
