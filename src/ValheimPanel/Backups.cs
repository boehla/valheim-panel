using System.Text.RegularExpressions;

namespace ValheimPanel;

public class BackupInfo {
    public string FileName { get; set; } = "";
    public string WorldName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public long SizeBytes { get; set; }
    public bool Permanent { get; set; }
}

/// <summary>
/// Panel-side world backups, separate from the ones Valheim rotates itself. A backup
/// is a tar.gz of the four files that make up a world, named
/// "20260909-181500_perma_Kanisfjall.tar.gz" -- timestamp, kind, world. Everything the
/// panel knows about a backup is in that name, so there is no index to keep in sync.
/// </summary>
public static partial class Backups {

    public const string BackupDir = "/opt/valheim/backups";

    /// <summary>Temporary backups pruned past this count. Permanent ones are never touched.</summary>
    const int keepTemporary = 10;

    // The strict shape is also the guard for restore and delete: a name that does not
    // match never reaches the filesystem.
    [GeneratedRegex(@"^(\d{8}-\d{6})_(perma|temp)_(.+)\.tar\.gz$")]
    private static partial Regex BackupNameRegex();

    public static List<BackupInfo> List() {
        if(!Directory.Exists(BackupDir)) return new List<BackupInfo>();

        List<BackupInfo> backups = new List<BackupInfo>();
        foreach(string path in Directory.GetFiles(BackupDir, "*.tar.gz")) {
            BackupInfo? info = describe(Path.GetFileName(path));
            if(info != null) backups.Add(info);
        }
        return backups.OrderByDescending(b => b.CreatedAt).ToList();
    }

