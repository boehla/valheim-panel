const $ = (id) => document.getElementById(id);

let token = sessionStorage.getItem("panelToken") || "";
let settingsLoaded = false;

async function api(path, options = {}) {
    const res = await fetch(path, {
        ...options,
        headers: {
            "Content-Type": "application/json",
            "X-Panel-Token": token,
            ...(options.headers || {})
        }
    });
    if (res.status === 401) {
        sessionStorage.removeItem("panelToken");
        location.reload();
    }
    return res;
}

/* --- login ------------------------------------------------------------ */

$("gate-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    token = $("token").value.trim();
    const res = await fetch("/api/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ token })
    });
    if (!res.ok) {
        $("gate-error").textContent = "Token stimmt nicht.";
        $("gate-error").hidden = false;
        return;
    }
    sessionStorage.setItem("panelToken", token);
    unlock();
});

function unlock() {
    $("gate").hidden = true;
    $("app").hidden = false;
    loadSettings();
    loadBackups();
    checkPanelUpdate();
    refresh();
    setInterval(refresh, 5000);
}

/* --- status ----------------------------------------------------------- */

const stateLabels = {
    active: "läuft",
    activating: "startet",
    deactivating: "stoppt",
    inactive: "gestoppt",
    failed: "abgestürzt"
};

async function refresh() {
    try {
        const status = await (await api("/api/status")).json();
        renderStatus(status);
    } catch { /* transient — next tick retries */ }

    try {
        $("log").textContent = await (await api("/api/logs?lines=200")).text();
    } catch { }

    pollJob();
}

function renderStatus(s) {
    const accent = s.running ? "var(--pine)" : (s.state === "failed" ? "var(--ember)" : "var(--muted)");
    document.documentElement.style.setProperty("--state", accent);

    $("hero-world").textContent = s.worldName || "keine Welt gesetzt";
    $("hero-state").textContent = stateLabels[s.state] || s.state;
    $("hero-detail").textContent = s.startedAt && s.running
        ? `seit ${new Date(s.startedAt).toLocaleString("de-AT")}`
        : "";

    renderJoin(s);

    $("fact-players").textContent = s.running ? s.playersOnline : "–";
    $("fact-build").textContent = s.gameVersion || s.buildId || "–";
    $("fact-saved").textContent = s.worldSavedAt
        ? new Date(s.worldSavedAt).toLocaleTimeString("de-AT", { hour: "2-digit", minute: "2-digit" })
        : "–";
    $("fact-size").textContent = s.worldSizeBytes ? `${(s.worldSizeBytes / 1048576).toFixed(1)} MB` : "–";

    $("server-build").querySelector("span").textContent =
        s.gameVersion ? `${s.gameVersion}${s.buildId ? ` (Build ${s.buildId})` : ""}` : (s.buildId || "unbekannt");
    $("panel-version").dataset.current = s.panelVersion;

    $("btn-start").disabled = s.running;
    $("btn-stop").disabled = !s.running;
}

/* --- join ------------------------------------------------------------- */

// PlayFab issues the code a few seconds after start, so an empty one is normal at
// first. Valheim only registers the session — and only then hands out a code — when
// the server is public, so crossplay alone leaves the field empty forever.
function renderJoin(s) {
    $("join").hidden = !s.running;
    if (!s.running) return;

    const form = $("settings").elements;
    const upSeconds = s.startedAt ? (Date.now() - new Date(s.startedAt)) / 1000 : 0;
    const code = $("join-code");
    // The first refresh can beat loadSettings, and unchecked boxes would then read
    // as a misconfiguration that isn't there.
    code.textContent = s.joinCode || (
        !settingsLoaded ? "wird vergeben …"
            : !form.crossplay.checked ? "Crossplay ist aus"
                : !form.public.checked ? "braucht „In der Serverliste zeigen“"
                    : upSeconds > 60 ? "kein Code — Log prüfen"
                        : "wird vergeben …");
    code.classList.toggle("small", !s.joinCode);
    code.dataset.copy = s.joinCode;

    const port = $("settings").elements.port.value || "2456";
    const address = `${location.hostname}:${port}`;
    $("join-address").textContent = address;
    $("join-address").dataset.copy = address;
    $("join-address").classList.add("small");
}

