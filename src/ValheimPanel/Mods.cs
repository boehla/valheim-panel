using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ValheimPanel;

/// <summary>What the panel records about one installed package, written next to its files.</summary>
public class ModMarker {
    public string FullName { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public string Icon { get; set; } = "";
    public string PackageUrl { get; set; } = "";
    public DateTime InstalledAt { get; set; }

    /// <summary>False for a package that only came along as somebody else's dependency.</summary>
    public bool Manual { get; set; } = true;

    /// <summary>Whether this package goes into the pack the players download.</summary>
    public bool Client { get; set; } = true;

    /// <summary>Thunderstore's own tags, kept so the card can show "Server-side" and friends.</summary>
    public List<string> Categories { get; set; } = new List<string>();

    /// <summary>Base names ("Owner-Name") this package needs, minus the loader.</summary>
    public List<string> Dependencies { get; set; } = new List<string>();

    /// <summary>Files written outside the package directory, relative to the server root.</summary>
    public List<string> ExtraFiles { get; set; } = new List<string>();
}

public class ModInfo : ModMarker {
    public bool Enabled { get; set; }
    public string LatestVersion { get; set; } = "";
    public bool UpdateAvailable { get; set; }
}

public class LoaderInfo {
    public bool Installed { get; set; }
    public bool Enabled { get; set; }
    public string Version { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public bool UpdateAvailable { get; set; }
}

public class ModsState {
    public LoaderInfo Loader { get; set; } = new LoaderInfo();
    public List<ModInfo> Mods { get; set; } = new List<ModInfo>();
    public CatalogInfo Catalog { get; set; } = new CatalogInfo();
    public bool ClientPackReady { get; set; }
    public DateTime? ClientPackBuiltAt { get; set; }
    public bool RestartRequired { get; set; }
}

/// <summary>
/// Installs Thunderstore packages into the server tree.
///
/// State is read back from the filesystem rather than from an index: one directory per
/// package under BepInEx/plugins, each holding a valheim-panel.json with the version and
/// the flags. Deleting the directory really does uninstall the mod, and nothing can drift
/// out of sync with what BepInEx actually loads. Disabling moves the directory to a
/// sibling that BepInEx does not scan.
///
/// BepInEx itself is not installed as a mod: it unpacks into the server root and is
/// switched on by a systemd drop-in that sets the Doorstop variables. The drop-in is
/// deliberately not part of valheim.service, because the install script rewrites that
/// file on every container update and would take the mod loader with it.
/// </summary>
public static partial class Mods {

    public const string PluginsDir = "/opt/valheim/server/BepInEx/plugins";
    public const string DisabledDir = "/opt/valheim/server/BepInEx/plugins-disabled";
    public const string ClientPackFile = "/opt/valheim/mods/valheim-client-mods.zip";

    const string patchersDir = "/opt/valheim/server/BepInEx/patchers";
    const string patchersDisabledDir = "/opt/valheim/server/BepInEx/patchers-disabled";
    const string loaderMarkerFile = "/opt/valheim/server/BepInEx/valheim-panel-loader.json";
    const string dropInFile = "/etc/systemd/system/valheim.service.d/bepinex.conf";
    internal const string MarkerName = "valheim-panel.json";

    /// <summary>Thunderstore allows only these characters in a namespace or package name.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_]+-[A-Za-z0-9_]+$")]
    private static partial Regex FullNameRegex();

    static readonly JsonSerializerOptions markerJson = new JsonSerializerOptions { WriteIndented = true };

