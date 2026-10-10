import { call } from "./bridge.js";

// Plugin Dialogue page: find the user's plugins, read one, and show its voiced lines grouped by VoiceType.
// The lines live in the C# backend; this page shows a summary, previews lines on demand and tells the
// batch page which VoiceTypes to generate. `host` is supplied by app.js so the page can read and extend
// the Voice Mapping table and move between pages without importing app.js.

const $ = (id) => document.getElementById(id);
const PAGE = 100;

let host = null;
let plugins = [];            // [{name, path, source, sizeKb}]
let loaded = null;           // {pluginName, lineCount, voiceTypes:[{name,lines,speakers}], warnings, suggestedVoiceFolder}
let included = new Set();    // VoiceTypes ticked for generation
let lineFilterVoiceType = ""; // the VoiceType clicked in the table, if any
let shownLines = 0;
let lineRequest = 0;

const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

export function isLoaded() { return !!loaded; }
export function pluginName() { return loaded?.pluginName || ""; }
export function suggestedVoiceFolder() { return loaded?.suggestedVoiceFolder || ""; }

/** The VoiceTypes to generate, or null for "all" (nothing ticked off). */
export function includedVoiceTypes() {
  if (!loaded) return null;
  return loaded.voiceTypes.length === included.size ? null : [...included];
}

export function selectedLineCount() {
  return loaded ? loaded.voiceTypes.filter((v) => included.has(v.name)).reduce((n, v) => n + v.lines, 0) : 0;
}

export function dataFolder() { return $("plugin-data-path").value.trim(); }
export function extraFolder() { return $("plugin-extra-path").value.trim(); }

export function setFolders(data, extra) {
  $("plugin-data-path").value = data || "";
  $("plugin-extra-path").value = extra || "";
}

/** Called by app.js whenever the Voice Mapping table changes, so "Voice Mapping" status stays right. */
export function refreshMappingStatus() { if (loaded) renderVoiceTypes(); }

function mappingFor(voiceType) {
  return host.getVoiceMap().find((e) => e.voiceType.trim().toLowerCase() === voiceType.toLowerCase());
}

function mappingLabel(voiceType) {
  const entry = mappingFor(voiceType);
  if (!entry || !entry.voiceId) return { text: entry ? "Row added - pick a voice" : "Not mapped", ok: false };
  const provider = host.getProviders().find((p) => p.id === entry.providerId)?.displayName || entry.providerId;
  return { text: `${provider}: ${entry.voiceDisplay || entry.voiceId}`, ok: true };
}

// ---------------------------------------------------------------- finding plugins

async function detect(silent = false) {
  const { path, found } = await call("detectDataFolder");
  if (found) {
    $("plugin-data-path").value = path;
    host.appendLog(`Found the Skyrim Data folder: ${path}`, "ok");
    return true;
  }
  if (!silent) host.appendLog("Could not find Skyrim Special Edition automatically. Type or Browse to its Data folder.", "error");
  return false;
}

async function scan() {
  const data = dataFolder(), extra = extraFolder();
  if (!data && !extra) { host.appendLog("Set the Skyrim Data folder first (Auto-detect or Browse).", "error"); return; }
  const result = await call("listPlugins", { dataPath: data, extraPath: extra, includeOfficial: $("plugin-show-official").checked });
  plugins = Array.from(result.plugins);
  host.persistSettings();
  renderPluginList();
}

function renderPluginList() {
  const filter = $("plugin-filter").value.trim().toLowerCase();
  const list = plugins.filter((p) => !filter || p.name.toLowerCase().includes(filter) || p.source.toLowerCase().includes(filter));
  const select = $("plugin-list");
  const keep = select.value;
  select.innerHTML = list.map((p) =>
    `<option value="${esc(p.path)}">${esc(p.name)}  -  ${esc(p.source)}  (${p.sizeKb.toLocaleString()} KB)</option>`).join("");
  if (list.some((p) => p.path === keep)) select.value = keep;
  $("plugin-list-hint").textContent = plugins.length === 0
    ? "No plugins found. Check the folder, or use Open a plugin file."
    : `${list.length} of ${plugins.length} plugin(s) shown. Select one and click Read dialogue (or double-click it).`;
  $("plugin-read-btn").disabled = !select.value;
}

