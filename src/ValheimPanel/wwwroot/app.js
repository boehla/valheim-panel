const $ = (id) => document.getElementById(id);

let token = sessionStorage.getItem("panelToken") || "";
let settingsLoaded = false;
let serverRunning = false;

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
    loadMods();
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
    serverRunning = s.running;
    renderConfigWarning();

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

// Jobs that change a list the page shows; that list is reloaded once when one of them
// ends, keyed on the finish timestamp so the poll does not refetch every five seconds.
const backupJobs = ["Sicherung", "Automatische Sicherung", "Wiederherstellung", "Welt neu generieren"];
const modJobs = ["Mod installieren", "Mods aktualisieren", "BepInEx installieren", "Mod-Katalog", "Client-Paket"];
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

        for (const id of ["btn-update-server", "btn-update-panel", "btn-backup",
                          "btn-mods-update", "btn-catalog", "btn-client-pack", "btn-loader-install",
                          "btn-config-save", "btn-config-reset"]) {
            $(id).disabled = job.running;
        }

        const key = `${job.name}@${job.finishedAt}`;
        if (!job.running && key !== lastFinishedJob) {
            if (backupJobs.includes(job.name)) {
                lastFinishedJob = key;
                loadBackups();
                loadSettings();
            } else if (modJobs.includes(job.name)) {
                lastFinishedJob = key;
                loadMods();
                if ($("mod-search").elements.q.value.trim()) searchMods();
            }
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

/* --- mods ------------------------------------------------------------- */

let mods = { loader: {}, mods: [], catalog: {} };

const ago = (iso) => {
    const minutes = Math.round((Date.now() - new Date(iso)) / 60000);
    if (minutes < 1) return "gerade eben";
    if (minutes < 60) return `vor ${minutes} min`;
    if (minutes < 1440) return `vor ${Math.round(minutes / 60)} h`;
    return `vor ${Math.round(minutes / 1440)} Tagen`;
};

async function loadMods() {
    // Not just the network: a 500 body parses perfectly well as JSON, and letting that
    // through as `mods` used to take out the whole page on the next property access —
    // silently, because nothing here is awaited.
    let res;
    try {
        res = await api("/api/mods");
    } catch {
        return;
    }

    if (!res.ok) {
        $("mods-warning").hidden = false;
        $("mods-warning").textContent = "Der Mod-Status ist nicht abrufbar: "
            + ((await res.json().catch(() => ({}))).error || `HTTP ${res.status}`)
            + " — das Log unten sagt meist, woran es liegt.";
        return;
    }

    mods = await res.json();

    renderLoader();
    renderInstalled();
    renderResults();
    loadConfigs();

    $("client-pack-link").hidden = !mods.clientPackReady;
    $("client-pack-age").textContent = mods.clientPackReady
        ? `zuletzt gebaut ${ago(mods.clientPackBuiltAt)}`
        : "noch nicht gebaut";

    $("catalog-age").textContent = mods.catalog.fetchedAt
        ? `${mods.catalog.packageCount} Pakete, geladen ${ago(mods.catalog.fetchedAt)}`
        : "noch nicht geladen";
}

function renderLoader() {
    const loader = mods.loader;
    const install = $("btn-loader-install");

    $("mods-body").hidden = !loader.installed;
    $("loader-toggle").hidden = !loader.installed;
    $("loader-enabled").checked = loader.enabled;

    $("loader-state").textContent = !loader.installed
        ? "Nicht installiert — der Server läuft ohne Mods."
        : `${loader.version}${loader.updateAvailable ? ` — ${loader.latestVersion} verfügbar` : ""}` +
          (loader.enabled ? "" : " — deaktiviert, der Server startet ohne Mods.");

    install.textContent = !loader.installed ? "Installieren"
        : loader.updateAvailable ? `Auf ${loader.latestVersion} aktualisieren`
            : "Neu installieren";

    const notes = [];
    if (mods.restartRequired) {
        notes.push("Auf der Platte hat sich etwas geändert. Der laufende Server hat die Mods beim "
            + "Start geladen und übernimmt sie erst nach einem Neustart.");
    }
    // A modded server that silently updates itself is a server that silently breaks: a new
    // Valheim build invalidates every assembly the mods were compiled against.
    if (loader.installed && loader.enabled && settingsLoaded && $("settings").elements.autoUpdateServer.checked) {
        notes.push("„Beim Start automatisch auf neue Version prüfen“ ist an. Ein Valheim-Update macht "
            + "Mods meist unbrauchbar, bis die Autoren nachziehen — für einen Modserver besser abschalten.");
    }

    $("mods-warning").hidden = notes.length === 0;
    $("mods-warning").textContent = notes.join(" ");
}

function renderInstalled() {
    const list = $("mod-list");
    if (!mods.mods.length) {
        list.replaceChildren(el("p", "hint", "Noch keine Mods installiert. Oben suchen."));
        return;
    }
    list.replaceChildren(...mods.mods.map(installedCard));
}

function installedCard(mod) {
    const side = el("div", "mod-side");

    const clientLabel = el("label", "check");
    const clientBox = document.createElement("input");
    clientBox.type = "checkbox";
    clientBox.checked = mod.client;
    clientBox.onchange = () => toggle(`/api/mods/${mod.fullName}/client`, clientBox.checked);
    clientLabel.append(clientBox, "für Clients");

    const actions = el("div", "row-actions");

    const power = document.createElement("button");
    power.textContent = mod.enabled ? "Deaktivieren" : "Aktivieren";
    power.onclick = () => toggle(`/api/mods/${mod.fullName}/enabled`, !mod.enabled);

    const remove = document.createElement("button");
    remove.className = "danger";
    remove.textContent = "Entfernen";
    remove.onclick = async () => {
        const needs = mods.mods.filter(m => m.dependencies.includes(mod.fullName)).map(m => m.name);
        const warning = needs.length ? `\n\nACHTUNG: ${needs.join(", ")} braucht diesen Mod.` : "";
        if (!confirm(`"${mod.name}" entfernen?${warning}`)) return;

        const res = await api(`/api/mods/${mod.fullName}`, { method: "DELETE" });
        if (!res.ok) alert((await res.json()).error);
        loadMods();
    };

    actions.append(power, remove);
    side.append(clientLabel, actions);

    const badges = [];
    if (!mod.enabled) badges.push(["off", "aus"]);
    if (mod.updateAvailable) badges.push(["new", `${mod.latestVersion} verfügbar`]);
    if (!mod.manual) badges.push(["dep", "Abhängigkeit"]);

    // Thunderstore's own tags, and the reason the "für Clients" box starts where it does.
    const server = mod.categories.includes("Server-side");
    const client = mod.categories.includes("Client-side");
    if (server && !client) badges.push(["dep", "nur Server"]);
    if (client && !server) badges.push(["dep", "nur Client"]);

    return modCard(mod, `${mod.version} · ${mod.owner}`, badges, side);
}

function modCard(mod, meta, badges, side) {
    const card = el("div", "mod");

    if (mod.icon) {
        const icon = document.createElement("img");
        icon.src = mod.icon;
        icon.alt = "";
        icon.loading = "lazy";
        icon.referrerPolicy = "no-referrer";
        card.append(icon);
    }

    const main = el("div", "mod-main");
    const title = document.createElement("div");
    const name = document.createElement("strong");
    name.textContent = mod.name;
    title.append(name);
    for (const [kind, text] of badges) title.append(" ", el("span", `badge ${kind}`, text));

    main.append(title, el("div", "mod-meta", meta), el("div", "mod-desc", mod.description));
    card.append(main, side);
    return card;
}

function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
}