    /// <summary>
    /// Everything the mods page shows. Update information needs the catalogue, but this is
    /// polled from the UI, so it only ever reads what is already cached — never the network.
    /// </summary>
    public static async Task<ModsState> StateAsync() {
        List<ModPackage> catalog = await Thunderstore.CachedCatalogAsync();

        // Thunderstore keeps the author's capitalisation but treats names case-insensitively,
        // so the catalogue really does contain pairs like eideehi-Better_Portal and
        // eideehi-better_portal. ToDictionary would throw on those; last one wins instead.
        Dictionary<string, ModPackage> byName = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        foreach(ModPackage package in catalog) byName[package.FullName] = package;

        ModsState state = new ModsState { Catalog = Thunderstore.Info() };

        ModMarker? loader = ReadMarker(loaderMarkerFile);
        state.Loader.Installed = loader != null && Directory.Exists("/opt/valheim/server/BepInEx/core");
        state.Loader.Version = loader?.Version ?? "";
        state.Loader.Enabled = File.Exists(dropInFile);
        if(byName.TryGetValue(Thunderstore.LoaderPackage, out ModPackage? loaderLatest)) {
            state.Loader.LatestVersion = loaderLatest.Version;
            state.Loader.UpdateAvailable = state.Loader.Installed && loaderLatest.Version != state.Loader.Version;
        }

        foreach((string dir, bool enabled) in PackageDirs()) {
            ModMarker? marker = ReadMarker(Path.Combine(dir, MarkerName));
            if(marker == null) continue;

            ModInfo info = new ModInfo {
                FullName = marker.FullName,
                Owner = marker.Owner,
                Name = marker.Name,
                Version = marker.Version,
                Description = marker.Description,
                Icon = marker.Icon,
                PackageUrl = marker.PackageUrl,
                InstalledAt = marker.InstalledAt,
                Manual = marker.Manual,
                Client = marker.Client,
                Categories = marker.Categories,
                Dependencies = marker.Dependencies,
                Enabled = enabled
            };

            if(byName.TryGetValue(marker.FullName, out ModPackage? latest)) {
                info.LatestVersion = latest.Version;
                info.UpdateAvailable = latest.Version != marker.Version;
            }
            state.Mods.Add(info);
        }

        state.Mods = state.Mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

        if(File.Exists(ClientPackFile)) {
            state.ClientPackReady = true;
            state.ClientPackBuiltAt = File.GetLastWriteTime(ClientPackFile);
        }

        // Mods are read at startup, so anything installed since then is not live yet.
        state.RestartRequired = await restartRequiredAsync(state);
        return state;
    }

    /// <summary>
    /// Installs a package and everything it depends on. BepInEx is pulled in first when it
    /// is missing — without the loader a plugin directory is just files nobody reads.
    /// </summary>
    public static async Task InstallAsync(Action<string> log, string fullName, bool manual = true) {
        requireValidName(fullName);
        await Thunderstore.CatalogAsync(log);

        if(!File.Exists(loaderMarkerFile)) {
            log("BepInEx fehlt — der Mod-Loader wird zuerst installiert.");
            await InstallLoaderAsync(log);
        }

        List<string> queue = await resolveAsync(log, fullName);

        foreach(string name in queue) {
            ModPackage? package = await Thunderstore.FindAsync(name);
            if(package == null) {
                log($"Nicht im Katalog: {name} — übersprungen.");
                continue;
            }

            ModMarker? installed = ReadMarker(Path.Combine(packageDir(name), MarkerName));
            if(installed != null && installed.Version == package.Version) {
                log($"{package.Name} {package.Version} ist bereits installiert.");
                // A dependency that the user now asks for by name becomes a manual pick,
                // so a later cleanup does not treat it as disposable.
                if(manual && name.Equals(fullName, StringComparison.OrdinalIgnoreCase) && !installed.Manual) {
                    installed.Manual = true;
                    writeMarker(Path.Combine(packageDir(name), MarkerName), installed);
                }
                continue;
            }

            bool isRequested = name.Equals(fullName, StringComparison.OrdinalIgnoreCase);
            await installOneAsync(log, package, manual && isRequested, installed);
        }

        log("Fertig. Die Änderung wird beim nächsten Neustart des Servers geladen.");
    }