    /// <summary>
    /// Archives the current world. Returns the file name, or "" when there is no world
    /// on disk yet -- that is the normal state before the server has ever run, not an error.
    /// </summary>
    public static async Task<string> CreateAsync(Action<string> log, bool permanent) {
        ServerSettings settings = ServerSettings.Load();
        string worldDir = Path.Combine(settings.SaveDir, "worlds_local");

        List<string> present = worldFiles(settings.WorldName).Where(f => File.Exists(Path.Combine(worldDir, f))).ToList();
        if(present.Count == 0) {
            log($"Keine Weltdateien für \"{settings.WorldName}\" gefunden — nichts zu sichern.");
            return "";
        }

        Directory.CreateDirectory(BackupDir);
        File.SetUnixFileMode(BackupDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string kind = permanent ? "perma" : "temp";
        string name = $"{DateTime.Now:yyyyMMdd-HHmmss}_{kind}_{sanitize(settings.WorldName)}.tar.gz";
        string target = Path.Combine(BackupDir, name);

        log($"Sichere {present.Count} Datei(en) nach {name} ...");
        // "--" so a world name starting with a dash is never read as an option.
        List<string> args = new List<string> { "-czf", target, "-C", worldDir, "--" };
        args.AddRange(present);

        ShellResult res = await Shell.RunAsync("/usr/bin/tar", args, 600);
        if(!res.Ok) {
            // A half-written archive is worse than none: it would look restorable.
            try { File.Delete(target); } catch { }
            throw new Exception($"tar fehlgeschlagen: {res.StdErr.Trim()}");
        }

        long size = new FileInfo(target).Length;
        log($"Sicherung erstellt: {name} ({size / 1048576.0:F1} MB){(permanent ? ", dauerhaft" : "")}");

        if(!permanent) prune(log);
        return name;
    }

    /// <summary>
    /// Stops the server, puts the archived world back, starts it again. The world that
    /// is being replaced is archived first -- a restore is as destructive as a delete.
    /// </summary>
    public static async Task RestoreAsync(Action<string> log, string fileName) {
        BackupInfo info = describe(fileName) ?? throw new Exception("Unbekannte Sicherung.");
        string source = Path.Combine(BackupDir, info.FileName);
        if(!File.Exists(source)) throw new Exception("Sicherung nicht gefunden.");

        ServerSettings settings = ServerSettings.Load();
        string worldDir = Path.Combine(settings.SaveDir, "worlds_local");

        bool wasRunning = (await Shell.ServicePropertyAsync(ServerControl.Unit, "ActiveState")) == "active";
        if(wasRunning) {
            log("Stoppe Server (SIGINT, Welt wird gespeichert) ...");
            await ServerControl.StopAsync();
        }

        log("Sichere den aktuellen Stand, bevor er überschrieben wird ...");
        await CreateAsync(log, false);

        Directory.CreateDirectory(worldDir);
        log($"Stelle {info.FileName} wieder her ...");
        ShellResult res = await Shell.RunAsync("/usr/bin/tar", ["-xzf", source, "-C", worldDir], 600);
        if(!res.Ok) throw new Exception($"tar fehlgeschlagen: {res.StdErr.Trim()}");

        if(!info.WorldName.Equals(sanitize(settings.WorldName), StringComparison.OrdinalIgnoreCase)) {
            log($"ACHTUNG: Die Sicherung enthält die Welt \"{info.WorldName}\", eingestellt ist aber \"{settings.WorldName}\".");
            log($"Der Server lädt sie erst, wenn du den Weltnamen in den Einstellungen auf \"{info.WorldName}\" änderst.");
        }

        if(wasRunning) {
            log("Starte Server ...");
            await ServerControl.StartAsync();
        }
        log("Wiederherstellung abgeschlossen.");
    }

    public static void Delete(string fileName) {
        BackupInfo info = describe(fileName) ?? throw new Exception("Unbekannte Sicherung.");
        string path = Path.Combine(BackupDir, info.FileName);
        if(File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Throws away the current world so Valheim generates a fresh one on the next start.
    /// The permanent backup is made first and its success is the precondition for
    /// deleting anything -- this is the one operation with no undo.
    /// </summary>
    public static async Task RegenerateAsync(Action<string> log, string newWorldName) {
        ServerSettings settings = ServerSettings.Load();
        string worldDir = Path.Combine(settings.SaveDir, "worlds_local");

        bool wasRunning = (await Shell.ServicePropertyAsync(ServerControl.Unit, "ActiveState")) == "active";
        if(wasRunning) {
            log("Stoppe Server (SIGINT, Welt wird gespeichert) ...");
            await ServerControl.StopAsync();
        }

        log("Lege eine dauerhafte Sicherung an. Ohne sie wird nichts gelöscht.");
        string archive = await CreateAsync(log, true);

        List<string> present = worldFiles(settings.WorldName)
            .Where(f => File.Exists(Path.Combine(worldDir, f))).ToList();

        // Nothing is deleted that is not in an archive we just wrote successfully.
        if(present.Count > 0 && archive.Length == 0) throw new Exception("Sicherung fehlt — Löschen abgebrochen.");

        foreach(string file in present) {
            File.Delete(Path.Combine(worldDir, file));
            log($"Gelöscht: {file}");
        }

        if(newWorldName.Length > 0 && newWorldName != settings.WorldName) {
            log($"Weltname: \"{settings.WorldName}\" → \"{newWorldName}\"");
            settings.WorldName = newWorldName;
            List<string> errors = settings.Validate();
            if(errors.Count > 0) throw new Exception(string.Join(" ", errors));
            settings.Save();
        }

        if(wasRunning) {
            log("Starte Server ...");
            await ServerControl.StartAsync();
            log("Der Server generiert jetzt eine neue Welt. Das dauert beim ersten Start ein bis zwei Minuten.");
        } else {
            log("Server war gestoppt. Die neue Welt entsteht beim nächsten Start.");
        }

        if(archive.Length > 0) log($"Die alte Welt liegt dauerhaft unter {BackupDir}/{archive}.");
    }

    /// <summary>The four files Valheim keeps per world: the world, its metadata, and one generation of each.</summary>
    static IEnumerable<string> worldFiles(string world) {
        return [$"{world}.db", $"{world}.fwl", $"{world}.db.old", $"{world}.fwl.old"];
    }

    static BackupInfo? describe(string fileName) {
        Match match = BackupNameRegex().Match(fileName);
        if(!match.Success) return null;
        if(!DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd-HHmmss", null,
            System.Globalization.DateTimeStyles.None, out DateTime created)) return null;

        string path = Path.Combine(BackupDir, fileName);
        return new BackupInfo {
            FileName = fileName,
            WorldName = match.Groups[3].Value,
            CreatedAt = created,
            SizeBytes = File.Exists(path) ? new FileInfo(path).Length : 0,
            Permanent = match.Groups[2].Value == "perma"
        };
    }

    static void prune(Action<string> log) {
        List<BackupInfo> temporary = List().Where(b => !b.Permanent).Skip(keepTemporary).ToList();
        foreach(BackupInfo old in temporary) {
            try {
                File.Delete(Path.Combine(BackupDir, old.FileName));
                log($"Alte Sicherung entfernt: {old.FileName}");
            } catch(Exception ex) {
                log($"Konnte {old.FileName} nicht entfernen: {ex.Message}");
            }
        }
    }

    /// <summary>World names reach the filesystem, so anything that could leave the directory goes.</summary>
    static string sanitize(string world) {
        string clean = new string(world.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == ' ').ToArray()).Trim();
        return clean.Length > 0 ? clean : "Welt";
    }
}