// Only the real values carry data-copy, so a placeholder cannot end up on the
// clipboard. Silent where clipboard access needs a secure context (plain http).
$("join").addEventListener("click", (e) => {
    const value = e.target.closest("strong")?.dataset.copy;
    if (value) navigator.clipboard?.writeText(value);
});

/* --- controls --------------------------------------------------------- */

const control = (id, action, confirmText) => $(id).addEventListener("click", async () => {
    if (confirmText && !confirm(confirmText)) return;
    $(id).disabled = true;
    await api(`/api/server/${action}`, { method: "POST" });
    refresh();
});

control("btn-start", "start");
control("btn-restart", "restart", "Server neu starten? Spieler fliegen raus, die Welt wird vorher gespeichert.");
control("btn-stop", "stop", "Server stoppen?");

$("btn-update-server").addEventListener("click", async () => {
    if (!confirm("Server stoppen und über SteamCMD aktualisieren?")) return;
    await api("/api/server/update", { method: "POST" });
    pollJob();
});

/* --- jobs ------------------------------------------------------------- */

// Jobs that change the backup list; the list is reloaded once when one of them ends,
// keyed on the finish timestamp so the poll does not refetch every five seconds.
const backupJobs = ["Sicherung", "Automatische Sicherung", "Wiederherstellung", "Welt neu generieren"];
let lastFinishedJob = "";

async function pollJob() {
    try {
        const job = await (await api("/api/job")).json();
        if (!job.name) return;

        $("job-block").hidden = false;
        $("job-title").textContent = job.running
            ? `${job.name} läuft …`
            : `${job.name} — ${job.failed ? "fehlgeschlagen" : "fertig"}`;
        $("job-log").textContent = job.log.join("\n");
        $("job-log").scrollTop = $("job-log").scrollHeight;

        $("btn-update-server").disabled = job.running;
        $("btn-update-panel").disabled = job.running;
        $("btn-backup").disabled = job.running;

        const key = `${job.name}@${job.finishedAt}`;
        if (!job.running && backupJobs.includes(job.name) && key !== lastFinishedJob) {
            lastFinishedJob = key;
            loadBackups();
            loadSettings();
        }
    } catch { }
}

/* --- backups ---------------------------------------------------------- */

async function loadBackups() {
    let backups;
    try {
        backups = await (await api("/api/backups")).json();
    } catch { return; }

    const list = $("backup-list");
    if (!backups.length) {
        list.innerHTML = `<tr><td class="muted">Noch keine Sicherung angelegt.</td></tr>`;
        return;
    }

    list.replaceChildren(...backups.map(b => {
        const row = document.createElement("tr");

        const when = document.createElement("td");
        when.textContent = new Date(b.createdAt).toLocaleString("de-AT");
        if (b.permanent) {
            const badge = document.createElement("span");
            badge.className = "badge";
            badge.textContent = "dauerhaft";
            when.append(" ", badge);
        }

        const world = document.createElement("td");
        world.textContent = b.worldName;

        const size = document.createElement("td");
        size.textContent = `${(b.sizeBytes / 1048576).toFixed(1)} MB`;

        const actions = document.createElement("td");
        actions.className = "row-actions";

        const restore = document.createElement("button");
        restore.textContent = "Wiederherstellen";
        restore.onclick = async () => {
            if (!confirm(`"${b.worldName}" vom ${new Date(b.createdAt).toLocaleString("de-AT")} wiederherstellen?\n\nDer Server wird gestoppt. Der aktuelle Stand wird vorher gesichert.`)) return;
            await api("/api/backups/restore", { method: "POST", body: JSON.stringify({ fileName: b.fileName }) });
            pollJob();
        };

        const remove = document.createElement("button");
        remove.className = "danger";
        remove.textContent = "Löschen";
        remove.onclick = async () => {
            if (!confirm(`Sicherung vom ${new Date(b.createdAt).toLocaleString("de-AT")} endgültig löschen?`)) return;
            const res = await api(`/api/backups/${encodeURIComponent(b.fileName)}`, { method: "DELETE" });
            if (!res.ok) alert((await res.json()).error);
            loadBackups();
        };

        actions.append(restore, remove);
        row.append(when, world, size, actions);
        return row;
    }));
}