    /// <summary>Unpacks the community BepInEx build into the server root.</summary>
    public static async Task InstallLoaderAsync(Action<string> log) {
        await Thunderstore.CatalogAsync(log);
        ModPackage package = await Thunderstore.FindAsync(Thunderstore.LoaderPackage)
            ?? throw new Exception("BepInEx ist im Thunderstore-Katalog nicht auffindbar.");

        string zipPath = await Thunderstore.DownloadAsync(package, log);

        using(ZipArchive archive = ZipFile.OpenRead(zipPath)) {
            string prefix = loaderPrefix(archive);
            int written = 0;

            foreach(ZipArchiveEntry entry in archive.Entries) {
                if(entry.FullName.Length == 0 || entry.FullName.EndsWith('/')) continue;
                string name = normalize(entry.FullName);
                if(!name.StartsWith(prefix, StringComparison.Ordinal)) continue;

                string relative = name[prefix.Length..];
                if(relative.Length == 0) continue;

                string target = Path.Combine(ServerControl.ServerDir, relative);

                // BepInEx.cfg carries whatever the admin tuned; an update must not reset it.
                if(relative.StartsWith("BepInEx/config/", StringComparison.Ordinal) && File.Exists(target)) continue;

                extract(entry, target);
                written++;
            }
            log($"BepInEx {package.Version} entpackt ({written} Dateien).");
        }

        Directory.CreateDirectory(PluginsDir);
        writeMarker(loaderMarkerFile, new ModMarker {
            FullName = package.FullName,
            Owner = package.Owner,
            Name = package.Name,
            Version = package.Version,
            Description = package.Description,
            PackageUrl = package.PackageUrl,
            InstalledAt = DateTime.Now
        });

        await SetLoaderEnabledAsync(log, true);
    }

    /// <summary>
    /// Writes or removes the systemd drop-in that preloads Doorstop. This is the switch
    /// between a vanilla and a modded server; the installed files stay either way.
    /// </summary>
    public static async Task SetLoaderEnabledAsync(Action<string> log, bool enabled) {
        if(enabled && !File.Exists(loaderMarkerFile)) throw new Exception("BepInEx ist nicht installiert.");

        if(enabled) {
            // Same variables as the pack's own start_server_bepinex.sh, but absolute:
            // systemd does not run the unit through that script.
            string content = $"""
                # Written by valheim-panel. Removing this file starts Valheim without mods.
                [Service]
                Environment=DOORSTOP_ENABLED=1
                Environment=DOORSTOP_TARGET_ASSEMBLY={ServerControl.ServerDir}/BepInEx/core/BepInEx.Preloader.dll
                Environment=LD_LIBRARY_PATH={ServerControl.ServerDir}/doorstop_libs:{ServerControl.ServerDir}/linux64
                Environment=LD_PRELOAD=libdoorstop_x64.so

                """;
            Directory.CreateDirectory(Path.GetDirectoryName(dropInFile)!);
            await File.WriteAllTextAsync(dropInFile, content);
            log("BepInEx aktiviert. Der Server lädt Mods ab dem nächsten Start.");
        } else if(File.Exists(dropInFile)) {
            File.Delete(dropInFile);
            log("BepInEx deaktiviert. Der Server startet ab dem nächsten Start ohne Mods.");
        }

        await Shell.SystemctlAsync("daemon-reload");
    }

    /// <summary>Removes a package's files. Config files are left behind on purpose.</summary>
    public static void Uninstall(string fullName) {
        requireValidName(fullName);

        string dir = packageDir(fullName);
        string disabled = Path.Combine(DisabledDir, fullName);
        string present = Directory.Exists(dir) ? dir : disabled;
        if(!Directory.Exists(present)) throw new Exception("Dieser Mod ist nicht installiert.");

        ModMarker? marker = ReadMarker(Path.Combine(present, MarkerName));
        foreach(string relative in marker?.ExtraFiles ?? new List<string>()) {
            string path = Path.Combine(ServerControl.ServerDir, relative);
            try { if(File.Exists(path)) File.Delete(path); } catch { }
        }

        Directory.Delete(present, true);
        pruneEmptyDirs(patchersDir);
        pruneEmptyDirs(patchersDisabledDir);
    }

    /// <summary>Names of installed mods that list this one as a dependency.</summary>
    public static List<string> DependentsOf(string fullName) {
        List<string> dependents = new List<string>();
        foreach((string dir, _) in PackageDirs()) {
            ModMarker? marker = ReadMarker(Path.Combine(dir, MarkerName));
            if(marker == null || marker.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase)) continue;
            if(marker.Dependencies.Any(d => d.Equals(fullName, StringComparison.OrdinalIgnoreCase))) dependents.Add(marker.Name);
        }
        return dependents;
    }