// ---------------------------------------------------------------- reading a plugin

async function readSelected(pathOverride) {
  const pluginPath = pathOverride || $("plugin-list").value;
  if (!pluginPath) return;
  const button = $("plugin-read-btn");
  button.disabled = true;
  const original = button.textContent;
  button.textContent = "Reading...";
  try {
    loaded = await call("readPlugin", { pluginPath, dataPath: dataFolder(), extraPath: extraFolder() });
    loaded.voiceTypes = Array.from(loaded.voiceTypes);
    // "Unknown" lines have no resolvable speaker; they start unticked so they aren't voiced by accident.
    included = new Set(loaded.voiceTypes.filter((v) => v.name !== "Unknown").map((v) => v.name));
    lineFilterVoiceType = "";
    $("plugin-lines-search").value = "";
    renderResult();
    host.onPluginLoaded();
  } catch (err) {
    host.appendLog(`Could not read the plugin: ${err.message}`, "error");
  } finally {
    button.textContent = original;
    button.disabled = !$("plugin-list").value;
  }
}

async function openPluginFile() {
  const path = await call("browseOpenFile", { title: "Open a plugin", filterName: "Skyrim plugins", filterExtensions: ["esp", "esm", "esl"] });
  if (path) await readSelected(path);
}

// ---------------------------------------------------------------- showing the result

function renderResult() {
  $("plugin-result").classList.remove("hidden");
  $("plugin-result-name").textContent = loaded.pluginName;
  $("plugin-warnings").innerHTML = loaded.warnings.map((w) => `<div>${esc(w)}</div>`).join("");
  renderVoiceTypes();
  loadLines(true);
}

function renderVoiceTypes() {
  const body = $("plugin-vt-body");
  body.innerHTML = "";
  for (const vt of loaded.voiceTypes) {
    const m = mappingLabel(vt.name);
    const tr = document.createElement("tr");
    tr.innerHTML = `
      <td><input type="checkbox" ${included.has(vt.name) ? "checked" : ""} /></td>
      <td class="wrap"><a href="#" class="vt-link">${esc(vt.name)}</a></td>
      <td class="num">${vt.lines.toLocaleString()}</td>
      <td class="wrap">${esc(vt.speakers.join(", "))}</td>
      <td class="wrap ${m.ok ? "mapped" : "unmapped"}">${esc(m.text)}</td>`;
    tr.querySelector("input").addEventListener("change", (e) => {
      if (e.target.checked) included.add(vt.name); else included.delete(vt.name);
      updateSummary();
      host.onPluginSelectionChanged();
    });
    tr.querySelector(".vt-link").addEventListener("click", (e) => {
      e.preventDefault();
      lineFilterVoiceType = vt.name;
      loadLines(true);
    });
    body.appendChild(tr);
  }
  $("plugin-vt-all").checked = included.size === loaded.voiceTypes.length;
  updateSummary();
}

function updateSummary() {
  const unmapped = loaded.voiceTypes.filter((v) => included.has(v.name) && !mappingLabel(v.name).ok).length;
  $("plugin-summary").textContent =
    `${loaded.lineCount.toLocaleString()} line(s) in ${loaded.voiceTypes.length} VoiceType(s). ` +
    `${selectedLineCount().toLocaleString()} line(s) ticked for generation.` +
    (unmapped > 0 ? ` ${unmapped} ticked VoiceType(s) still need a voice in Voice Mapping.` : "");
}

