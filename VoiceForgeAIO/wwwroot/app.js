import { call, on } from "./bridge.js";
import { initLocalVoices } from "./local-voices.js";

// ==========================================================================
// State
// ==========================================================================

let providers = [];          // [{id, displayName, models, defaultModel, supportsWav, requiresApiKey}]
let providerKeys = {};       // {providerId: {apiKey, groupId}}
let voiceMap = [];           // [{voiceType, providerId, voiceId, voiceDisplay}]
let voicesByProvider = {};   // {providerId: [{voiceId, displayName, description}]}
let batchRunning = false;
let batchPaused = false;

const providerById = (id) => providers.find((p) => p.id === id);

// ==========================================================================
// Init
// ==========================================================================

async function init() {
  wireStaticHandlers();

  const state = await call("getInitialState");
  providers = Array.from(state.providers);
  providerKeys = state.providerKeys;
  voiceMap = Array.from(state.voiceMap);

  renderProviderKeyRows();
  renderProviderSelect(document.getElementById("single-provider"));
  applySettingsToForm(state.settings);
  renderVoiceMapTable();
  refreshSingleLineDropdowns();

  // Local providers (OmniVoice, Qwen3-TTS) list the voices made on the Local Voices page.
  await initLocalVoices(onLocalVoicesChanged).catch((err) => appendLog(`Local voices: ${err.message}`, "error"));

  if (state.hasSeenSplash) {
    document.getElementById("splash-overlay").classList.add("hidden");
  }

  appendLog('Ready. Enter your API keys in Settings, then "Get Available Voices" before building the Voice Mapping table.');
}

/** The voices of a local provider changed (loaded, saved or deleted): offer them in Voice Mapping and Single-Line without a refresh. */
function onLocalVoicesChanged(providerId, voices) {
  voicesByProvider[providerId] = voices;
  renderVoiceMapTable();
  refreshSingleLineDropdowns();
}

function applySettingsToForm(s) {
  document.getElementById("facefx-path").value = s.faceFxWrapperPath || "";
  document.getElementById("fonixdata-path").value = s.fonixDataPath || "";
  document.getElementById("xwma-path").value = s.xwmaEncodePath || "";
  document.getElementById("lipfuzer-path").value = s.lipFuzerPath || "";
  document.getElementById("batch-csv-path").value = s.lastCsvPath || "";
  document.getElementById("batch-voice-folder").value = s.lastVoiceFolderPath || "";
  document.getElementById("batch-concurrency").value = s.concurrency || 3;
  document.getElementById("batch-lip").checked = !!s.lipGenerationEnabled;
  document.getElementById("batch-fuz").checked = !!s.fuzGenerationEnabled && !!s.lipGenerationEnabled;
  updateFuzCheckboxState();

  if (s.lastProviderId) {
    const sel = document.getElementById("single-provider");
    if ([...sel.options].some((o) => o.value === s.lastProviderId)) sel.value = s.lastProviderId;
  }
}

// ==========================================================================
// Settings / provider key rows
// ==========================================================================

function renderProviderKeyRows() {
  const container = document.getElementById("provider-key-rows");
  container.innerHTML = "";

  for (const p of providers) {
    const keys = providerKeys[p.id] || { apiKey: "", groupId: "" };

    if (p.requiresApiKey === false) {
      const note = document.createElement("div");
      note.className = "field-hint";
      note.innerHTML = `<strong>${escapeHtml(p.displayName)}</strong>: runs on this computer, no key needed. Set it up on the Local Voices page.`;
      container.appendChild(note);
      continue;
    }

    const row = document.createElement("div");
    row.className = "field-row";
    row.innerHTML = `
      <div class="grow">
        <label class="field-label">${p.displayName}</label>
        <input type="password" class="provider-key-input" data-provider="${p.id}" data-field="apiKey" value="${escapeAttr(keys.apiKey)}" />
      </div>`;
    container.appendChild(row);

    if (p.id === "MiniMax") {
      const groupRow = document.createElement("div");
      groupRow.className = "field-row";
      groupRow.innerHTML = `
        <div class="grow">
          <label class="field-label">MiniMax Group ID</label>
          <input type="password" class="provider-key-input" data-provider="MiniMax" data-field="groupId" value="${escapeAttr(keys.groupId)}" />
        </div>`;
      container.appendChild(groupRow);
    }
  }
}