async function toggle(path, value) {
    const res = await api(path, { method: "POST", body: JSON.stringify({ value }) });
    if (!res.ok) alert((await res.json()).error);
    loadMods();
}

/* --- mod search ------------------------------------------------------- */

// The very first search pulls the whole Thunderstore catalogue, which takes a moment.
// Everything after that is matched locally and comes back instantly.
let results = [];
let searchTimer = 0;

$("mod-search").addEventListener("submit", (e) => e.preventDefault());

$("mod-search").elements.q.addEventListener("input", () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(searchMods, 300);
});

async function searchMods() {
    const query = $("mod-search").elements.q.value.trim();
    if (!query) {
        results = [];
        renderResults();
        return;
    }

    $("mod-results").replaceChildren(el("p", "hint", "Suche … beim ersten Mal wird der Katalog geladen."));
    try {
        const res = await api(`/api/mods/search?q=${encodeURIComponent(query)}&limit=25`);
        if (!res.ok) throw new Error();
        results = await res.json();
    } catch {
        $("mod-results").replaceChildren(el("p", "error", "Thunderstore ist nicht erreichbar."));
        return;
    }
    renderResults();
    loadMods();
}

function renderResults() {
    const box = $("mod-results");
    if (!results.length) {
        box.replaceChildren();
        return;
    }

    box.replaceChildren(...results.map(hit => {
        const installed = mods.mods.find(m => m.fullName === hit.fullName);
        const side = el("div", "mod-side");
        const actions = el("div", "row-actions");

        const link = document.createElement("a");
        link.className = "mod-meta";
        link.href = hit.packageUrl;
        link.target = "_blank";
        link.rel = "noreferrer";
        link.textContent = "Thunderstore";

        const install = document.createElement("button");
        install.textContent = !installed ? "Installieren"
            : installed.version === hit.version ? "Installiert"
                : `Auf ${hit.version} aktualisieren`;
        install.disabled = installed && installed.version === hit.version;
        install.onclick = async () => {
            install.disabled = true;
            if (!await startJob("/api/mods/install", { fullName: hit.fullName })) install.disabled = false;
        };

        actions.append(install);
        side.append(link, actions);

        const downloads = hit.downloads > 1000 ? `${Math.round(hit.downloads / 1000)}k Downloads` : `${hit.downloads} Downloads`;
        return modCard(hit, `${hit.version} · ${hit.owner} · ${downloads}`, [], side);
    }));
}