async function loadLines(reset) {
  if (!loaded) return;
  const request = ++lineRequest;
  if (reset) { shownLines = 0; $("plugin-lines-body").innerHTML = ""; }
  const result = await call("getPluginLines", {
    skip: shownLines, take: PAGE, search: $("plugin-lines-search").value.trim(),
    voiceTypes: lineFilterVoiceType ? [lineFilterVoiceType] : null
  });
  if (request !== lineRequest) return; // a newer search replaced this one
  const body = $("plugin-lines-body");
  for (const l of result.lines) {
    const tr = document.createElement("tr");
    tr.innerHTML = `<td class="wrap">${esc(l.id)}</td><td class="wrap">${esc(l.voiceType)}</td><td class="wrap">${esc(l.text)}</td>`;
    body.appendChild(tr);
  }
  shownLines += result.lines.length;
  $("plugin-lines-count").textContent =
    `${shownLines.toLocaleString()} of ${result.total.toLocaleString()}` + (lineFilterVoiceType ? ` - ${lineFilterVoiceType} only` : "");
  $("plugin-lines-more-btn").classList.toggle("hidden", shownLines >= result.total);
  $("plugin-lines-clear-btn").classList.toggle("hidden", !lineFilterVoiceType);
}

// ---------------------------------------------------------------- actions

function addMissingToMapping() {
  const names = loaded.voiceTypes.filter((v) => included.has(v.name) && !mappingFor(v.name)).map((v) => v.name);
  if (names.length === 0) { host.appendLog("Every ticked VoiceType already has a row in Voice Mapping.", "ok"); return; }
  host.addVoiceTypes(names);
  host.appendLog(`Added ${names.length} VoiceType(s) to Voice Mapping. Pick a provider and voice for each, then Save Mapping.`, "ok");
  host.goToPage("voice-mapping-page");
}

async function exportCsv() {
  const path = await call("browseSaveFile", { title: "Export dialogue as CSV", filterName: "CSV files", filterExtensions: ["csv"], defaultFileName: loaded.pluginName.replace(/\.[^.]+$/, "") + ".csv" });
  if (!path) return;
  const result = await call("exportPluginCsv", { path, includeVoiceTypes: includedVoiceTypes() });
  host.appendLog(`Exported ${result.count} line(s) to ${path}.`, "ok");
}

export function initPluginDialogue(hostApi) {
  host = hostApi;
  const safe = (fn) => async (...a) => { try { await fn(...a); } catch (err) { host.appendLog(`Error: ${err.message}`, "error"); } };

  $("plugin-detect-btn").addEventListener("click", safe(async () => { if (await detect()) await scan(); }));
  $("plugin-scan-btn").addEventListener("click", safe(scan));
  $("plugin-show-official").addEventListener("change", safe(async () => { if (dataFolder() || extraFolder()) await scan(); }));
  $("plugin-filter").addEventListener("input", renderPluginList);
  $("plugin-list").addEventListener("change", () => { $("plugin-read-btn").disabled = !$("plugin-list").value; });
  $("plugin-list").addEventListener("dblclick", safe(() => readSelected()));
  $("plugin-read-btn").addEventListener("click", safe(() => readSelected()));
  $("plugin-open-btn").addEventListener("click", safe(openPluginFile));
  $("plugin-vt-all").addEventListener("change", (e) => {
    included = e.target.checked ? new Set(loaded.voiceTypes.map((v) => v.name)) : new Set();
    renderVoiceTypes();
    host.onPluginSelectionChanged();
  });
  $("plugin-add-mapping-btn").addEventListener("click", addMissingToMapping);
  $("plugin-generate-btn").addEventListener("click", () => {
    if (included.size === 0) { host.appendLog("Tick at least one VoiceType first.", "error"); return; }
    host.useForBatch();
  });
  $("plugin-export-btn").addEventListener("click", safe(exportCsv));
  let timer = null;
  $("plugin-lines-search").addEventListener("input", () => { clearTimeout(timer); timer = setTimeout(safe(() => loadLines(true)), 250); });
  $("plugin-lines-clear-btn").addEventListener("click", safe(() => { lineFilterVoiceType = ""; return loadLines(true); }));
  $("plugin-lines-more-btn").addEventListener("click", safe(() => loadLines(false)));
}

/** First run: look for Skyrim by itself, and list plugins if a folder is already known. */
export async function startUp() {
  if (!dataFolder()) await detect(true).catch(() => {});
  if (dataFolder() || extraFolder()) {
    try { await scan(); } catch (err) { host.appendLog(`Plugin list: ${err.message}`, "error"); }
  }
}