function collectProviderKeysFromForm() {
  const result = {};
  for (const p of providers) result[p.id] = { apiKey: "", groupId: "" };
  document.querySelectorAll(".provider-key-input").forEach((input) => {
    const providerId = input.dataset.provider;
    const field = input.dataset.field;
    if (!result[providerId]) result[providerId] = { apiKey: "", groupId: "" };
    result[providerId][field] = input.value.trim();
  });
  return result;
}

function renderProviderSelect(selectEl) {
  selectEl.innerHTML = providers.map((p) => `<option value="${p.id}">${p.displayName}</option>`).join("");
}

// ==========================================================================
// Voice mapping table
// ==========================================================================

function renderVoiceMapTable() {
  const tbody = document.getElementById("voice-map-body");
  tbody.innerHTML = "";
  voiceMap.forEach((entry, index) => tbody.appendChild(buildVoiceMapRow(entry, index)));
}

function buildVoiceMapRow(entry, index) {
  const tr = document.createElement("tr");
  tr.dataset.index = String(index);

  const typeTd = document.createElement("td");
  const typeInput = document.createElement("input");
  typeInput.type = "text";
  typeInput.value = entry.voiceType;
  typeInput.placeholder = "e.g. Kaylin";
  typeInput.addEventListener("input", () => { voiceMap[index].voiceType = typeInput.value; });
  typeInput.addEventListener("click", () => selectRow(tr));
  typeTd.appendChild(typeInput);

  const providerTd = document.createElement("td");
  const providerSelect = document.createElement("select");
  providerSelect.innerHTML = providers.map((p) => `<option value="${p.id}">${p.displayName}</option>`).join("");
  providerSelect.value = entry.providerId || providers[0]?.id || "";
  providerSelect.addEventListener("click", () => selectRow(tr));
  providerSelect.addEventListener("change", () => {
    voiceMap[index].providerId = providerSelect.value;
    voiceMap[index].voiceId = "";
    voiceMap[index].voiceDisplay = "";
    rebuildVoiceSelect(voiceSelect, voiceMap[index]);
  });
  providerTd.appendChild(providerSelect);

  const voiceTd = document.createElement("td");
  const voiceSelect = document.createElement("select");
  voiceSelect.addEventListener("click", () => selectRow(tr));
  voiceSelect.addEventListener("change", () => {
    const opt = voiceSelect.selectedOptions[0];
    voiceMap[index].voiceId = voiceSelect.value;
    voiceMap[index].voiceDisplay = opt ? opt.dataset.display || "" : "";
  });
  rebuildVoiceSelect(voiceSelect, entry);
  voiceTd.appendChild(voiceSelect);

  tr.appendChild(typeTd);
  tr.appendChild(providerTd);
  tr.appendChild(voiceTd);
  return tr;
}

/** Populates a row's Voice <select> from the cached voice list for its current
 *  Provider, always including the row's current value even if it's not (yet)
 *  in that list, so a persisted mapping never displays blank. */
function rebuildVoiceSelect(selectEl, entry) {
  const voices = voicesByProvider[entry.providerId] || [];
  const options = [];

  if (entry.voiceId) {
    const known = voices.find((v) => v.voiceId === entry.voiceId);
    const label = known ? `${known.displayName} [${known.voiceId}]` : (entry.voiceDisplay ? `${entry.voiceDisplay} [${entry.voiceId}]` : `[${entry.voiceId}]`);
    options.push(`<option value="${escapeAttr(entry.voiceId)}" data-display="${escapeAttr(entry.voiceDisplay || (known ? known.displayName : ""))}" selected>${escapeHtml(label)}</option>`);
  } else {
    options.push(`<option value="" data-display="">-- select a voice --</option>`);
  }

  for (const v of voices) {
    if (v.voiceId === entry.voiceId) continue; // already added above as the selected option
    const label = `${v.displayName} [${v.voiceId}]`;
    options.push(`<option value="${escapeAttr(v.voiceId)}" data-display="${escapeAttr(v.displayName)}">${escapeHtml(label)}</option>`);
  }

  selectEl.innerHTML = options.join("");
}

function selectRow(tr) {
  document.querySelectorAll("#voice-map-body tr.selected").forEach((r) => r.classList.remove("selected"));
  tr.classList.add("selected");
}

function addVoiceMapRow() {
  voiceMap.push({ voiceType: "", providerId: providers[0]?.id || "", voiceId: "", voiceDisplay: "" });
  renderVoiceMapTable();
}

function removeSelectedRows() {
  const selected = document.querySelectorAll("#voice-map-body tr.selected");
  const indices = new Set([...selected].map((tr) => Number(tr.dataset.index)));
  voiceMap = voiceMap.filter((_, i) => !indices.has(i));
  renderVoiceMapTable();
}

