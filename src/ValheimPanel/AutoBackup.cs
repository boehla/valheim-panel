namespace ValheimPanel;

/// <summary>
/// Cyclic world backups. Due-ness is derived from the newest temporary archive on disk
/// rather than from a timer, so a panel restart -- or a self-update -- does not reset
/// the cycle and no schedule has to be persisted anywhere.
/// </summary>
public class AutoBackupService : BackgroundService {

    readonly ILogger<AutoBackupService> logger;

    /// <summary>How often due-ness is re-checked, not how often a backup is taken.</summary>
    static readonly TimeSpan tick = TimeSpan.FromMinutes(10);

    public AutoBackupService(ILogger<AutoBackupService> logger) {
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        // The server is still booting when the panel starts; nothing is due that early.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch(OperationCanceledException) { return; }

        while(!stoppingToken.IsCancellationRequested) {
            try {
                runOnce();
            } catch(Exception ex) {
                logger.LogWarning("Automatische Sicherung fehlgeschlagen: {Message}", ex.Message);
            }

            try { await Task.Delay(tick, stoppingToken); } catch(OperationCanceledException) { return; }
        }
    }

    void runOnce() {
        ServerSettings settings = ServerSettings.Load();
        if(settings.AutoBackupHours <= 0) return;

        // A job of our own would be pointless mid-update, and TryStart would refuse anyway.
        if(JobRunner.IsBusy) return;

        DateTime? newest = Backups.List().Where(b => !b.Permanent).Select(b => (DateTime?)b.CreatedAt).FirstOrDefault();
        DateTime due = (newest ?? DateTime.MinValue).AddHours(settings.AutoBackupHours);
        if(DateTime.Now < due) return;

        // Backing up a world that has not been written since the last archive would only
        // push an older one out of the ten we keep.
        string worldFile = Path.Combine(settings.SaveDir, "worlds_local", $"{settings.WorldName}.db");
        if(!File.Exists(worldFile)) return;
        if(newest != null && File.GetLastWriteTime(worldFile) <= newest) return;

        logger.LogInformation("Automatische Sicherung fällig (alle {Hours} h).", settings.AutoBackupHours);
        JobRunner.TryStart("Automatische Sicherung", log => Backups.CreateAsync(log, false));
    }
}
