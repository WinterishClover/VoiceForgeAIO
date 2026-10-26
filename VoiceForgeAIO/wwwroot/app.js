import { call, on } from "./bridge.js";
import { initLocalVoices } from "./local-voices.js";
import * as pluginPage from "./plugin-dialogue.js";

// ==========================================================================
// State
// ==========================================================================

let providers = [];          // [{id, displayName, models, defaultModel, supportsWav, requiresApiKey}]
let providerKeys = {};       // {providerId: {apiKey, groupId}}
let voiceMap = [];           // [{voiceType, providerId, voiceId, voiceDisplay}]
let voicesByProvider = {};   // {providerId: [{voiceId, displayName, description}]}
let batchRunning = false;
let batchPaused = false;
let defaultModels = {};      // {providerId: model} chosen in Settings; used when a mapping row has no model of its own
let selectedRows = new Set(); // indices of selected Voice Mapping rows
let lastClickedRow = -1;
let mappingDirty = false;     // edited since the last Save Mapping

const providerById = (id) => providers.find((p) => p.id === id);

/** The model a provider runs when nothing more specific is chosen: the Settings choice, else the provider's built-in default. */
function effectiveDefaultModel(providerId) {
  const p = providerById(providerId);
  if (!p) return "";
  return p.models.includes(defaultModels[providerId]) ? defaultModels[providerId] : p.defaultModel;
}

// ==========================================================================
// Init
// ==========================================================================