async function saveVoiceMapping() {
  const entries = voiceMap.filter((e) => e.voiceType.trim());
  const result = await call("saveVoiceMap", { entries });
  appendLog(`Voice mapping saved (${result.count} row(s)).`, "ok");
}

async function exportVoiceMapCsv() {
  const path = await call("browseSaveFile", {
    title: "Export Voice Mapping",
    filterName: "CSV files",
    filterExtensions: ["csv"],
    defaultFileName: "voice_map.csv"
  });
  if (!path) return;
  const entries = voiceMap.filter((e) => e.voiceType.trim());
  const result = await call("exportVoiceMapCsv", { path, entries });
  appendLog(`Voice mapping exported to ${path} (${result.count} row(s)).`, "ok");
}

async function importVoiceMapCsv() {
  const path = await call("browseOpenFile", { title: "Import Voice Mapping", filterName: "CSV files", filterExtensions: ["csv"] });
  if (!path) return;

  const { entries } = await call("importVoiceMapCsv", { path });
  let imported = 0, updated = 0;

  for (const e of entries) {
    const existing = voiceMap.find((v) => v.voiceType.toLowerCase() === e.voiceType.toLowerCase());
    if (existing) {
      Object.assign(existing, e);
      updated++;
    } else {
      voiceMap.push(e);
      imported++;
    }
  }

  renderVoiceMapTable();
  appendLog(`Voice mapping imported from ${path}: ${imported} new, ${updated} updated.`, "ok");
  if (imported + updated > 0) await saveVoiceMapping();
}

// ==========================================================================
// Voices
// ==========================================================================

async function refreshAllVoices() {
  const keys = collectProviderKeysFromForm();
  providerKeys = keys;

  const { voicesByProvider: results, errors } = await call("refreshAllVoices", { providerKeys: keys });
  voicesByProvider = results;

  let total = 0;
  for (const id in voicesByProvider) total += voicesByProvider[id].length;
  document.getElementById("voice-count-badge").textContent =
    `${total} voice(s) loaded across ${Object.keys(voicesByProvider).length} provider(s)`;

  renderVoiceMapTable();
  refreshSingleLineDropdowns();

  const errorMessages = Object.entries(errors);
  if (errorMessages.length > 0) {
    appendLog("Some providers failed to load voices:", "error");
    for (const [id, msg] of errorMessages) appendLog(`  ${providerById(id)?.displayName || id}: ${msg}`, "error");
  }
}

// ==========================================================================
// Single-line generate
// ==========================================================================

function refreshSingleLineDropdowns() {
  const providerId = document.getElementById("single-provider").value;
  const provider = providerById(providerId);
  const modelSelect = document.getElementById("single-model");
  const voiceSelect = document.getElementById("single-voice");

  modelSelect.innerHTML = provider ? provider.models.map((m) => `<option value="${m}">${m}</option>`).join("") : "";

  const voices = voicesByProvider[providerId] || [];
  voiceSelect.innerHTML = voices.length
    ? voices.map((v) => `<option value="${v.voiceId}" data-display="${escapeAttr(v.displayName)}">${escapeHtml(v.displayName)} [${v.voiceId}]</option>`).join("")
    : `<option value="">-- click "Get Available Voices" above (local providers: make a voice on the Local Voices page) --</option>`;
}

async function generateSingleLine() {
  const providerId = document.getElementById("single-provider").value;
  const provider = providerById(providerId);
  if (!provider) return appendLog("Select a provider first.", "error");

  const text = document.getElementById("single-text").value.trim();
  if (!text) return appendLog("Enter some text first.", "error");

  const voiceSelect = document.getElementById("single-voice");
  const voiceId = voiceSelect.value;
  if (!voiceId) return appendLog(provider.requiresApiKey === false ? "Select a voice first (make one on the Local Voices page)." : "Select a voice first (Get Available Voices).", "error");

  const outputPath = document.getElementById("single-output-path").value.trim();
  if (!outputPath) return appendLog("Select an output path first.", "error");

  const keys = collectProviderKeysFromForm();
  const creds = keys[providerId] || { apiKey: "", groupId: "" };
  if (provider.requiresApiKey !== false && !creds.apiKey) return appendLog(`No API key configured for ${provider.displayName}.`, "error");

  const modelId = document.getElementById("single-model").value || provider.defaultModel;
  const fileName = document.getElementById("single-filename").value.trim() || "line1.wav";

  try {
    appendLog(`Generating via ${provider.displayName}: "${truncate(text, 60)}" -> ${outputPath}\\${fileName}`);
    const result = await call("generateSingleLine", {
      providerId, modelId, voiceId,
      apiKey: creds.apiKey, groupId: creds.groupId,
      text, outputPath, fileName
    });
    appendLog(`Done: ${result.path}`, "ok");
  } catch (err) {
    appendLog(`Failed: ${err.message}`, "error");
  }
}

