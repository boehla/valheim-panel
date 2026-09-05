namespace ValheimPanel;

public class ServerSettings {
    public string ServerName { get; set; } = "Valheim";
    public int Port { get; set; } = 2456;
    public string WorldName { get; set; } = "Dedicated";
    public string Password { get; set; } = "";
    public bool Public { get; set; } = false;
    public bool Crossplay { get; set; } = true;
    public string SaveDir { get; set; } = "/opt/valheim/data";
    public int Backups { get; set; } = 4;
    public int BackupShort { get; set; } = 7200;
    public int BackupLong { get; set; } = 43200;
    public bool AutoUpdateServer { get; set; } = true;
    public int AutoBackupHours { get; set; } = 6;

    static readonly string configFile = "/etc/valheim/server.env";

    public static ServerSettings Load() {
        ServerSettings settings = new ServerSettings();
        if(!File.Exists(configFile)) return settings;

        Dictionary<string, string> raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach(string line in File.ReadAllLines(configFile)) {
            string trimmed = line.Trim();
            if(trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            int sep = trimmed.IndexOf('=');
            if(sep < 1) continue;
            raw[trimmed[..sep].Trim()] = trimmed[(sep + 1)..].Trim().Trim('"');
        }

        settings.ServerName = raw.GetValueOrDefault("SERVER_NAME", settings.ServerName);
        settings.WorldName = raw.GetValueOrDefault("WORLD_NAME", settings.WorldName);
        settings.Password = raw.GetValueOrDefault("SERVER_PASSWORD", settings.Password);
        settings.SaveDir = raw.GetValueOrDefault("SAVE_DIR", settings.SaveDir);
        settings.Port = parseInt(raw.GetValueOrDefault("SERVER_PORT"), settings.Port);
        settings.Backups = parseInt(raw.GetValueOrDefault("BACKUPS"), settings.Backups);
        settings.BackupShort = parseInt(raw.GetValueOrDefault("BACKUP_SHORT"), settings.BackupShort);
        settings.BackupLong = parseInt(raw.GetValueOrDefault("BACKUP_LONG"), settings.BackupLong);
        settings.Public = parseBool(raw.GetValueOrDefault("PUBLIC"), settings.Public);
        settings.Crossplay = parseBool(raw.GetValueOrDefault("CROSSPLAY"), settings.Crossplay);
        settings.AutoUpdateServer = parseBool(raw.GetValueOrDefault("AUTO_UPDATE"), settings.AutoUpdateServer);
        settings.AutoBackupHours = parseInt(raw.GetValueOrDefault("AUTO_BACKUP_HOURS"), settings.AutoBackupHours);
        return settings;
    }

    public void Save() {
        Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
        string content = $"""
            # Managed by valheim-panel — edits here are picked up on the next restart.
            SERVER_NAME={ServerName}
            SERVER_PORT={Port}
            WORLD_NAME={WorldName}
            SERVER_PASSWORD={Password}
            PUBLIC={(Public ? 1 : 0)}
            CROSSPLAY={(Crossplay ? 1 : 0)}
            SAVE_DIR={SaveDir}
            BACKUPS={Backups}
            BACKUP_SHORT={BackupShort}
            BACKUP_LONG={BackupLong}
            AUTO_UPDATE={(AutoUpdateServer ? 1 : 0)}
            AUTO_BACKUP_HOURS={AutoBackupHours}

            """;
        File.WriteAllText(configFile, content);
        // 0600 — the file holds the server password
        File.SetUnixFileMode(configFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// Valheim refuses to boot on these, so we reject them here instead of
    /// letting the user find out from a crash loop.
    /// </summary>
    public List<string> Validate() {
        List<string> errors = new List<string>();

        if(string.IsNullOrWhiteSpace(ServerName)) errors.Add("Servername darf nicht leer sein.");
        if(string.IsNullOrWhiteSpace(WorldName)) errors.Add("Weltname darf nicht leer sein.");
        if(Port < 1024 || Port > 65530) errors.Add("Port muss zwischen 1024 und 65530 liegen.");
        if(AutoBackupHours < 0 || AutoBackupHours > 168) errors.Add("Automatische Sicherung muss zwischen 0 und 168 Stunden liegen (0 = aus).");

        if(Password.Length > 0) {
            if(Password.Length < 5) errors.Add("Passwort muss mindestens 5 Zeichen haben.");
            if(ServerName.Contains(Password, StringComparison.OrdinalIgnoreCase)) errors.Add("Passwort darf nicht im Servernamen vorkommen.");
            if(WorldName.Contains(Password, StringComparison.OrdinalIgnoreCase)) errors.Add("Passwort darf nicht im Weltnamen vorkommen.");
        } else if(Public) {
            errors.Add("Ein öffentlicher Server braucht ein Passwort.");
        }

        return errors;
    }

    static int parseInt(string? value, int fallback) {
        return int.TryParse(value, out int result) ? result : fallback;
    }

    static bool parseBool(string? value, bool fallback) {
        if(string.IsNullOrWhiteSpace(value)) return fallback;
        return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
