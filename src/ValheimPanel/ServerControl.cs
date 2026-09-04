using System.Text.RegularExpressions;

namespace ValheimPanel;

public class ServerStatus {
    public string State { get; set; } = "unknown";
    public bool Running { get; set; }
    public DateTime? StartedAt { get; set; }
    public string BuildId { get; set; } = "";
    public int PlayersOnline { get; set; }
    public string WorldName { get; set; } = "";
    public long WorldSizeBytes { get; set; }
    public DateTime? WorldSavedAt { get; set; }
    public string PanelVersion { get; set; } = "";
}

public static partial class ServerControl {

    public const string ServerDir = "/opt/valheim/server";
    public const string SteamCmd = "/opt/valheim/steamcmd/steamcmd.sh";
    public const string Unit = "valheim";
    public const string SteamAppId = "896660";

    [GeneratedRegex(@"Got connection SteamID (\d+)")]
    private static partial Regex ConnectRegex();

    [GeneratedRegex(@"Closing socket (\d+)")]
    private static partial Regex DisconnectRegex();

    public static async Task<ServerStatus> GetStatusAsync() {
        ServerSettings settings = ServerSettings.Load();
        ServerStatus status = new ServerStatus {
            WorldName = settings.WorldName,
            BuildId = readBuildId(),
            PanelVersion = SelfUpdate.CurrentVersion
        };

        status.State = await Shell.ServicePropertyAsync(Unit, "ActiveState");
        status.Running = status.State == "active";

        string startedRaw = await Shell.ServicePropertyAsync(Unit, "ExecMainStartTimestamp");
        if(DateTime.TryParse(startedRaw, out DateTime started)) status.StartedAt = started;

        string worldFile = Path.Combine(settings.SaveDir, "worlds_local", $"{settings.WorldName}.db");
        if(File.Exists(worldFile)) {
            FileInfo info = new FileInfo(worldFile);
            status.WorldSizeBytes = info.Length;
            status.WorldSavedAt = info.LastWriteTime;
        }

        if(status.Running) status.PlayersOnline = await countPlayersAsync();
        return status;
    }

    public static Task<ShellResult> StartAsync() => Shell.SystemctlAsync("start", Unit);

    public static Task<ShellResult> StopAsync() => Shell.SystemctlAsync("stop", Unit);

    public static Task<ShellResult> RestartAsync() => Shell.SystemctlAsync("restart", Unit);

    public static async Task<string> GetLogAsync(int lines) {
        ShellResult res = await Shell.RunAsync("/usr/bin/journalctl", ["-u", Unit, "-n", lines.ToString(), "--no-pager", "-o", "cat"]);
        return res.Ok ? res.StdOut : res.StdErr;
    }

    /// <summary>
    /// Stops the server, runs SteamCMD, starts it again. Reported through JobRunner
    /// because a full app_update easily runs for several minutes.
    /// </summary>
    public static async Task UpdateServerAsync(Action<string> log) {
        bool wasRunning = (await Shell.ServicePropertyAsync(Unit, "ActiveState")) == "active";

        if(wasRunning) {
            log("Stoppe Server (SIGINT, Welt wird gespeichert) ...");
            await StopAsync();
        }

        log("SteamCMD: app_update 896660 validate ...");
        ShellResult res = await Shell.RunAsync(SteamCmd, [
            "+force_install_dir", ServerDir,
            "+login", "anonymous",
            "+app_update", SteamAppId, "validate",
            "+quit"
        ], 1800);

        foreach(string line in res.StdOut.Split('\n')) {
            if(line.Trim().Length > 0) log(line.TrimEnd());
        }

        if(!res.Ok) {
            log($"SteamCMD fehlgeschlagen (Exit {res.ExitCode}). Server wird nicht gestartet.");
            return;
        }

        log($"Update fertig. Build-ID: {readBuildId()}");

        if(wasRunning) {
            log("Starte Server ...");
            await StartAsync();
        }
    }

    static string readBuildId() {
        string manifest = Path.Combine(ServerDir, "steamapps", $"appmanifest_{SteamAppId}.acf");
        if(!File.Exists(manifest)) return "";
        Match match = Regex.Match(File.ReadAllText(manifest), "\"buildid\"\\s+\"(\\d+)\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>
    /// Valheim has no query protocol we can hit, so we replay the connect/disconnect
    /// lines from the journal since the current start and count what is left open.
    /// </summary>
    static async Task<int> countPlayersAsync() {
        ShellResult res = await Shell.RunAsync("/usr/bin/journalctl", ["-u", Unit, "-n", "5000", "--no-pager", "-o", "cat"]);
        if(!res.Ok) return 0;

        HashSet<string> connected = new HashSet<string>();
        foreach(string line in res.StdOut.Split('\n')) {
            Match connect = ConnectRegex().Match(line);
            if(connect.Success) {
                connected.Add(connect.Groups[1].Value);
                continue;
            }
            Match disconnect = DisconnectRegex().Match(line);
            if(disconnect.Success) connected.Remove(disconnect.Groups[1].Value);
        }
        return connected.Count;
    }
}