/* --- mod actions ------------------------------------------------------ */

// Every one of these can come back 409 ("a job is already running") or fail outright, and
// swallowing that is what makes a button look dead. Accepted (202) counts as ok.
async function startJob(path, body) {
    const res = await api(path, { method: "POST", body: body && JSON.stringify(body) });
    if (res.ok) {
        pollJob();
        return true;
    }
    alert((await res.json().catch(() => ({}))).error || `Fehlgeschlagen (HTTP ${res.status}).`);
    return false;
}

$("btn-loader-install").addEventListener("click", () => startJob("/api/mods/loader/install"));

$("loader-enabled").addEventListener("change", async (e) => {
    await toggle("/api/mods/loader/enabled", e.target.checked);
});

$("btn-mods-update").addEventListener("click", () => startJob("/api/mods/update"));

$("btn-catalog").addEventListener("click", () => startJob("/api/mods/catalog/refresh"));

$("btn-client-pack").addEventListener("click", () => startJob("/api/mods/client-pack"));

/* --- mod configuration ------------------------------------------------ */

// The editor holds the file exactly as it came off disk, plus the timestamp it came with.
// That timestamp goes back with every save, because BepInEx rewrites config files when the
// server shuts down — without it, saving into a running server would quietly undo whatever
// the server itself had just written.
let configs = [];
let configFile = null;
let configDirty = false;

async function loadConfigs() {
    try {
        configs = await (await api("/api/mods/configs")).json();
    } catch { return; }
    renderConfigList();
}

function renderConfigList() {
    const select = $("config-file");
    const wanted = configFile?.path || select.value;

    $("config-empty").hidden = configs.length > 0;
    $("config-editor").hidden = configs.length === 0;

    if (!configs.length) {
        $("config-empty").textContent = mods.mods.length
            ? "Noch keine Konfigurationsdatei gefunden. Die meisten Mods legen sie erst beim ersten Start des Servers an."
            : "Noch keine Mods installiert — also auch nichts zu konfigurieren.";
        configFile = null;
        return;
    }

    const groups = new Map();
    for (const file of configs) {
        if (!groups.has(file.group)) groups.set(file.group, []);
        groups.get(file.group).push(file);
    }

    select.replaceChildren(...[...groups].map(([name, files]) => {
        const group = document.createElement("optgroup");
        group.label = files[0].enabled ? name : `${name} (deaktiviert)`;
        group.append(...files.map(file => {
            const option = document.createElement("option");
            option.value = file.path;
            option.textContent = file.name;
            return option;
        }));
        return group;
    }));

    if (configs.some(file => file.path === wanted)) select.value = wanted;

    // A reload triggered by something else must not throw away what is being typed,
    // nor the message from whatever caused that reload.
    if (!configDirty && select.value !== configFile?.path) openConfig(select.value, true);
}

