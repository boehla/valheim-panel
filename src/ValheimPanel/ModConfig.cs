using System.Text;

namespace ValheimPanel;

/// <summary>One editable file, as the picker in the UI lists it.</summary>
public class ConfigFileInfo {
    /// <summary>Relative to the server root, with forward slashes — the handle every endpoint takes.</summary>
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>"BepInEx" for the shared config folder, otherwise the mod the file belongs to.</summary>
    public string Group { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime ModifiedAt { get; set; }

    /// <summary>False for a file inside a package that is currently switched off.</summary>
    public bool Enabled { get; set; } = true;
}

public class ConfigContent {
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime ModifiedAt { get; set; }

    /// <summary>Last write time in ticks — handed back on save so a foreign change is noticed.</summary>
    public long Stamp { get; set; }
    public bool HasBackup { get; set; }
}

/// <summary>Raised when the file moved on while the browser was holding an older copy.</summary>
public class StaleConfigException : Exception {
    public StaleConfigException(string message) : base(message) { }
}

/// <summary>
/// A plain text editor for the configuration of installed mods.
///
/// Deliberately raw: whatever a mod writes, the admin gets to edit, and nothing here knows what
/// a single setting means. That is what makes it work for every mod instead of only for the two
/// or three somebody bothered to model — ValheimPlus alone has hundreds of keys across a dozen
/// sections, and they move between releases.
///
/// Two BepInEx habits shape the rest. A plugin creates its config file the first time it loads,
/// so the file an admin is looking for may simply not exist yet; and a plugin writes that file
/// back when it shuts down, so a save into a running server can be undone by the server itself.
/// Hence the timestamp guard and the .bak copy.
/// </summary>
public static class ModConfig {

    public const string ConfigDir = "/opt/valheim/server/BepInEx/config";

    /// <summary>Nothing outside these is reachable, whatever a request asks for.</summary>
    static readonly string[] roots = [
        "BepInEx/config/",
        "BepInEx/plugins/",
        "BepInEx/plugins-disabled/"
    ];

    /// <summary>
    /// What mods actually keep their settings in. An allowlist and not a blocklist: a package
    /// directory is mostly DLLs, and offering those in a text editor helps nobody. It also keeps
    /// the .bak and .part files this class writes out of the listing.
    /// </summary>
    static readonly string[] extensions = [".cfg", ".json", ".yml", ".yaml", ".ini", ".txt", ".xml"];

    const long maxRead = 512 * 1024;
    const long maxWrite = 1024 * 1024;

    /// <summary>Everything the picker offers, the shared BepInEx config folder first.</summary>
    public static List<ConfigFileInfo> List() {
        List<ConfigFileInfo> files = new List<ConfigFileInfo>();

        foreach(FileInfo file in filesUnder(ConfigDir)) {
            files.Add(describe(file, "BepInEx", true));
        }

        foreach((string dir, bool enabled) in Mods.PackageDirs()) {
            ModMarker? marker = Mods.ReadMarker(Path.Combine(dir, Mods.MarkerName));
            string group = marker?.Name is { Length: > 0 } name ? name : Path.GetFileName(dir);

            foreach(FileInfo file in filesUnder(dir)) {
                files.Add(describe(file, group, enabled));
            }
        }

        return files;
    }

    public static ConfigContent Read(string relativePath) {
        string full = resolve(relativePath);
        if(!File.Exists(full)) throw new Exception("Diese Datei gibt es (noch) nicht — viele Mods legen sie erst beim ersten Start an.");

        long size = new FileInfo(full).Length;
        if(size > maxRead) throw new Exception($"Die Datei ist {size / 1024} KB groß und damit zu groß für den Editor.");

        byte[] bytes = File.ReadAllBytes(full);
        // A DLL that slipped past the extension check would be mangled silently on save.
        if(Array.IndexOf(bytes, (byte)0) >= 0) throw new Exception("Das sieht nach einer Binärdatei aus und wird nicht im Editor geöffnet.");

        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;

        return new ConfigContent {
            Path = relative(full),
            Name = Path.GetFileName(full),
            Text = Encoding.UTF8.GetString(bytes, start, bytes.Length - start),
            ModifiedAt = File.GetLastWriteTime(full),
            Stamp = stampOf(full),
            HasBackup = File.Exists(full + ".bak")
        };
    }