async function init() {
  wireStaticHandlers();

  const state = await call("getInitialState");
  // A returning user shouldn't see the welcome card flash while the rest of start-up runs.
  if (state.hasSeenSplash) document.getElementById("splash-overlay").classList.add("hidden");
  providers = Array.from(state.providers);
  providerKeys = state.providerKeys;
  voiceMap = Array.from(state.voiceMap);
  defaultModels = { ...(state.settings.defaultModels || {}) };
  // Voices fetched on an earlier run, so mappings are usable straight away (Get Available Voices refreshes them).
  voicesByProvider = Object.fromEntries(Object.entries(state.cachedVoices || {}).map(([id, list]) => [id, Array.from(list)]));

  renderProviderKeyRows();
  renderDefaultModelRows();
  renderProviderSelect(document.getElementById("single-provider"));
  applySettingsToForm(state.settings);
  renderVoiceMapTable();
  refreshSingleLineDropdowns();

  wirePluginDialogue();
  pluginPage.setFolders(state.settings.skyrimDataPath, state.settings.extraPluginFolder);
  // Find Skyrim and list its plugins in the background; the page is usable straight away.
  pluginPage.startUp().catch((err) => appendLog(`Plugin list: ${err.message}`, "error"));

  // Local providers (OmniVoice, Qwen3-TTS) list the voices made on the Local Voices page.
  await initLocalVoices(onLocalVoicesChanged).catch((err) => appendLog(`Local voices: ${err.message}`, "error"));

  updateVoiceBadge(Object.keys(state.cachedVoices || {}).length > 0);
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
        <div class="field-title"><label class="field-label">${p.displayName}</label><em class="key-status"></em></div>
        <input type="password" class="provider-key-input" data-provider="${p.id}" data-field="apiKey" value="${escapeAttr(keys.apiKey)}" placeholder="Paste your ${escapeAttr(p.displayName)} API key" autocomplete="off" />
      </div>`;
    const keyInput = row.querySelector("input");
    const keyStatus = row.querySelector(".key-status");
    const showKeyStatus = () => {
      const has = keyInput.value.trim().length > 0;
      keyStatus.textContent = has ? "Key entered" : "Not set";
      keyStatus.classList.toggle("configured", has);
    };
    keyInput.addEventListener("input", showKeyStatus);
    showKeyStatus();
    container.appendChild(row);

    if (p.id === "MiniMax") {
      const groupRow = document.createElement("div");
      groupRow.className = "field-row";
      groupRow.innerHTML = `
        <div class="grow">
          <div class="field-title"><label class="field-label">MiniMax Group ID</label></div>
          <input type="password" class="provider-key-input" data-provider="MiniMax" data-field="groupId" value="${escapeAttr(keys.groupId)}" placeholder="From your MiniMax dashboard" autocomplete="off" />
        </div>`;
      container.appendChild(groupRow);
    }
  }
}

/** Settings: one model drop-down per provider that offers more than one. */
function renderDefaultModelRows() {
  const container = document.getElementById("default-models");
  container.innerHTML = "";
  for (const p of providers.filter((x) => x.models.length > 1)) {
    const row = document.createElement("div");
    row.className = "field-row";
    row.innerHTML = `
      <div class="grow">
        <div class="field-title"><label class="field-label" for="default-model-${escapeAttr(p.id)}">${escapeHtml(p.displayName)}</label><em class="key-status">Built-in default: ${escapeHtml(p.defaultModel)}</em></div>
        <select id="default-model-${escapeAttr(p.id)}" data-provider="${escapeAttr(p.id)}">
          ${p.models.map((m) => `<option value="${escapeAttr(m)}">${escapeHtml(m)}</option>`).join("")}
        </select>
      </div>`;
    const select = row.querySelector("select");
    select.value = effectiveDefaultModel(p.id);
    select.addEventListener("change", () => {
      defaultModels[p.id] = select.value;
      renderVoiceMapTable();        // the "Default - model" labels follow
      refreshSingleLineDropdowns(); // Single Line starts on it
      if (document.getElementById("single-provider").value === p.id) document.getElementById("single-model").value = select.value;
    });
    container.appendChild(row);
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

const GUID_LIKE = /^[0-9a-f]{32}$/i;

/** "Name [id]" for provider voices; local voices have an opaque generated id, so only the name is shown. */
function voiceLabel(name, id) {
  if (!name) return id ? `[${id}]` : "";
  return GUID_LIKE.test(id) ? name : `${name} [${id}]`;
}

let statusTimer = null;
/** The Plugin Dialogue page shows each VoiceType's mapping; keep it current as the table changes. */
function refreshPluginStatusSoon() {
  clearTimeout(statusTimer);
  statusTimer = setTimeout(() => pluginPage.refreshMappingStatus(), 200);
}

function markDirty() {
  mappingDirty = true;
  document.getElementById("save-mapping-btn").textContent = "Save Mapping *";
  refreshPluginStatusSoon();
}

function markClean() {
  mappingDirty = false;
  document.getElementById("save-mapping-btn").textContent = "Save Mapping";
  refreshPluginStatusSoon();
}

function updateVoiceBadge(fromCache = false) {
  let total = 0;
  for (const id in voicesByProvider) total += voicesByProvider[id].length;
  const providersWithVoices = Object.keys(voicesByProvider).filter((id) => voicesByProvider[id].length > 0).length;
  const badge = document.getElementById("voice-count-badge");
  badge.textContent = total === 0
    ? "No voices loaded yet"
    : `${total} voice(s) across ${providersWithVoices} provider(s)${fromCache ? " (remembered from last time)" : ""}`;
}

function renderVoiceMapTable() {
  const tbody = document.getElementById("voice-map-body");
  tbody.innerHTML = "";
  voiceMap.forEach((entry, index) => tbody.appendChild(buildVoiceMapRow(entry, index)));
  applySelection();
}

/** Click selects a row; Ctrl/Cmd-click adds or removes one; Shift-click selects the range from the last click. */
function onRowClick(e, index) {
  if (e.shiftKey && lastClickedRow >= 0) {
    const [from, to] = [Math.min(lastClickedRow, index), Math.max(lastClickedRow, index)];
    selectedRows = new Set(Array.from({ length: to - from + 1 }, (_, i) => from + i));
  } else if (e.ctrlKey || e.metaKey) {
    if (!selectedRows.delete(index)) selectedRows.add(index);
    lastClickedRow = index;
  } else {
    selectedRows = new Set([index]);
    lastClickedRow = index;
  }
  applySelection();
}

function applySelection() {
  document.querySelectorAll("#voice-map-body tr").forEach((tr) =>
    tr.classList.toggle("selected", selectedRows.has(Number(tr.dataset.index))));
}

function buildVoiceMapRow(entry, index) {
  const tr = document.createElement("tr");
  tr.dataset.index = String(index);
  tr.addEventListener("click", (e) => onRowClick(e, index));

  const typeTd = document.createElement("td");
  const typeInput = document.createElement("input");
  typeInput.type = "text";
  typeInput.value = entry.voiceType;
  typeInput.placeholder = "e.g. Kaylin";
  typeInput.addEventListener("input", () => { voiceMap[index].voiceType = typeInput.value; markDirty(); });
  // Stray spaces would make the name never match the CSV's VoiceType column.
  typeInput.addEventListener("change", () => {
    typeInput.value = typeInput.value.trim();
    voiceMap[index].voiceType = typeInput.value;
  });
  typeTd.appendChild(typeInput);

  const providerTd = document.createElement("td");
  const providerSelect = document.createElement("select");
  const known = providers.some((p) => p.id === entry.providerId);
  providerSelect.innerHTML =
    (!known && entry.providerId ? `<option value="${escapeAttr(entry.providerId)}">${escapeHtml(entry.providerId)} (unknown provider)</option>` : "") +
    (!known && !entry.providerId ? `<option value="">-- select a provider --</option>` : "") +
    providers.map((p) => `<option value="${p.id}">${p.displayName}</option>`).join("");
  providerSelect.value = entry.providerId || (known ? entry.providerId : "");
  providerSelect.classList.toggle("warn", !known);
  providerSelect.addEventListener("change", () => {
    voiceMap[index].providerId = providerSelect.value;
    voiceMap[index].voiceId = "";
    voiceMap[index].voiceDisplay = "";
    voiceMap[index].modelId = ""; // models belong to a provider
    providerSelect.classList.toggle("warn", !providers.some((p) => p.id === providerSelect.value));
    rebuildModelSelect(modelSelect, voiceMap[index]);
    rebuildVoiceSelect(voiceSelect, voiceMap[index]);
    markDirty();
  });
  providerTd.appendChild(providerSelect);

  const modelTd = document.createElement("td");
  const modelSelect = document.createElement("select");
  rebuildModelSelect(modelSelect, voiceMap[index]);
  modelSelect.addEventListener("change", () => {
    voiceMap[index].modelId = modelSelect.value;
    modelSelect.classList.toggle("warn", !!modelSelect.value && !providerById(voiceMap[index].providerId)?.models.includes(modelSelect.value));
    markDirty();
  });
  modelTd.appendChild(modelSelect);

  const voiceTd = document.createElement("td");
  const voiceSelect = document.createElement("select");
  voiceSelect.addEventListener("change", () => {
    const opt = voiceSelect.selectedOptions[0];
    voiceMap[index].voiceId = voiceSelect.value;
    voiceMap[index].voiceDisplay = opt ? opt.dataset.display || "" : "";
    voiceSelect.classList.toggle("warn", !voiceSelect.value || voiceSelect.selectedOptions[0]?.dataset.missing === "1");
    markDirty();
  });
  rebuildVoiceSelect(voiceSelect, entry);
  voiceTd.appendChild(voiceSelect);

  tr.appendChild(typeTd);
  tr.appendChild(providerTd);
  tr.appendChild(modelTd);
  tr.appendChild(voiceTd);
  return tr;
}

/** The Model drop-down offers the models of the row's provider; blank means the provider's default. A saved model the provider no longer lists is kept and flagged. */
function rebuildModelSelect(selectEl, entry) {
  const provider = providerById(entry.providerId);
  const models = provider ? provider.models : [];
  const saved = entry.modelId || "";
  const unknown = saved && !models.includes(saved);
  selectEl.innerHTML =
    `<option value="">${provider ? `Default - ${escapeHtml(effectiveDefaultModel(entry.providerId))}` : "-"}</option>` +
    (unknown ? `<option value="${escapeAttr(saved)}">${escapeHtml(saved)} (not offered)</option>` : "") +
    models.map((m) => `<option value="${escapeAttr(m)}">${escapeHtml(m)}</option>`).join("");
  selectEl.value = saved;
  selectEl.classList.toggle("warn", !!unknown);
}

/** Populates a row's Voice <select> from the cached voice list for its current
 *  Provider, always including the row's current value even if it's not (yet)
 *  in that list, so a persisted mapping never displays blank. A saved voice that
 *  is no longer in a loaded list (deleted, or removed by the provider) is flagged. */
function rebuildVoiceSelect(selectEl, entry) {
  const voices = voicesByProvider[entry.providerId] || [];
  const options = [];
  let missing = false;

  if (entry.voiceId) {
    const known = voices.find((v) => v.voiceId === entry.voiceId);
    // A local provider's list is always complete (even when empty); a cloud list may simply not be loaded yet.
    missing = !known && (voices.length > 0 || providerById(entry.providerId)?.requiresApiKey === false);
    const name = known ? known.displayName : entry.voiceDisplay;
    const label = voiceLabel(name, entry.voiceId) + (missing ? "  (not found - pick another)" : "");
    options.push(`<option value="${escapeAttr(entry.voiceId)}" data-display="${escapeAttr(entry.voiceDisplay || (known ? known.displayName : ""))}" data-missing="${missing ? 1 : 0}" selected>${escapeHtml(label)}</option>`);
  } else {
    options.push(`<option value="" data-display="">-- select a voice --</option>`);
  }

  for (const v of voices) {
    if (v.voiceId === entry.voiceId) continue; // already added above as the selected option
    options.push(`<option value="${escapeAttr(v.voiceId)}" data-display="${escapeAttr(v.displayName)}">${escapeHtml(voiceLabel(v.displayName, v.voiceId))}</option>`);
  }

  selectEl.innerHTML = options.join("");
  selectEl.classList.toggle("warn", !entry.voiceId || missing);
}

function addVoiceMapRow() {
  voiceMap.push({ voiceType: "", providerId: providers[0]?.id || "", voiceId: "", voiceDisplay: "", modelId: "" });
  selectedRows = new Set([voiceMap.length - 1]);
  lastClickedRow = voiceMap.length - 1;
  renderVoiceMapTable();
  markDirty();
  // Ready to type the new VoiceType.
  const inputs = document.querySelectorAll("#voice-map-body tr input");
  inputs[inputs.length - 1]?.focus();
}

function removeSelectedRows() {
  if (selectedRows.size === 0) {
    appendLog("Select one or more rows first (click a row; Ctrl-click or Shift-click for several), then Remove Selected.", "error");
    return;
  }
  const count = selectedRows.size;
  voiceMap = voiceMap.filter((_, i) => !selectedRows.has(i));
  selectedRows = new Set();
  lastClickedRow = -1;
  renderVoiceMapTable();
  markDirty();
  appendLog(`Removed ${count} row(s). Click Save Mapping to keep the change.`);
}

/** Names that differ only by letter case are the same VoiceType to the batch, so they can't both be mapped. */
function findDuplicateVoiceTypes(entries) {
  const seen = new Map();
  const dupes = new Set();
  for (const e of entries) {
    const key = e.voiceType.trim().toLowerCase();
    if (seen.has(key)) dupes.add(seen.get(key)); else seen.set(key, e.voiceType.trim());
  }
  return [...dupes];
}

/** Logs rows that will fail in a batch. Returns the number of problems. */
function reportMappingProblems(entries) {
  let problems = 0;
  for (const e of entries) {
    const name = e.voiceType.trim();
    if (!providers.some((p) => p.id === e.providerId)) { appendLog(`Warning: '${name}' has no valid provider selected.`, "error"); problems++; }
    else if (!e.voiceId) { appendLog(`Warning: '${name}' has no voice selected; its lines will fail.`, "error"); problems++; }
    else if (e.modelId && !providers.find((p) => p.id === e.providerId).models.includes(e.modelId)) { appendLog(`Warning: '${name}' uses model '${e.modelId}', which its provider no longer offers; the provider's default model will be used.`, "error"); problems++; }
  }
  return problems;
}