async function openConfig(path, keepMessage = false) {
    const res = await api(`/api/mods/config?path=${encodeURIComponent(path)}`);
    if (!res.ok) {
        configFile = null;
        $("config-text").value = "";
        configMessage("error", (await res.json()).error);
        return;
    }

    configFile = await res.json();
    configDirty = false;
    $("config-text").value = configFile.text;
    $("config-meta").textContent = `${configFile.path} · geändert ${new Date(configFile.modifiedAt).toLocaleString("de-AT")}`;
    $("btn-config-restart").hidden = true;
    if (!keepMessage) configMessage("", "");
}

function configMessage(kind, text) {
    const msg = $("config-msg");
    msg.hidden = !text;
    msg.className = kind;
    msg.textContent = text;
}

// Shown from the polled status, so it stays right when the server is started or stopped
// from another tab while the editor is open.
function renderConfigWarning() {
    $("config-warning").hidden = !serverRunning;
}

$("config-text").addEventListener("input", () => {
    configDirty = true;
});

$("config-file").addEventListener("change", (e) => {
    if (configDirty && !confirm("Die Änderungen an dieser Datei sind nicht gespeichert. Verwerfen?")) {
        e.target.value = configFile.path;
        return;
    }
    openConfig(e.target.value);
});

$("btn-config-reload").addEventListener("click", () => {
    if (!configFile) return;
    if (configDirty && !confirm("Die Änderungen an dieser Datei sind nicht gespeichert. Verwerfen?")) return;
    openConfig(configFile.path);
});

$("btn-config-save").addEventListener("click", async () => {
    if (!configFile) return;

    const res = await api("/api/mods/config", {
        method: "PUT",
        body: JSON.stringify({
            path: configFile.path,
            text: $("config-text").value,
            stamp: configFile.stamp
        })
    });
    const body = await res.json();

    if (!res.ok) {
        configMessage("error", res.status === 409
            ? `${body.error} Über „Neu laden“ kommt die aktuelle Fassung — die Änderungen hier gehen dabei verloren.`
            : body.error);
        return;
    }

    configFile = body.content;
    configDirty = false;
    $("config-meta").textContent = `${configFile.path} · geändert ${new Date(configFile.modifiedAt).toLocaleString("de-AT")}`;
    $("btn-config-restart").hidden = false;
    configMessage("ok", "Gespeichert. Der Mod liest die Datei beim nächsten Start des Servers.");
    loadMods();
});

$("btn-config-restart").addEventListener("click", async () => {
    if (!confirm("Server neu starten? Spieler fliegen raus, die Welt wird vorher gespeichert.")) return;
    await api("/api/server/restart", { method: "POST" });
    $("btn-config-restart").hidden = true;
    refresh();
});

$("btn-config-reset").addEventListener("click", async () => {
    if (!configFile) return;
    if (!confirm(`"${configFile.name}" löschen?\n\nDer Mod legt die Datei beim nächsten Start mit seinen Standardwerten neu an. Die bisherige Fassung bleibt als .bak daneben liegen.`)) return;

    const res = await api(`/api/mods/config?path=${encodeURIComponent(configFile.path)}`, { method: "DELETE" });
    if (!res.ok) {
        configMessage("error", (await res.json()).error);
        return;
    }

    configFile = null;
    configDirty = false;
    $("config-text").value = "";
    $("config-meta").textContent = "";
    configMessage("ok", "Zurückgesetzt. Die Datei entsteht beim nächsten Start des Servers neu.");
    loadMods();
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
    // The mods warning about auto-update can only be decided once this form is filled in.
    renderLoader();

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
        renderLoader();
    } else {
        const body = await res.json();
        msg.className = "error";
        msg.textContent = (body.errors || [body.error]).join(" ");
    }
});

if (token) unlock();