// ==========================================================================
// CSV batch generate
// ==========================================================================

function updateFuzCheckboxState() {
  const lipChecked = document.getElementById("batch-lip").checked;
  const fuzCheckbox = document.getElementById("batch-fuz");
  const fuzRow = document.getElementById("batch-fuz-row");
  fuzCheckbox.disabled = !lipChecked;
  fuzRow.classList.toggle("disabled", !lipChecked);
  if (!lipChecked) fuzCheckbox.checked = false;
}

async function startBatch() {
  if (batchRunning) return;

  const csvPath = document.getElementById("batch-csv-path").value.trim();
  const voiceFolder = document.getElementById("batch-voice-folder").value.trim();
  const generateLip = document.getElementById("batch-lip").checked;
  const generateFuz = document.getElementById("batch-fuz").checked;
  const faceFxWrapperPath = document.getElementById("facefx-path").value.trim();
  const fonixDataPath = document.getElementById("fonixdata-path").value.trim();
  const xwmaEncodePath = document.getElementById("xwma-path").value.trim();
  const lipFuzerPath = document.getElementById("lipfuzer-path").value.trim();

  if (!csvPath) return alert("Select a valid CSV file first.");
  if (!voiceFolder) return alert("Select the mod's Voice folder first.");
  if (generateLip && (!faceFxWrapperPath || !fonixDataPath))
    return alert("LIP generation is checked, but FaceFXWrapper.exe / FonixData.cdf aren't set in Settings.");
  if (generateFuz && !generateLip)
    return alert('FUZ generation requires "Generate .lip files" to also be checked (a .fuz needs both WAV and LIP).');
  if (generateFuz && (!xwmaEncodePath || !lipFuzerPath))
    return alert("FUZ generation is checked, but xWMAEncode.exe / LipFuzer.exe aren't set in Settings.");

  if (voiceMap.filter((e) => e.voiceType.trim()).length === 0) {
    if (!confirm("The Voice Mapping table is empty, so every line will fail. Continue anyway?")) return;
  }

  const providerKeysNow = collectProviderKeysFromForm();

  setBatchControlsEnabled(false);
  document.getElementById("batch-progress-fill").style.width = "0%";
  document.getElementById("batch-progress-label").textContent = "Progress: 0/0";

  const result = await call("startBatch", {
    csvPath, voiceFolder,
    concurrency: Number(document.getElementById("batch-concurrency").value) || 3,
    overwrite: document.getElementById("batch-overwrite").checked,
    generateLip, generateFuz,
    faceFxWrapperPath, fonixDataPath, xwmaEncodePath, lipFuzerPath,
    providerKeys: providerKeysNow,
    voiceMap: voiceMap.filter((e) => e.voiceType.trim())
  });

  if (!result.ok) {
    appendLog(`Could not start batch: ${result.error}`, "error");
    setBatchControlsEnabled(true);
  }
}

function setBatchControlsEnabled(enabled) {
  batchRunning = !enabled;
  document.getElementById("start-batch-btn").disabled = !enabled;
  document.getElementById("pause-batch-btn").disabled = enabled;
  document.getElementById("stop-batch-btn").disabled = enabled;
  if (enabled) {
    batchPaused = false;
    document.getElementById("pause-batch-btn").textContent = "Pause";
  }
}

async function togglePauseBatch() {
  const result = await call("pauseBatch");
  if (!result.ok) return;
  batchPaused = result.paused;
  document.getElementById("pause-batch-btn").textContent = batchPaused ? "Resume" : "Pause";
}

async function stopBatch() {
  await call("stopBatch");
}

// ==========================================================================
// Log panel
// ==========================================================================

function appendLog(message, cls) {
  const el = document.getElementById("log-output");
  const line = document.createElement("div");
  line.className = "log-line" + (cls ? ` ${cls}` : "");
  const time = new Date().toLocaleTimeString([], { hour12: false });
  line.textContent = `[${time}] ${message}`;
  el.appendChild(line);
  el.scrollTop = el.scrollHeight;
}