    /// <summary>Moves a package between the directory BepInEx scans and the one it does not.</summary>
    public static void SetEnabled(string fullName, bool enabled) {
        requireValidName(fullName);

        string active = packageDir(fullName);
        string parked = Path.Combine(DisabledDir, fullName);
        string from = enabled ? parked : active;
        string to = enabled ? active : parked;

        if(!Directory.Exists(from)) {
            if(Directory.Exists(to)) return;
            throw new Exception("Dieser Mod ist nicht installiert.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        Directory.Move(from, to);

        // A preloader patcher runs before the plugin does, so it has to move along or
        // the mod is only half switched off.
        string patchersFrom = Path.Combine(enabled ? patchersDisabledDir : patchersDir, fullName);
        if(Directory.Exists(patchersFrom)) {
            string patchersTo = Path.Combine(enabled ? patchersDir : patchersDisabledDir, fullName);
            Directory.CreateDirectory(Path.GetDirectoryName(patchersTo)!);
            Directory.Move(patchersFrom, patchersTo);
        }
    }

    public static void SetClient(string fullName, bool client) {
        requireValidName(fullName);

        string dir = Directory.Exists(packageDir(fullName)) ? packageDir(fullName) : Path.Combine(DisabledDir, fullName);
        string path = Path.Combine(dir, MarkerName);
        ModMarker marker = ReadMarker(path) ?? throw new Exception("Dieser Mod ist nicht installiert.");

        marker.Client = client;
        writeMarker(path, marker);
    }

    /// <summary>Reinstalls every package whose catalogue version has moved on.</summary>
    public static async Task UpdateAllAsync(Action<string> log) {
        await Thunderstore.CatalogAsync(log, force: true);
        ModsState state = await StateAsync();
        int updated = 0;

        if(state.Loader.Installed && state.Loader.UpdateAvailable) {
            log($"BepInEx {state.Loader.Version} → {state.Loader.LatestVersion}");
            await InstallLoaderAsync(log);
            updated++;
        }

        foreach(ModInfo mod in state.Mods.Where(m => m.UpdateAvailable)) {
            ModPackage? package = await Thunderstore.FindAsync(mod.FullName);
            if(package == null) continue;

            log($"{mod.Name} {mod.Version} → {package.Version}");
            ModMarker? current = ReadMarker(Path.Combine(installedDir(mod.FullName), MarkerName));
            await installOneAsync(log, package, mod.Manual, current);
            updated++;
        }

        log(updated == 0 ? "Alles ist auf dem neuesten Stand." : $"{updated} Paket(e) aktualisiert. Server neu starten, damit es greift.");
        pruneCache();
    }

    /// <summary>
    /// Builds the zip the players unpack into their own Valheim folder: the same BepInEx
    /// build the server runs, plus every enabled mod that is marked as client-relevant.
    /// Valheim refuses a connection when the mod sets do not line up, so this has to come
    /// from the installed state and not from a list somebody keeps by hand.
    /// </summary>
    public static async Task BuildClientPackAsync(Action<string> log) {
        ModMarker loader = ReadMarker(loaderMarkerFile) ?? throw new Exception("BepInEx ist nicht installiert.");
        string loaderZip = Path.Combine(Thunderstore.CacheDir, $"{loader.FullName}-{loader.Version}.zip");

        if(!File.Exists(loaderZip)) {
            ModPackage? package = await Thunderstore.FindAsync(Thunderstore.LoaderPackage)
                ?? throw new Exception("BepInEx ist im Katalog nicht auffindbar und liegt nicht im Cache.");
            loaderZip = await Thunderstore.DownloadAsync(package, log);
        }

        List<ModMarker> included = new List<ModMarker>();
        foreach((string dir, bool enabled) in PackageDirs()) {
            ModMarker? marker = ReadMarker(Path.Combine(dir, MarkerName));
            if(marker != null && enabled && marker.Client) included.Add(marker);
        }

        // A mod in the pack whose library is not is a mod that throws on the player's
        // first load. Cheap to notice here, expensive to work out from a Unity crash log.
        HashSet<string> shipping = new HashSet<string>(included.Select(m => m.FullName), StringComparer.OrdinalIgnoreCase);
        foreach(ModMarker mod in included) {
            foreach(string dependency in mod.Dependencies.Where(d => !shipping.Contains(d))) {
                log($"ACHTUNG: {mod.Name} braucht {dependency}, das nicht im Paket ist — für Clients markieren oder {mod.Name} abwählen.");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ClientPackFile)!);
        string staging = ClientPackFile + ".part";
        if(File.Exists(staging)) File.Delete(staging);

        using(FileStream file = File.Create(staging))
        using(ZipArchive pack = new ZipArchive(file, ZipArchiveMode.Create)) {
            HashSet<string> written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            log($"BepInEx {loader.Version} ...");
            using(ZipArchive source = ZipFile.OpenRead(loaderZip)) {
                string prefix = loaderPrefix(source);
                foreach(ZipArchiveEntry entry in source.Entries) {
                    if(entry.FullName.EndsWith('/')) continue;
                    string name = normalize(entry.FullName);
                    if(!name.StartsWith(prefix, StringComparison.Ordinal)) continue;

                    string relative = name[prefix.Length..];
                    // The server launcher would only confuse a player unpacking this.
                    if(relative == "start_server_bepinex.sh") continue;
                    copyInto(pack, entry, relative, written);
                }
            }

            foreach(ModMarker marker in included) {
                string zipPath = Path.Combine(Thunderstore.CacheDir, $"{marker.FullName}-{marker.Version}.zip");
                if(!File.Exists(zipPath)) {
                    ModPackage? package = await Thunderstore.FindAsync(marker.FullName);
                    if(package == null || package.Version != marker.Version) {
                        log($"ACHTUNG: {marker.Name} {marker.Version} liegt nicht im Cache und ist nicht mehr im Katalog — fehlt im Client-Paket.");
                        continue;
                    }
                    zipPath = await Thunderstore.DownloadAsync(package, log);
                }

                log($"{marker.Name} {marker.Version} ...");
                using(ZipArchive source = ZipFile.OpenRead(zipPath)) {
                    foreach(ZipArchiveEntry entry in source.Entries) {
                        if(entry.FullName.EndsWith('/')) continue;
                        string? relative = mapEntry(normalize(entry.FullName), marker.FullName);
                        if(relative == null) continue;
                        copyInto(pack, entry, relative, written);
                    }
                }
            }

            ZipArchiveEntry readme = pack.CreateEntry("LIESMICH.txt");
            using(Stream stream = readme.Open()) {
                byte[] bytes = Encoding.UTF8.GetBytes(clientReadme(loader, included));
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        File.Move(staging, ClientPackFile, true);
        long size = new FileInfo(ClientPackFile).Length;
        log($"Client-Paket fertig: {included.Count} Mod(s), {size / 1048576.0:F1} MB.");
    }

    /* --- installation --------------------------------------------------- */

    static async Task installOneAsync(Action<string> log, ModPackage package, bool manual, ModMarker? existing) {
        string zipPath = await Thunderstore.DownloadAsync(package, log);

        // Whether it was switched off stays true across an update; so does the client flag.
        bool wasDisabled = Directory.Exists(Path.Combine(DisabledDir, package.FullName));
        ModMarker marker = new ModMarker {
            FullName = package.FullName,
            Owner = package.Owner,
            Name = package.Name,
            Version = package.Version,
            Description = package.Description,
            Icon = package.Icon,
            PackageUrl = package.PackageUrl,
            InstalledAt = DateTime.Now,
            Manual = existing?.Manual ?? manual,
            Client = existing?.Client ?? defaultClient(package),
            Categories = package.Categories,
            Dependencies = package.Dependencies.Select(baseName).Where(d => d != Thunderstore.LoaderPackage).ToList()
        };

        // The old version goes first: a rename inside a package would otherwise leave a
        // stale DLL next to the new one and BepInEx would load both.
        if(existing != null) {
            try { Uninstall(package.FullName); } catch { }
        }

        string target = wasDisabled ? Path.Combine(DisabledDir, package.FullName) : packageDir(package.FullName);
        Directory.CreateDirectory(target);

        int files = 0;
        using(ZipArchive archive = ZipFile.OpenRead(zipPath)) {
            foreach(ZipArchiveEntry entry in archive.Entries) {
                if(entry.FullName.Length == 0 || entry.FullName.EndsWith('/')) continue;

                string? relative = mapEntry(normalize(entry.FullName), package.FullName);
                if(relative == null) continue;

                // A package that is parked stays parked, patchers included.
                string mapped = wasDisabled ? relative
                    .Replace("BepInEx/plugins/", "BepInEx/plugins-disabled/", StringComparison.Ordinal)
                    .Replace("BepInEx/patchers/", "BepInEx/patchers-disabled/", StringComparison.Ordinal)
                    : relative;

                string path = Path.Combine(ServerControl.ServerDir, mapped);

                // Never overwrite a config the admin has already edited.
                bool isConfig = mapped.StartsWith("BepInEx/config/", StringComparison.Ordinal);
                if(isConfig && File.Exists(path)) continue;

                extract(entry, path);
                files++;

                // Anything outside the package directory has to be remembered, or an
                // uninstall would leave it behind. Configs are left behind on purpose.
                if(!isConfig && !mapped.Contains($"/{package.FullName}/", StringComparison.Ordinal)) marker.ExtraFiles.Add(mapped);
            }
        }

        writeMarker(Path.Combine(target, MarkerName), marker);
        log($"{package.Name} {package.Version} installiert ({files} Dateien){(wasDisabled ? ", bleibt deaktiviert" : "")}.");
    }

    /// <summary>
    /// Whether a package belongs in the client pack by default. Thunderstore tags tell us
    /// this often enough to be worth using: a mod tagged server-side and not client-side
    /// changes nothing on a player's machine, and shipping it would only be noise. Anything
    /// untagged is assumed to be needed on both sides, which is the safe way round — a
    /// missing mod on the client is a refused connection, a superfluous one is harmless.
    /// </summary>
    static bool defaultClient(ModPackage package) {
        bool server = package.Categories.Contains("Server-side", StringComparer.OrdinalIgnoreCase);
        bool client = package.Categories.Contains("Client-side", StringComparer.OrdinalIgnoreCase);
        return !server || client;
    }

    /// <summary>
    /// Walks the dependency graph depth first, so a package is always installed after the
    /// things it needs. The loader is not part of this — it is handled on its own.
    /// </summary>
    static async Task<List<string>> resolveAsync(Action<string> log, string fullName) {
        List<string> ordered = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task walk(string name, int depth) {
            if(depth > 10 || !seen.Add(name)) return;

            ModPackage? package = await Thunderstore.FindAsync(name);
            if(package == null) {
                log($"Abhängigkeit nicht im Katalog: {name}");
                return;
            }

            foreach(string raw in package.Dependencies) {
                string dependency = baseName(raw);
                if(dependency == Thunderstore.LoaderPackage) continue;
                await walk(dependency, depth + 1);
            }
            ordered.Add(name);
        }

        await walk(fullName, 0);

        List<string> extra = ordered.Where(n => !n.Equals(fullName, StringComparison.OrdinalIgnoreCase)).ToList();
        if(extra.Count > 0) log($"Benötigt zusätzlich: {string.Join(", ", extra)}");
        return ordered;
    }

    /// <summary>
    /// Maps a zip entry onto a path under the server root. Thunderstore packages come in
    /// three shapes — plugins/, BepInEx/plugins/, or the DLL loose at the root — and the
    /// point of this is that all three land in one directory per package, so a package
    /// can be removed again by deleting that directory.
    /// </summary>
    static string? mapEntry(string entry, string fullName) {
        string rest = entry;
        if(rest.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase)) rest = rest["BepInEx/".Length..];

        if(rest.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)) return $"BepInEx/plugins/{fullName}/{rest["plugins/".Length..]}";
        if(rest.StartsWith("patchers/", StringComparison.OrdinalIgnoreCase)) return $"BepInEx/patchers/{fullName}/{rest["patchers/".Length..]}";
        if(rest.StartsWith("monomod/", StringComparison.OrdinalIgnoreCase)) return $"BepInEx/monomod/{fullName}/{rest["monomod/".Length..]}";
        if(rest.StartsWith("config/", StringComparison.OrdinalIgnoreCase)) return $"BepInEx/config/{rest["config/".Length..]}";
        if(rest.StartsWith("core/", StringComparison.OrdinalIgnoreCase)) return $"BepInEx/core/{rest["core/".Length..]}";

        return $"BepInEx/plugins/{fullName}/{rest}";
    }

    /// <summary>The BepInEx pack nests everything under one folder; this finds its name.</summary>
    static string loaderPrefix(ZipArchive archive) {
        const string anchor = "BepInEx/core/BepInEx.Preloader.dll";
        foreach(ZipArchiveEntry entry in archive.Entries) {
            string name = normalize(entry.FullName);
            if(name.EndsWith(anchor, StringComparison.OrdinalIgnoreCase)) return name[..^anchor.Length];
        }
        throw new Exception("Das BepInEx-Paket enthält keinen Preloader — Download vermutlich kaputt.");
    }

    static void extract(ZipArchiveEntry entry, string targetPath) {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        entry.ExtractToFile(targetPath, true);
    }

    static void copyInto(ZipArchive pack, ZipArchiveEntry entry, string relative, HashSet<string> written) {
        // Two mods can ship the same shared library; the first one wins, as on disk.
        if(!written.Add(relative)) return;

        ZipArchiveEntry target = pack.CreateEntry(relative, CompressionLevel.Optimal);
        // Thunderstore zips carry no execute bit, so start_game_bepinex.sh unpacked on Linux as a
        // plain file that Steam cannot launch. Mode 0755 in the upper half of the external
        // attributes is what unzip and the desktop archive tools restore.
        if(relative.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)) {
            target.ExternalAttributes = Convert.ToInt32("100755", 8) << 16;
        }
        using(Stream from = entry.Open())
        using(Stream to = target.Open()) {
            from.CopyTo(to);
        }
    }

    static string clientReadme(ModMarker loader, List<ModMarker> mods) {
        StringBuilder text = new StringBuilder();
        text.AppendLine("Valheim — Mods für diesen Server");
        text.AppendLine("=================================");
        text.AppendLine();
        text.AppendLine("So installierst du sie:");
        text.AppendLine();
        text.AppendLine("1. Steam -> Rechtsklick auf Valheim -> Verwalten -> Lokale Dateien durchsuchen.");
        text.AppendLine("2. Den gesamten Inhalt dieses Archivs in diesen Ordner entpacken.");
        text.AppendLine("3. Je nach System wie unten beschrieben weiter.");
        text.AppendLine();
        text.AppendLine("Windows");
        text.AppendLine("-------");
        text.AppendLine("Nichts weiter zu tun: Valheim ganz normal über Steam starten. Beim Start öffnet");
        text.AppendLine("sich ein Konsolenfenster, und der erste Start dauert etwas länger.");
        text.AppendLine();
        text.AppendLine("Linux");
        text.AppendLine("-----");
        text.AppendLine("Die winhttp.dll wirkt unter Linux nicht, BepInEx braucht dort eine Startoption.");
        text.AppendLine("Welche, hängt davon ab, was im Spielordner liegt:");
        text.AppendLine();
        text.AppendLine("valheim.x86_64 (Valheim läuft nativ, der Normalfall):");
        text.AppendLine("  Steam -> Rechtsklick auf Valheim -> Eigenschaften -> Startoptionen:");
        text.AppendLine("      ./start_game_bepinex.sh %command%");
        text.AppendLine("  Ein Konsolenfenster gibt es dabei nicht, das ist normal. Startet das Spiel");
        text.AppendLine("  mit dieser Startoption gar nicht, hat das Entpackprogramm das");
        text.AppendLine("  Ausführungsrecht nicht übernommen. Dann im Spielordner einmal ausführen:");
        text.AppendLine("      chmod u+x start_game_bepinex.sh");
        text.AppendLine();
        text.AppendLine("valheim.exe (Valheim läuft über Proton):");
        text.AppendLine("  Startoptionen:");
        text.AppendLine("      WINEDLLOVERRIDES=\"winhttp=n,b\" %command%");
        text.AppendLine();
        text.AppendLine("Alle Mitspieler brauchen exakt diese Dateien, sonst lässt der Server nicht");
        text.AppendLine("verbinden.");
        text.AppendLine();
        text.AppendLine("Zum Entfernen die Ordner BepInEx und doorstop_libs sowie die Dateien winhttp.dll,");
        text.AppendLine("doorstop_config.ini und start_game_bepinex.sh löschen. Unter Linux zusätzlich die");
        text.AppendLine("Startoption in Steam wieder leeren.");
        text.AppendLine();
        text.AppendLine($"BepInEx {loader.Version}");
        foreach(ModMarker mod in mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)) {
            text.AppendLine($"  - {mod.Name} {mod.Version} ({mod.Owner})");
        }
        text.AppendLine();
        text.AppendLine($"Erstellt am {DateTime.Now:dd.MM.yyyy HH:mm} vom Valheim-Panel.");
        return text.ToString();
    }

