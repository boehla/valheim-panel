using System.Text.Json;

namespace ValheimPanel;

public class ReleaseInfo {
    public string Current { get; set; } = "";
    public string Latest { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public string Notes { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
}

/// <summary>
/// Pulls a newer single-file binary from the GitHub release and stages it as
/// valheim-panel.new. The systemd unit swaps it in on the next start, so the
/// running process never has to overwrite itself.
/// </summary>
public static class SelfUpdate {

    public const string InstallDir = "/opt/valheim-panel";
    public const string AssetName = "valheim-panel-linux-x64";

    static readonly HttpClient http = createClient();

    public static string CurrentVersion {
        get {
            string versionFile = Path.Combine(InstallDir, "VERSION");
            return File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "dev";
        }
    }

    public static string Repository {
        get => Environment.GetEnvironmentVariable("PANEL_REPO") ?? "boehla/valheim-panel";
    }

    public static async Task<ReleaseInfo> CheckAsync() {
        ReleaseInfo info = new ReleaseInfo { Current = CurrentVersion };

        string json = await http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest");
        using(JsonDocument doc = JsonDocument.Parse(json)) {
            JsonElement root = doc.RootElement;
            info.Latest = root.GetProperty("tag_name").GetString() ?? "";
            info.Notes = root.TryGetProperty("body", out JsonElement body) ? (body.GetString() ?? "") : "";

            foreach(JsonElement asset in root.GetProperty("assets").EnumerateArray()) {
                if(asset.GetProperty("name").GetString() == AssetName) {
                    info.DownloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    break;
                }
            }
        }

        info.UpdateAvailable = info.Latest.Length > 0
            && info.Latest.TrimStart('v') != info.Current.TrimStart('v')
            && info.DownloadUrl.Length > 0;
        return info;
    }

    public static async Task ApplyAsync(Action<string> log) {
        ReleaseInfo info = await CheckAsync();
        if(!info.UpdateAvailable) {
            log($"Kein Update verfügbar (installiert: {info.Current}).");
            return;
        }

        log($"Lade {info.Latest} von {info.DownloadUrl} ...");
        string staged = Path.Combine(InstallDir, "valheim-panel.new");

        using(Stream source = await http.GetStreamAsync(info.DownloadUrl))
        using(FileStream target = File.Create(staged)) {
            await source.CopyToAsync(target);
        }

        File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(InstallDir, "VERSION.new"), info.Latest);

        log("Binary abgelegt. Panel startet in 2 Sekunden neu ...");

        // The restart has to outlive this process, so hand it to systemd as a transient unit.
        await Shell.RunAsync("/usr/bin/systemd-run", [
            "--on-active=2", "--unit=valheim-panel-selfupdate",
            "/usr/bin/systemctl", "restart", "valheim-panel"
        ]);
    }

    static HttpClient createClient() {
        HttpClient client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("valheim-panel");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if(!string.IsNullOrWhiteSpace(token)) client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        return client;
    }
}