function saveLogToFile() {
  const text = [...document.querySelectorAll("#log-output .log-line")].map((l) => l.textContent).join("\n");
  const blob = new Blob([text], { type: "text/plain" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = "log.txt";
  a.click();
  URL.revokeObjectURL(url);
}

function clearLog() {
  document.getElementById("log-output").innerHTML = "";
}

// ==========================================================================
// Wiring
// ==========================================================================

function wireStaticHandlers() {
  wireSplash();
  wireSidebarNav();
  wireModals();
  wireLogResizer();
  wireExternalLinks();

  document.getElementById("show-keys-toggle").addEventListener("change", (e) => {
    const type = e.target.checked ? "text" : "password";
    document.querySelectorAll(".provider-key-input").forEach((input) => (input.type = type));
  });

  document.getElementById("save-settings-btn").addEventListener("click", saveAllSettings);

  document.getElementById("refresh-voices-btn").addEventListener("click", () =>
    withButtonBusy("refresh-voices-btn", refreshAllVoices));

  document.getElementById("add-row-btn").addEventListener("click", addVoiceMapRow);
  document.getElementById("remove-row-btn").addEventListener("click", removeSelectedRows);
  document.getElementById("save-mapping-btn").addEventListener("click", saveVoiceMapping);
  document.getElementById("export-csv-btn").addEventListener("click", exportVoiceMapCsv);
  document.getElementById("import-csv-btn").addEventListener("click", importVoiceMapCsv);

  document.getElementById("single-provider").addEventListener("change", refreshSingleLineDropdowns);
  document.getElementById("generate-single-btn").addEventListener("click", () =>
    withButtonBusy("generate-single-btn", generateSingleLine));

  document.getElementById("batch-lip").addEventListener("change", updateFuzCheckboxState);
  document.getElementById("start-batch-btn").addEventListener("click", startBatch);
  document.getElementById("pause-batch-btn").addEventListener("click", togglePauseBatch);
  document.getElementById("stop-batch-btn").addEventListener("click", stopBatch);

  document.getElementById("save-log-btn").addEventListener("click", saveLogToFile);
  document.getElementById("clear-log-btn").addEventListener("click", clearLog);

  document.querySelectorAll("[data-browse-file]").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const targetId = btn.dataset.browseFile;
      const path = await call("browseOpenFile", {
        title: `Select ${btn.dataset.filterName || "file"}`,
        filterName: btn.dataset.filterName || "Files",
        filterExtensions: [btn.dataset.filterExt || "*"]
      });
      if (path) document.getElementById(targetId).value = path;
    });
  });

  document.querySelectorAll("[data-browse-folder]").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const targetId = btn.dataset.browseFolder;
      const path = await call("browseOpenFolder", { title: "Select folder" });
      if (path) document.getElementById(targetId).value = path;
    });
  });

  // Pushed events from C#
  on("log", (data) => appendLog(data.message));
  on("batchStarted", () => { /* handled by startBatch's own control state already */ });
  on("batchProgress", (p) => {
    const pct = p.total > 0 ? Math.min(100, Math.round((p.completed / p.total) * 100)) : 0;
    document.getElementById("batch-progress-fill").style.width = `${pct}%`;
    document.getElementById("batch-progress-label").textContent =
      `Progress: ${p.completed}/${p.total}   Skipped: ${p.skipped}   Failed: ${p.failed}`;
    if (p.currentId) appendLog(`[${p.completed}/${p.total}] ${p.currentId} -- ${p.message}`, p.message?.startsWith("FAILED") ? "error" : undefined);
    else if (p.message) appendLog(p.message);
  });
  on("batchDone", () => setBatchControlsEnabled(true));
}

async function saveAllSettings() {
  const providerKeysNow = collectProviderKeysFromForm();
  await call("saveSettings", {
    providerKeys: providerKeysNow,
    faceFxWrapperPath: document.getElementById("facefx-path").value.trim(),
    fonixDataPath: document.getElementById("fonixdata-path").value.trim(),
    xwmaEncodePath: document.getElementById("xwma-path").value.trim(),
    lipFuzerPath: document.getElementById("lipfuzer-path").value.trim(),
    lastCsvPath: document.getElementById("batch-csv-path").value.trim(),
    lastVoiceFolderPath: document.getElementById("batch-voice-folder").value.trim(),
    concurrency: Number(document.getElementById("batch-concurrency").value) || 3,
    lipGenerationEnabled: document.getElementById("batch-lip").checked,
    fuzGenerationEnabled: document.getElementById("batch-fuz").checked,
    lastProviderId: document.getElementById("single-provider").value
  });
  appendLog("Settings saved.", "ok");
}