$("btn-backup").addEventListener("click", async () => {
    await api("/api/backups", {
        method: "POST",
        body: JSON.stringify({ permanent: $("backup-perma").checked })
    });
    pollJob();
});

/* --- world regeneration ----------------------------------------------- */

$("regenerate").addEventListener("submit", async (e) => {
    e.preventDefault();
    const form = e.target;
    const msg = $("regenerate-msg");

    if (!confirm("Die aktuelle Welt wird gelöscht und beim nächsten Start neu erzeugt.\n\nEine dauerhafte Sicherung wird vorher angelegt, aber der Spielfortschritt in dieser Welt ist danach nur noch über eine Wiederherstellung erreichbar.\n\nWirklich fortfahren?")) return;

    const res = await api("/api/world/regenerate", {
        method: "POST",
        body: JSON.stringify({
            confirm: form.elements.confirm.value,
            newWorldName: form.elements.newWorldName.value
        })
    });

    if (res.ok || res.status === 202) {
        msg.hidden = true;
        form.reset();
        pollJob();
        return;
    }

    msg.hidden = false;
    msg.className = "error";
    msg.textContent = (await res.json()).error;
});

/* --- panel self-update ------------------------------------------------ */

async function checkPanelUpdate() {
    const btn = $("btn-update-panel");
    try {
        const info = await (await api("/api/panel/update")).json();
        if (info.updateAvailable) {
            $("panel-version").textContent = `${info.current} → ${info.latest} verfügbar`;
            btn.textContent = `Auf ${info.latest} aktualisieren`;
            btn.disabled = false;
        } else {
            $("panel-version").textContent = `${info.current} — aktuell`;
            btn.textContent = "Aktuell";
            btn.disabled = true;
        }
    } catch {
        $("panel-version").textContent = "Release-Check nicht erreichbar";
        btn.textContent = "Erneut prüfen";
        btn.disabled = false;
        btn.onclick = checkPanelUpdate;
        return;
    }

    btn.onclick = async () => {
        if (!confirm("Panel aktualisieren? Die Weboberfläche startet dabei kurz neu.")) return;
        await api("/api/panel/update", { method: "POST" });
        pollJob();
    };
}

/* --- settings --------------------------------------------------------- */

async function loadSettings() {
    const settings = await (await api("/api/settings")).json();
    const form = $("settings");
    for (const [key, value] of Object.entries(settings)) {
        const field = form.elements[key];
        if (!field) continue;
        if (field.type === "checkbox") field.checked = value;
        else field.value = value;
    }
    settingsLoaded = true;

    $("auto-backup-hint").textContent = settings.autoBackupHours > 0
        ? `Automatisch alle ${settings.autoBackupHours} Stunden, sofern die Welt seit der letzten Sicherung gespeichert wurde.`
        : "Automatische Sicherung ist aus — einzustellen weiter oben.";
}

$("settings").addEventListener("submit", async (e) => {
    e.preventDefault();
    const form = e.target;
    const payload = {};
    for (const field of form.elements) {
        if (!field.name) continue;
        if (field.type === "checkbox") payload[field.name] = field.checked;
        else if (field.type === "number") payload[field.name] = Number(field.value);
        else payload[field.name] = field.value;
    }

    const res = await api("/api/settings", { method: "PUT", body: JSON.stringify(payload) });
    const msg = $("settings-msg");
    msg.hidden = false;

    if (res.ok) {
        msg.className = "ok";
        msg.textContent = "Gespeichert. Beim nächsten Neustart aktiv.";
    } else {
        const body = await res.json();
        msg.className = "error";
        msg.textContent = (body.errors || [body.error]).join(" ");
    }
});

if (token) unlock();
