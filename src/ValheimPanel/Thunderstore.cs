using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ValheimPanel;

/// <summary>One Thunderstore package, reduced to what the panel needs to show and install it.</summary>
public class ModPackage {
    public string FullName { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Icon { get; set; } = "";
    public string PackageUrl { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public long Downloads { get; set; }
    public long FileSize { get; set; }
    public bool Deprecated { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<string> Categories { get; set; } = new List<string>();

    /// <summary>Raw "Owner-Name-Version" strings, exactly as Thunderstore states them.</summary>
    public List<string> Dependencies { get; set; } = new List<string>();
}

public class CatalogInfo {
    public int PackageCount { get; set; }
    public DateTime? FetchedAt { get; set; }
    public bool Stale { get; set; }
}

/// <summary>
/// The Thunderstore catalogue for the Valheim community, cached on disk.
///
/// Thunderstore has no usable search endpoint — /api/experimental/package/ takes a "q"
/// parameter and then ignores it, and the frontend API sits behind a bot check. What
/// does work is the full community listing, so the panel takes that once and searches
/// locally. It is 162 MB of JSON, but gzip brings it to about 12 MB and only the newest
/// version of each package is kept, which leaves a few MB in memory. Streaming the array
/// element by element is what keeps the rest off the heap.
/// </summary>
public static class Thunderstore {

    public const string CatalogUrl = "https://thunderstore.io/c/valheim/api/v1/package/";

    /// <summary>The BepInEx build the Valheim community ships. Not a mod — it is the loader.</summary>
    public const string LoaderPackage = "denikson-BepInExPack_Valheim";

    /// <summary>
    /// Packages that are not mods and would only mislead here. r2modman is the desktop
    /// mod manager and, being the most downloaded thing in the community by a wide
    /// margin, it otherwise sits at the top of the list with nothing to install.
    /// </summary>
    static readonly HashSet<string> notMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        LoaderPackage, "ebkr-r2modman"
    };

    public const string CacheDir = "/opt/valheim/mods/cache";
    const string indexFile = "/opt/valheim/mods/catalog.json";

    /// <summary>A catalogue older than this is refetched before the next search.</summary>
    static readonly TimeSpan maxAge = TimeSpan.FromHours(12);

    static readonly HttpClient http = createClient();
    static readonly SemaphoreSlim loadGate = new SemaphoreSlim(1, 1);
    static List<ModPackage> packages = new List<ModPackage>();
    static DateTime fetchedAt;

    public static CatalogInfo Info() {
        return new CatalogInfo {
            PackageCount = packages.Count,
            FetchedAt = fetchedAt == default ? null : fetchedAt,
            Stale = fetchedAt == default || DateTime.Now - fetchedAt > maxAge
        };
    }

    /// <summary>
    /// Serves the catalogue from memory, then from disk, then from Thunderstore. Only the
    /// last of those is slow, and it happens at most twice a day.
    /// </summary>
    public static async Task<List<ModPackage>> CatalogAsync(Action<string>? log = null, bool force = false) {
        if(!force && packages.Count > 0 && DateTime.Now - fetchedAt <= maxAge) return packages;

        await loadGate.WaitAsync();
        try {
            if(!force && packages.Count > 0 && DateTime.Now - fetchedAt <= maxAge) return packages;
            if(!force && await loadFromDiskAsync() && DateTime.Now - fetchedAt <= maxAge) return packages;

            await fetchAsync(log);
            return packages;
        } finally {
            loadGate.Release();
        }
    }

    /// <summary>
    /// Whatever is already cached, without ever touching the network. The mods page polls
    /// for version numbers every few seconds — that must not turn into a 12 MB download.
    /// </summary>
    public static async Task<List<ModPackage>> CachedCatalogAsync() {
        if(packages.Count > 0) return packages;

        await loadGate.WaitAsync();
        try {
            await loadFromDiskAsync();
            return packages;
        } finally {
            loadGate.Release();
        }
    }

    static async Task<bool> loadFromDiskAsync() {
        if(packages.Count > 0) return true;
        if(!File.Exists(indexFile)) return false;

        try {
            DateTime written = File.GetLastWriteTime(indexFile);
            string cached = await File.ReadAllTextAsync(indexFile);
            List<ModPackage>? fromDisk = JsonSerializer.Deserialize<List<ModPackage>>(cached);
            if(fromDisk == null || fromDisk.Count == 0) return false;

            packages = fromDisk;
            fetchedAt = written;
            return true;
        } catch {
            // A truncated cache is not worth reporting; the next fetch replaces it.
            return false;
        }
    }

    /// <summary>Best matches for a query, or the most downloaded packages when it is empty.</summary>
    public static async Task<List<ModPackage>> SearchAsync(string query, int limit) {
        List<ModPackage> all = await CatalogAsync();
        string q = query.Trim();

        IEnumerable<ModPackage> candidates = all.Where(p => !p.Deprecated && !notMods.Contains(p.FullName));

        if(q.Length == 0) return candidates.OrderByDescending(p => p.Downloads).Take(limit).ToList();

        return candidates
            .Select(p => new { Package = p, Score = score(p, q) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Package.Downloads)
            .Take(limit)
            .Select(x => x.Package)
            .ToList();
    }

    public static async Task<ModPackage?> FindAsync(string fullName) {
        List<ModPackage> all = await CatalogAsync();
        return all.FirstOrDefault(p => p.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Puts a package zip in the cache, or returns the copy that is already there. The
    /// cache is what lets the client pack be built without downloading everything a
    /// second time, and it is also how a reinstall works when Thunderstore is down.
    /// </summary>
    public static async Task<string> DownloadAsync(ModPackage package, Action<string> log) {
        Directory.CreateDirectory(CacheDir);
        string target = Path.Combine(CacheDir, $"{package.FullName}-{package.Version}.zip");

        if(File.Exists(target) && new FileInfo(target).Length > 0) {
            log($"{package.FullName} {package.Version} liegt schon im Cache.");
            return target;
        }

        log($"Lade {package.FullName} {package.Version} ({package.FileSize / 1024.0:F0} KB) ...");
        string partial = target + ".part";

        using(Stream source = await http.GetStreamAsync(package.DownloadUrl))
        using(FileStream file = File.Create(partial)) {
            await source.CopyToAsync(file);
        }

        // Only a complete download gets the real name, so a cache hit is always usable.
        File.Move(partial, target, true);
        return target;
    }

    /// <summary>Drops cached zips that no longer belong to an installed version.</summary>
    public static void PruneCache(IEnumerable<string> keepFileNames) {
        if(!Directory.Exists(CacheDir)) return;
        HashSet<string> keep = new HashSet<string>(keepFileNames, StringComparer.OrdinalIgnoreCase);

        foreach(string path in Directory.GetFiles(CacheDir, "*")) {
            if(keep.Contains(Path.GetFileName(path))) continue;
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>
    /// Ranks a hit so that "epicloot" finds EpicLoot before the dozen mods that merely
    /// mention it in their description. Without this the description text wins on volume.
    /// </summary>
    static int score(ModPackage package, string query) {
        if(package.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 100;
        if(package.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 80;
        if(package.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 60;
        if(package.Owner.Contains(query, StringComparison.OrdinalIgnoreCase)) return 40;
        if(package.Description.Contains(query, StringComparison.OrdinalIgnoreCase)) return 20;
        if(package.Categories.Any(c => c.Contains(query, StringComparison.OrdinalIgnoreCase))) return 10;
        return 0;
    }

    static async Task fetchAsync(Action<string>? log) {
        log?.Invoke("Lade den Thunderstore-Katalog (rund 12 MB gepackt) ...");

        List<ModPackage> fresh = new List<ModPackage>();

        using(HttpResponseMessage response = await http.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead))
        using(Stream stream = await response.EnsureSuccessStatusCode().Content.ReadAsStreamAsync()) {
            // Element by element: parsing this as one document would put all 162 MB of
            // decompressed JSON on the heap, in a container sized for the game server.
            await foreach(v1Package? raw in JsonSerializer.DeserializeAsyncEnumerable<v1Package>(stream, jsonOptions)) {
                if(raw == null) continue;
                ModPackage? slim = slimDown(raw);
                if(slim != null) fresh.Add(slim);
            }
        }

        if(fresh.Count == 0) throw new Exception("Thunderstore lieferte einen leeren Katalog.");

        packages = fresh;
        fetchedAt = DateTime.Now;

        Directory.CreateDirectory(Path.GetDirectoryName(indexFile)!);
        await File.WriteAllTextAsync(indexFile, JsonSerializer.Serialize(fresh));
        log?.Invoke($"Katalog aktualisiert: {fresh.Count} Pakete.");
    }

    static ModPackage? slimDown(v1Package raw) {
        // v1 lists every version newest first; the panel only ever installs the newest.
        v1Version? latest = raw.Versions.FirstOrDefault();
        if(latest == null || !latest.IsActive) return null;

        return new ModPackage {
            FullName = raw.FullName,
            Owner = raw.Owner,
            Name = raw.Name,
            Description = latest.Description,
            Version = latest.VersionNumber,
            Icon = latest.Icon,
            PackageUrl = raw.PackageUrl,
            DownloadUrl = latest.DownloadUrl,
            // v1 counts downloads per version, so a package total is only ever a sum.
            Downloads = raw.Versions.Sum(v => v.Downloads),
            FileSize = latest.FileSize,
            Deprecated = raw.IsDeprecated,
            UpdatedAt = raw.DateUpdated,
            Categories = raw.Categories,
            Dependencies = latest.Dependencies
        };
    }

    static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions {
        PropertyNameCaseInsensitive = true
    };

    static HttpClient createClient() {
        SocketsHttpHandler handler = new SocketsHttpHandler {
            AutomaticDecompression = DecompressionMethods.All
        };
        HttpClient client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("valheim-panel");
        return client;
    }

    class v1Package {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("full_name")] public string FullName { get; set; } = "";
        [JsonPropertyName("owner")] public string Owner { get; set; } = "";
        [JsonPropertyName("package_url")] public string PackageUrl { get; set; } = "";
        [JsonPropertyName("date_updated")] public DateTime DateUpdated { get; set; }
        [JsonPropertyName("is_deprecated")] public bool IsDeprecated { get; set; }
        [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new List<string>();
        [JsonPropertyName("versions")] public List<v1Version> Versions { get; set; } = new List<v1Version>();
    }

    class v1Version {
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("icon")] public string Icon { get; set; } = "";
        [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
        [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = new List<string>();
        [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
        [JsonPropertyName("file_size")] public long FileSize { get; set; }
        [JsonPropertyName("is_active")] public bool IsActive { get; set; }
    }
}