    /// <summary>
    /// Replaces the file and keeps the previous version next to it. The stamp is the copy the
    /// browser started from: when it no longer matches, somebody — most likely BepInEx on the
    /// last shutdown — has written the file in the meantime, and saving would throw that away.
    /// </summary>
    public static ConfigContent Write(string relativePath, string text, long stamp) {
        string full = resolve(relativePath);
        if(!File.Exists(full)) throw new Exception("Diese Datei gibt es nicht mehr.");

        if(stampOf(full) != stamp) {
            throw new StaleConfigException("Die Datei wurde inzwischen geändert — vermutlich vom Server selbst, "
                + "der seine Konfiguration beim Beenden zurückschreibt. Es wurde nichts überschrieben.");
        }

        // The server runs on Linux and BepInEx parses line by line; CRLF from the browser would
        // end up as part of every value.
        string content = text.Replace("\r\n", "\n").Replace("\r", "\n");
        if(!content.EndsWith('\n')) content += "\n";

        byte[] bytes = new UTF8Encoding(false).GetBytes(content);
        if(bytes.Length > maxWrite) throw new Exception($"Der Inhalt ist {bytes.Length / 1024} KB groß und damit zu groß zum Speichern.");

        File.Copy(full, full + ".bak", true);

        // Same staging dance as the backups and the client pack: a half-written config is a
        // mod that refuses to load.
        string staging = full + ".part";
        File.WriteAllBytes(staging, bytes);
        File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(staging, full, true);

        return Read(relative(full));
    }

    /// <summary>Deletes the file, so the mod writes a fresh one with its defaults on the next start.</summary>
    public static void Reset(string relativePath) {
        string full = resolve(relativePath);
        if(!File.Exists(full)) throw new Exception("Diese Datei gibt es nicht.");

        File.Copy(full, full + ".bak", true);
        File.Delete(full);
    }

    /// <summary>
    /// Whether a config has been edited since the given moment. Feeds the restart hint on the
    /// mods page — a mod reads its configuration once, when it loads.
    /// </summary>
    internal static bool AnyChangedSince(DateTime moment) {
        // This hangs off /api/mods, which the whole mods page depends on. A walk of the mod
        // tree races with an install that is rewriting it, and a restart hint is never worth
        // taking that page down for.
        try {
            return List().Any(file => file.ModifiedAt > moment);
        } catch {
            return false;
        }
    }

    /* --- paths ------------------------------------------------------------ */

    /// <summary>
    /// Turns a path from a request into an absolute one, or refuses. Same stance as the zip
    /// extraction in <see cref="Mods"/>: anything that is not plainly inside one of the mod
    /// directories is rejected rather than tidied up, because a sanitiser that gets it wrong
    /// hands out the whole filesystem.
    /// </summary>
    static string resolve(string? relativePath) {
        string path = (relativePath ?? "").Trim();
        if(path.Length == 0) throw new Exception("Kein Pfad angegeben.");

        if(path.Contains('\\') || path.StartsWith('/') || path.Split('/').Any(part => part.Length == 0 || part == "." || part == "..")) {
            throw new Exception($"Ungültiger Pfad: {path}");
        }
        if(!roots.Any(root => path.StartsWith(root, StringComparison.Ordinal))) {
            throw new Exception($"Außerhalb der Mod-Verzeichnisse: {path}");
        }
        if(!editable(path)) throw new Exception($"Das ist keine Konfigurationsdatei: {path}");

        string full = Path.GetFullPath(Path.Combine(ServerControl.ServerDir, path));
        string root = Path.GetFullPath(ServerControl.ServerDir) + Path.DirectorySeparatorChar;
        if(!full.StartsWith(root, StringComparison.Ordinal)) throw new Exception($"Ungültiger Pfad: {path}");

        return full;
    }

    static bool editable(string path) {
        // The panel's own bookkeeping sits in every package directory and is not the admin's to edit.
        if(Path.GetFileName(path).Equals(Mods.MarkerName, StringComparison.OrdinalIgnoreCase)) return false;
        return extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every editable file below a directory. An install rewrites this tree while it runs, so
    /// a file can be gone between being listed and being looked at; that is a file to skip,
    /// not a listing to fail.
    /// </summary>
    static IEnumerable<FileInfo> filesUnder(string dir) {
        string[] paths;
        try {
            paths = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        } catch {
            yield break;
        }

        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);

        foreach(string path in paths) {
            if(!editable(path)) continue;

            FileInfo file;
            try {
                file = new FileInfo(path);
                if(file.Length > maxRead) continue;
            } catch {
                continue;
            }

            yield return file;
        }
    }

    static ConfigFileInfo describe(FileInfo file, string group, bool enabled) {
        return new ConfigFileInfo {
            Path = relative(file.FullName),
            Name = file.Name,
            Group = group,
            SizeBytes = file.Length,
            ModifiedAt = file.LastWriteTime,
            Enabled = enabled
        };
    }

    static string relative(string full) {
        return Path.GetRelativePath(ServerControl.ServerDir, full).Replace('\\', '/');
    }

    static long stampOf(string full) {
        return File.GetLastWriteTimeUtc(full).Ticks;
    }
}