async function saveVoiceMapping() {
  for (const e of voiceMap) e.voiceType = e.voiceType.trim();
  const entries = voiceMap.filter((e) => e.voiceType);
  const dupes = findDuplicateVoiceTypes(entries);
  if (dupes.length > 0) {
    appendLog(`Not saved: these VoiceTypes appear more than once (letter case is ignored): ${dupes.join(", ")}. Remove or rename the extras.`, "error");
    return false;
  }
  const result = await call("saveVoiceMap", { entries });
  markClean();
  const problems = reportMappingProblems(entries);
  appendLog(`Voice mapping saved (${result.count} row(s))${problems ? `, ${problems} need attention (marked in orange)` : ""}.`, problems ? undefined : "ok");
  return true;
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

  const { entries, warnings } = await call("importVoiceMapCsv", { path });
  let imported = 0, updated = 0;

  for (const e of entries) {
    // An older file has no Provider column: if exactly one loaded provider offers that voice id, that's the provider.
    if (e.needsProvider && !e.providerId) {
      const owners = Object.keys(voicesByProvider).filter((id) => voicesByProvider[id].some((v) => v.voiceId === e.voiceId));
      if (owners.length === 1) e.providerId = owners[0];
    }
    delete e.needsProvider;
    e.modelId = e.modelId || "";
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
  for (const w of warnings || []) appendLog(w, "error");
  if (imported + updated > 0) {
    markDirty();
    await saveVoiceMapping();
  }
}

// ==========================================================================
// Voices
// ==========================================================================

async function refreshAllVoices(onlyProviderId) {
  const keys = collectProviderKeysFromForm();
  providerKeys = keys;

  const { voicesByProvider: results, errors } = await call("refreshAllVoices", { providerKeys: keys, providerId: onlyProviderId || null });
  // Only providers that answered are replaced: one that failed (bad key, offline) keeps the list it had.
  for (const id in results) voicesByProvider[id] = Array.from(results[id]);
  updateVoiceBadge();

  renderVoiceMapTable();
  refreshSingleLineDropdowns();

  const errorMessages = Object.entries(errors);
  if (errorMessages.length > 0) {
    appendLog("Some providers failed to load voices (their previous voice lists were kept):", "error");
    for (const [id, msg] of errorMessages) appendLog(`  ${providerById(id)?.displayName || id}: ${msg}`, "error");
  }
  return { results, errors };
}

// ==========================================================================
// Single-line generate
// ==========================================================================

function refreshSingleLineDropdowns() {
  const providerId = document.getElementById("single-provider").value;
  const provider = providerById(providerId);
  const modelSelect = document.getElementById("single-model");
  const voiceSelect = document.getElementById("single-voice");

  // Rebuilding the lists must not undo what the user already picked.
  const keepModel = modelSelect.value, keepVoice = voiceSelect.value;

  modelSelect.innerHTML = provider ? provider.models.map((m) => `<option value="${m}">${m}</option>`).join("") : "";
  if (provider) modelSelect.value = provider.models.includes(keepModel) ? keepModel : effectiveDefaultModel(providerId);

  const voices = voicesByProvider[providerId] || [];
  voiceSelect.innerHTML = voices.length
    ? voices.map((v) => `<option value="${escapeAttr(v.voiceId)}" data-display="${escapeAttr(v.displayName)}">${escapeHtml(voiceLabel(v.displayName, v.voiceId))}</option>`).join("")
    : `<option value="">${provider && provider.requiresApiKey === false ? "-- make a voice on the Local Voices page --" : "-- click Load voices --"}</option>`;
  if (voices.some((v) => v.voiceId === keepVoice)) voiceSelect.value = keepVoice;
}

async function generateSingleLine() {
  const providerId = document.getElementById("single-provider").value;
  const provider = providerById(providerId);
  if (!provider) return appendLog("Select a provider first.", "error");

  const text = document.getElementById("single-text").value.trim();
  if (!text) return appendLog("Enter some text first.", "error");

  const voiceSelect = document.getElementById("single-voice");
  const voiceId = voiceSelect.value;
  if (!voiceId) return appendLog(provider.requiresApiKey === false ? "Select a voice first (make one on the Local Voices page)." : "Select a voice first (click Load voices).", "error");

  const outputPath = document.getElementById("single-output-path").value.trim(); // blank = the default samples folder

  const keys = collectProviderKeysFromForm();
  const creds = keys[providerId] || { apiKey: "", groupId: "" };
  if (provider.requiresApiKey !== false && !creds.apiKey) return appendLog(`No API key configured for ${provider.displayName}.`, "error");

  const modelId = document.getElementById("single-model").value || provider.defaultModel;
  const fileName = document.getElementById("single-filename").value.trim() || "line1.wav";

  try {
    appendLog(`Generating via ${provider.displayName}: "${truncate(text, 60)}" ...`);
    const result = await call("generateSingleLine", {
      providerId, modelId, voiceId,
      apiKey: creds.apiKey, groupId: creds.groupId,
      text, outputPath, fileName
    });
    appendLog(`Done: ${result.path}`, "ok");
    showSingleResult(result);
  } catch (err) {
    appendLog(`Failed: ${err.message}`, "error");
  }
}

let lastSinglePath = "";

/** Shows the generated line in a player and plays it, so there's no hunting for the file. */
function showSingleResult(result) {
  lastSinglePath = result.path;
  const audio = document.getElementById("single-audio");
  document.getElementById("single-result").classList.remove("hidden");
  document.getElementById("single-result-path").textContent = result.path;
  if (result.audioData) {
    audio.src = "data:audio/wav;base64," + result.audioData;
    audio.play().catch(() => { /* autoplay can be blocked; the controls still work */ });
  } else {
    audio.removeAttribute("src");
    appendLog("The file is too large to preview here; open it from its folder.", "error");
  }
  document.getElementById("single-result").scrollIntoView({ behavior: "smooth", block: "nearest" });
}

/** "Load voices" on Single Line: fetch just the selected provider's voices (local providers just re-list theirs). */
async function loadSingleLineVoices() {
  const providerId = document.getElementById("single-provider").value;
  const provider = providerById(providerId);
  if (!provider) return;
  appendLog(`Loading ${provider.displayName} voices...`);
  const { results, errors } = await refreshAllVoices(providerId);
  if (errors[providerId]) return;
  appendLog(`${provider.displayName}: ${(results[providerId] || []).length} voice(s) available.`, "ok");
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

  const fromPlugin = document.getElementById("batch-source").value === "plugin";
  const csvPath = document.getElementById("batch-csv-path").value.trim();
  const voiceFolder = document.getElementById("batch-voice-folder").value.trim();
  const generateLip = document.getElementById("batch-lip").checked;
  const generateFuz = document.getElementById("batch-fuz").checked;
  const faceFxWrapperPath = document.getElementById("facefx-path").value.trim();
  const fonixDataPath = document.getElementById("fonixdata-path").value.trim();
  const xwmaEncodePath = document.getElementById("xwma-path").value.trim();
  const lipFuzerPath = document.getElementById("lipfuzer-path").value.trim();

  if (fromPlugin && !pluginPage.isLoaded()) return alert("Read a plugin on the Plugin Dialogue page first.");
  if (!fromPlugin && !csvPath) return alert("Select a valid CSV file first.");
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

  // The batch uses the table as shown; make sure that is also what is saved.
  if (mappingDirty && !(await saveVoiceMapping())) return;

  const providerKeysNow = collectProviderKeysFromForm();

  setBatchControlsEnabled(false);
  document.getElementById("batch-progress-fill").style.width = "0%";
  document.getElementById("batch-progress-label").textContent = "Progress: 0/0";

  const result = await call("startBatch", {
    csvPath, voiceFolder,
    source: fromPlugin ? "plugin" : "csv",
    includeVoiceTypes: fromPlugin ? pluginPage.includedVoiceTypes() : null,
    defaultModels,
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

/** The sidebar footer shows at a glance whether a batch is running. */
function setStatus(text, busy = false) {
  document.getElementById("status-text").textContent = text;
  document.getElementById("status-dot").classList.toggle("busy", busy);
  document.getElementById("status-dot").classList.toggle("ready", !busy);
}

function setBatchControlsEnabled(enabled) {
  batchRunning = !enabled;
  setStatus(enabled ? "Ready" : "Generating...", !enabled);
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
// Plugin Dialogue <-> Voice Mapping / CSV Batch
// ==========================================================================

function goToPage(pageId) {
  document.querySelector(`.nav-item[data-page="${pageId}"]`)?.click();
}

function wirePluginDialogue() {
  pluginPage.initPluginDialogue({
    getVoiceMap: () => voiceMap,
    getProviders: () => providers,
    appendLog,
    goToPage,
    persistSettings: () => saveAllSettings(true).catch(() => {}),
    addVoiceTypes(names) {
      // New rows start on the provider used by the last row, since a mod usually uses one provider for most of its voices.
      const providerId = voiceMap.length ? voiceMap[voiceMap.length - 1].providerId : (providers[0]?.id || "");
      for (const name of names) voiceMap.push({ voiceType: name, providerId, voiceId: "", voiceDisplay: "", modelId: "" });
      renderVoiceMapTable();
      markDirty();
    },
    onPluginLoaded() {
      document.getElementById("batch-source-plugin-option").disabled = false;
      // Offer the folder the game reads the voice files from, unless the user has already chosen one for this mod.
      const folder = pluginPage.suggestedVoiceFolder();
      const field = document.getElementById("batch-voice-folder");
      if (folder && (!field.value.trim() || /[\\/]Sound[\\/]Voice[\\/][^\\/]+\.(esp|esm|esl)$/i.test(field.value.trim()))) field.value = folder;
      updateBatchSource();
      pluginPage.refreshMappingStatus();
    },
    onPluginSelectionChanged: updateBatchSource,
    useForBatch() {
      document.getElementById("batch-source").value = "plugin";
      updateBatchSource();
      goToPage("batch-page");
    }
  });
}

/** Shows the CSV box or the plugin summary, depending on where the batch's lines come from. */
function updateBatchSource() {
  const select = document.getElementById("batch-source");
  if (select.value === "plugin" && !pluginPage.isLoaded()) select.value = "csv";
  const fromPlugin = select.value === "plugin";
  document.getElementById("batch-csv-row").classList.toggle("hidden", fromPlugin);
  document.getElementById("batch-csv-format-hint").classList.toggle("hidden", fromPlugin);
  const info = document.getElementById("batch-plugin-info");
  info.classList.toggle("hidden", !fromPlugin);
  if (fromPlugin)
    info.textContent = `${pluginPage.pluginName()}: ${pluginPage.selectedLineCount().toLocaleString()} line(s) from the VoiceTypes ticked on the Plugin Dialogue page. ` +
      `Finished files go to Mod Voice Folder below (the game reads Data\\Sound\\Voice\\${pluginPage.pluginName()}\\<VoiceType>\\).`;
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

  document.getElementById("save-settings-btn").addEventListener("click", () => saveAllSettings());
  document.getElementById("batch-source").addEventListener("change", updateBatchSource);

  document.getElementById("refresh-voices-btn").addEventListener("click", () =>
    withButtonBusy("refresh-voices-btn", refreshAllVoices));

  document.getElementById("add-row-btn").addEventListener("click", addVoiceMapRow);
  document.getElementById("remove-row-btn").addEventListener("click", removeSelectedRows);
  document.getElementById("save-mapping-btn").addEventListener("click", saveVoiceMapping);
  document.getElementById("export-csv-btn").addEventListener("click", exportVoiceMapCsv);
  document.getElementById("import-csv-btn").addEventListener("click", importVoiceMapCsv);

  document.getElementById("single-provider").addEventListener("change", refreshSingleLineDropdowns);
  document.getElementById("single-load-voices-btn").addEventListener("click", () => withButtonBusy("single-load-voices-btn", loadSingleLineVoices));
  document.getElementById("single-play-btn").addEventListener("click", () => {
    const audio = document.getElementById("single-audio");
    audio.currentTime = 0;
    audio.play().catch(() => {});
  });
  document.getElementById("single-show-btn").addEventListener("click", async () => {
    const r = await call("showInFolder", { path: lastSinglePath });
    if (!r.ok) appendLog(`Could not open the folder: ${r.error}`, "error");
  });
  document.getElementById("generate-single-btn").addEventListener("click", () =>
    withButtonBusy("generate-single-btn", generateSingleLine));

  document.getElementById("batch-lip").addEventListener("change", updateFuzCheckboxState);
  document.getElementById("start-batch-btn").addEventListener("click", startBatch);
  document.getElementById("pause-batch-btn").addEventListener("click", togglePauseBatch);
  document.getElementById("stop-batch-btn").addEventListener("click", stopBatch);

  document.getElementById("log-toggle").addEventListener("click", () => {
    const panel = document.getElementById("log-panel");
    const collapsed = panel.classList.toggle("collapsed");
    document.getElementById("log-toggle").textContent = collapsed ? "Show" : "Hide";
    document.getElementById("log-resizer").classList.toggle("disabled", collapsed);
  });
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
    setStatus(`Generating ${p.completed}/${p.total}`, true);
    if (p.currentId) appendLog(`[${p.completed}/${p.total}] ${p.currentId} -- ${p.message}`, p.message?.startsWith("FAILED") ? "error" : undefined);
    else if (p.message) appendLog(p.message);
  });
  on("batchDone", () => setBatchControlsEnabled(true));
}

async function saveAllSettings(silent = false) {
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
    lastProviderId: document.getElementById("single-provider").value,
    defaultModels,
    skyrimDataPath: pluginPage.dataFolder(),
    extraPluginFolder: pluginPage.extraFolder()
  });
  if (!silent) appendLog("Settings saved.", "ok");
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
  document.querySelectorAll(".nav-item[data-page]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const pageId = btn.dataset.page;

      document.querySelectorAll(".nav-item").forEach((b) => b.classList.remove("active"));
      btn.classList.add("active");

      document.querySelectorAll(".page").forEach((p) => p.classList.remove("active"));
      document.getElementById(pageId).classList.add("active");
      document.getElementById("content-area").scrollTo(0, 0);
    });
  });

  document.getElementById("sidebar-toggle").addEventListener("click", () => {
    const collapsed = document.getElementById("app").classList.toggle("sidebar-collapsed");
    const toggle = document.getElementById("sidebar-toggle");
    toggle.title = collapsed ? "Expand sidebar" : "Collapse sidebar";
    toggle.innerHTML = collapsed ? "&#8250;" : "&#8249;";
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
  const main = document.querySelector(".main");
  let dragging = false;

  resizer.addEventListener("mousedown", (e) => {
    dragging = true;
    resizer.classList.add("dragging");
    document.body.style.userSelect = "none";
    document.body.style.cursor = "row-resize";
    e.preventDefault();
  });

  window.addEventListener("mousemove", (e) => {
    if (!dragging) return;
    const rect = main.getBoundingClientRect();
    const min = 90, max = rect.height - 240; // keep the page above usable
    logPanel.style.height = `${Math.max(min, Math.min(max, rect.bottom - e.clientY))}px`;
  });

  window.addEventListener("mouseup", () => {
    if (!dragging) return;
    dragging = false;
    resizer.classList.remove("dragging");
    document.body.style.userSelect = "";
    document.body.style.cursor = "";
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