async function withButtonBusy(buttonId, fn) {
  const btn = document.getElementById(buttonId);
  const originalText = btn.textContent;
  btn.disabled = true;
  btn.textContent = "Working...";
  try {
    await fn();
  } catch (err) {
    appendLog(`Error: ${err.message}`, "error");
  } finally {
    btn.disabled = false;
    btn.textContent = originalText;
  }
}

// ==========================================================================
// Splash screen
// ==========================================================================

function wireSplash() {
  document.getElementById("splash-continue-btn").addEventListener("click", async () => {
    document.getElementById("splash-overlay").classList.add("hidden");
    try { await call("markSplashSeen"); } catch { /* non-critical */ }
  });
}

// ==========================================================================
// Sidebar navigation (page switching + collapse)
// ==========================================================================

function wireSidebarNav() {
  document.querySelectorAll(".nav-item").forEach((btn) => {
    btn.addEventListener("click", () => {
      const pageId = btn.dataset.page;

      document.querySelectorAll(".nav-item").forEach((b) => b.classList.remove("active"));
      btn.classList.add("active");

      document.querySelectorAll(".page").forEach((p) => p.classList.remove("active"));
      document.getElementById(pageId).classList.add("active");
    });
  });

  document.getElementById("sidebar-toggle").addEventListener("click", () => {
    const sidebar = document.getElementById("sidebar");
    const collapsed = sidebar.classList.toggle("collapsed");
    document.getElementById("sidebar-toggle").title = collapsed ? "Expand" : "Collapse";
  });
}

// ==========================================================================
// Modals (Settings / Help / About)
// ==========================================================================

function wireModals() {
  document.querySelectorAll("[data-open-modal]").forEach((btn) => {
    btn.addEventListener("click", () => openModal(btn.dataset.openModal));
  });
  document.querySelectorAll("[data-close-modal]").forEach((btn) => {
    btn.addEventListener("click", () => closeModal(btn.dataset.closeModal));
  });
  document.querySelectorAll(".modal-overlay").forEach((overlay) => {
    overlay.addEventListener("click", (e) => {
      if (e.target === overlay) closeModal(overlay.id); // click on backdrop, not the modal card itself
    });
  });
  document.addEventListener("keydown", (e) => {
    if (e.key !== "Escape") return;
    document.querySelectorAll(".modal-overlay.open").forEach((overlay) => closeModal(overlay.id));
  });
}

function openModal(id) { document.getElementById(id).classList.add("open"); }
function closeModal(id) { document.getElementById(id).classList.remove("open"); }

// ==========================================================================
// Log panel resizer (drag to resize between content area and log panel)
// ==========================================================================

function wireLogResizer() {
  const resizer = document.getElementById("log-resizer");
  const logPanel = document.getElementById("log-panel");
  const workspace = document.querySelector(".workspace");
  let dragging = false;

  resizer.addEventListener("mousedown", (e) => {
    dragging = true;
    resizer.classList.add("dragging");
    document.body.style.userSelect = "none";
    e.preventDefault();
  });

  window.addEventListener("mousemove", (e) => {
    if (!dragging) return;
    const workspaceRect = workspace.getBoundingClientRect();
    let newLogWidth = workspaceRect.right - e.clientX;
    const min = 280, max = workspaceRect.width - 340; // keep both sides usable
    newLogWidth = Math.max(min, Math.min(max, newLogWidth));
    logPanel.style.width = `${newLogWidth}px`;
  });

  window.addEventListener("mouseup", () => {
    if (!dragging) return;
    dragging = false;
    resizer.classList.remove("dragging");
    document.body.style.userSelect = "";
  });
}

// ==========================================================================
// External links (open in the OS default browser, not the app's own WebView)
// ==========================================================================

function wireExternalLinks() {
  document.getElementById("patreon-btn").addEventListener("click", () => {
    call("openExternalUrl", { url: "https://www.patreon.com/cw/winterishclover" });
  });
}

// ==========================================================================
// Helpers
// ==========================================================================

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
function escapeAttr(s) { return escapeHtml(s ?? ""); }
function truncate(s, max) { return s.length <= max ? s : s.slice(0, max) + "..."; }

init().catch((err) => {
  console.error(err);
  appendLog(`Startup error: ${err.message}`, "error");
});
