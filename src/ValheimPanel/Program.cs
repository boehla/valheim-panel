using System.Reflection;
using Microsoft.Extensions.FileProviders;
using ValheimPanel;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{Environment.GetEnvironmentVariable("PANEL_PORT") ?? "8099"}");
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Services.AddHostedService<AutoBackupService>();

WebApplication app = builder.Build();

string panelToken = Environment.GetEnvironmentVariable("PANEL_TOKEN") ?? "";
if(panelToken.Length < 16) {
    app.Logger.LogWarning("PANEL_TOKEN fehlt oder ist zu kurz — das Panel ist ungeschützt!");
}

// Everything under /api needs the token. The static UI itself is harmless.
app.Use(async (ctx, next) => {
    if(!ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path == "/api/login") {
        await next();
        return;
    }

    string supplied = ctx.Request.Headers["X-Panel-Token"].FirstOrDefault()
        ?? ctx.Request.Cookies["panel_token"]
        ?? "";

    if(panelToken.Length == 0 || supplied != panelToken) {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "Nicht autorisiert" });
        return;
    }
    await next();
});

// The UI lives inside the binary, so it must not be resolved from the working directory.
IFileProvider webAssets = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webAssets });
app.UseStaticFiles(new StaticFileOptions { FileProvider = webAssets });

app.MapPost("/api/login", (LoginRequest req, HttpContext ctx) => {
    if(panelToken.Length == 0 || req.Token != panelToken) return Results.Unauthorized();

    ctx.Response.Cookies.Append("panel_token", req.Token, new CookieOptions {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        MaxAge = TimeSpan.FromDays(30)
    });
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/status", async () => Results.Ok(await ServerControl.GetStatusAsync()));

app.MapGet("/api/logs", async (int? lines) => Results.Text(await ServerControl.GetLogAsync(Math.Clamp(lines ?? 200, 10, 2000))));

app.MapPost("/api/server/{action}", async (string action) => {
    ShellResult res = action switch {
        "start" => await ServerControl.StartAsync(),
        "stop" => await ServerControl.StopAsync(),
        "restart" => await ServerControl.RestartAsync(),
        _ => new ShellResult { ExitCode = 2, StdErr = "Unbekannte Aktion" }
    };
    return res.Ok ? Results.Ok(new { ok = true }) : Results.BadRequest(new { error = res.StdErr.Trim() });
});

app.MapPost("/api/server/update", () => {
    bool started = JobRunner.TryStart("Serverupdate", log => ServerControl.UpdateServerAsync(log));
    return started ? Results.Accepted() : Results.Conflict(new { error = "Es läuft bereits ein Job." });
});

app.MapGet("/api/job", () => Results.Ok(JobRunner.Snapshot()));

app.MapGet("/api/backups", () => Results.Ok(Backups.List()));

app.MapPost("/api/backups", (BackupRequest req) => {
    bool started = JobRunner.TryStart("Sicherung", log => Backups.CreateAsync(log, req.Permanent));
    return started ? Results.Accepted() : Results.Conflict(new { error = "Es läuft bereits ein Job." });
});

app.MapPost("/api/backups/restore", (RestoreRequest req) => {
    bool started = JobRunner.TryStart("Wiederherstellung", log => Backups.RestoreAsync(log, req.FileName));
    return started ? Results.Accepted() : Results.Conflict(new { error = "Es läuft bereits ein Job." });
});

app.MapDelete("/api/backups/{fileName}", (string fileName) => {
    if(JobRunner.IsBusy) return Results.Conflict(new { error = "Es läuft gerade ein Job." });
    try {
        Backups.Delete(fileName);
        return Results.Ok(new { ok = true });
    } catch(Exception ex) {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// The typed world name is checked here, not only in the browser: this is the one
// endpoint that destroys data, and a confirm dialog is not a safeguard an API has.
app.MapPost("/api/world/regenerate", (RegenerateRequest req) => {
    ServerSettings settings = ServerSettings.Load();
    if(req.Confirm != settings.WorldName) {
        return Results.BadRequest(new { error = $"Zur Bestätigung muss der Weltname \"{settings.WorldName}\" exakt eingegeben werden." });
    }

    string newName = (req.NewWorldName ?? "").Trim();
    bool started = JobRunner.TryStart("Welt neu generieren", log => Backups.RegenerateAsync(log, newName));
    return started ? Results.Accepted() : Results.Conflict(new { error = "Es läuft bereits ein Job." });
});

app.MapGet("/api/settings", () => Results.Ok(ServerSettings.Load()));

app.MapPut("/api/settings", (ServerSettings settings) => {
    List<string> errors = settings.Validate();
    if(errors.Count > 0) return Results.BadRequest(new { errors });

    settings.Save();
    return Results.Ok(new { ok = true, restartRequired = true });
});

app.MapGet("/api/panel/update", async () => {
    try {
        return Results.Ok(await SelfUpdate.CheckAsync());
    } catch(Exception ex) {
        return Results.Problem($"Release-Check fehlgeschlagen: {ex.Message}");
    }
});

app.MapPost("/api/panel/update", () => {
    bool started = JobRunner.TryStart("Panel-Update", log => SelfUpdate.ApplyAsync(log));
    return started ? Results.Accepted() : Results.Conflict(new { error = "Es läuft bereits ein Job." });
});

app.Run();

record LoginRequest(string Token);
record BackupRequest(bool Permanent);
record RestoreRequest(string FileName);
record RegenerateRequest(string Confirm, string? NewWorldName);