    /* --- state on disk -------------------------------------------------- */

    internal static IEnumerable<(string Dir, bool Enabled)> PackageDirs() {
        foreach((string root, bool enabled) in new[] { (PluginsDir, true), (DisabledDir, false) }) {
            if(!Directory.Exists(root)) continue;
            foreach(string dir in Directory.GetDirectories(root)) {
                if(File.Exists(Path.Combine(dir, MarkerName))) yield return (dir, enabled);
            }
        }
    }

    static string packageDir(string fullName) => Path.Combine(PluginsDir, fullName);

    static string installedDir(string fullName) {
        string active = packageDir(fullName);
        return Directory.Exists(active) ? active : Path.Combine(DisabledDir, fullName);
    }

    internal static ModMarker? ReadMarker(string path) {
        if(!File.Exists(path)) return null;
        try {
            return JsonSerializer.Deserialize<ModMarker>(File.ReadAllText(path));
        } catch {
            return null;
        }
    }

    static void writeMarker(string path, ModMarker marker) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(marker, markerJson));
    }

    /// <summary>
    /// True when what is on disk is not what the running server loaded. Derived from file
    /// times against the unit's start timestamp, so it survives a panel restart.
    /// </summary>
    static async Task<bool> restartRequiredAsync(ModsState state) {
        string startedRaw = await Shell.ServicePropertyAsync(ServerControl.Unit, "ExecMainStartTimestamp");
        if(!DateTime.TryParse(startedRaw, out DateTime started)) return false;
        if((await Shell.ServicePropertyAsync(ServerControl.Unit, "ActiveState")) != "active") return false;

        if(File.Exists(dropInFile) != state.Loader.Enabled) return true;
        if(File.Exists(dropInFile) && File.GetLastWriteTime(dropInFile) > started) return true;

        foreach((string dir, _) in PackageDirs()) {
            if(Directory.GetLastWriteTime(dir) > started) return true;
        }

        // A directory timestamp does not move when a file inside it is edited, so an edited
        // config needs its own look — and a mod reads its settings once, when it loads.
        return ModConfig.AnyChangedSince(started);
    }

    static void pruneCache() {
        List<string> keep = new List<string>();
        ModMarker? loader = ReadMarker(loaderMarkerFile);
        if(loader != null) keep.Add($"{loader.FullName}-{loader.Version}.zip");

        foreach((string dir, _) in PackageDirs()) {
            ModMarker? marker = ReadMarker(Path.Combine(dir, MarkerName));
            if(marker != null) keep.Add($"{marker.FullName}-{marker.Version}.zip");
        }
        Thunderstore.PruneCache(keep);
    }

    static void pruneEmptyDirs(string root) {
        if(!Directory.Exists(root)) return;
        foreach(string dir in Directory.GetDirectories(root)) {
            try {
                if(Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
            } catch { }
        }
    }

    /// <summary>"Owner-Name-1.2.3" as Thunderstore writes dependencies, reduced to "Owner-Name".</summary>
    static string baseName(string dependency) {
        int last = dependency.LastIndexOf('-');
        return last > 0 ? dependency[..last] : dependency;
    }

    /// <summary>
    /// Package names reach the filesystem, so a name that is not exactly what Thunderstore
    /// allows never gets that far.
    /// </summary>
    static void requireValidName(string fullName) {
        if(!FullNameRegex().IsMatch(fullName)) throw new Exception($"Ungültiger Paketname: {fullName}");
    }

    /// <summary>
    /// Zip entries are attacker-controlled once a package is untrusted, so anything that
    /// could climb out of the target directory is rejected rather than sanitised.
    /// </summary>
    static string normalize(string entryName) {
        string name = entryName.Replace('\\', '/').TrimStart('/');
        while(name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];

        if(name.Length == 0 || name.Split('/').Any(part => part == "..")) {
            throw new Exception($"Verdächtiger Pfad im Archiv: {entryName}");
        }
        return name;
    }
}
