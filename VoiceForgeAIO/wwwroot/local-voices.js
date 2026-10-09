import { call } from "./bridge.js";

// Local Voices page: set up OmniVoice and Qwen3-TTS (free text-to-speech that runs on this
// computer) and make the voices a Voice Mapping row can use. Each provider keeps its own
// settings and voices in %AppData%\VoiceForgeAIO; the page just edits them over the bridge.

const $ = (id) => document.getElementById(id);
const el = (tag, text) => { const e = document.createElement(tag); if (text !== undefined) e.textContent = text; return e; };
const option = (value, text) => { const o = el("option", text); o.value = value; return o; };

const EMOTIONS = ["", "happy", "excited", "angry", "sad", "tired", "calm", "scared", "sarcastic", "suspicious", "proud", "tender"];

let onVoicesChanged = () => {};

/** Builds the controller for one local provider card. `p` holds its element-id prefix and bridge method prefix. */
function createCard(p) {
  const id = (name) => $(p.prefix + name);
  let state = { settings: {}, voices: [], configured: false, running: false };
  let currentId = "";
  let audio = null;
  let busy = false;

  const current = () => state.voices.find((v) => v.id === currentId);

  function say(messageId, text, isError = false) {
    const box = $(messageId);
    box.textContent = text;
    box.className = "local-message" + (text ? (isError ? " error" : " success") : "");
  }

  async function guard(button, messageId, work) {
    if (busy) return;
    busy = true;
    if (button) button.disabled = true;
    try { await work(); } catch (e) { say(messageId, e.message, true); } finally { busy = false; if (button) button.disabled = false; }
  }

  function renderSetup() {
    const s = state.settings || {};
    id("Python").value = s.pythonPath || "";
    id("Wsl").checked = !!s.useWsl;
    id("Distro").value = s.wslDistro || "";
    id("Idle").value = String(s.idleMinutes || 10);
    id("State").textContent = state.configured ? (state.running ? "· loaded" : "· set up") : "· not set up yet";
    p.renderLists?.(state);
  }

  function renderVoices() {
    const list = state.voices || [];
    if (!list.some((v) => v.id === currentId)) currentId = list[0]?.id || "";
    id("VoiceSelect").replaceChildren(...list.map((v) => option(v.id, v.name || "(unnamed)")));
    id("VoiceSelect").value = currentId;
    p.fillForm(current() || p.blank);
    showKind();
  }

  function showKind() { p.showKind(id("VoiceKind").value); }

  function apply(next) {
    state = next;
    renderSetup();
    renderVoices();
    // Hand the voice list to the rest of the app, so Voice Mapping and Single-Line offer them without a refresh.
    onVoicesChanged(p.providerId, state.voices.map((v) => ({ voiceId: v.id, displayName: v.name, description: v.instruct || v.kind })));
  }

  async function load() { apply(await call(p.method + ".get")); }

  id("SaveButton").addEventListener("click", (e) => guard(e.currentTarget, id("Message").id, async () => {
    apply(await call(p.method + ".save", { settings: { ...state.settings, pythonPath: id("Python").value.trim(), useWsl: id("Wsl").checked, wslDistro: id("Distro").value.trim(), idleMinutes: Number(id("Idle").value) || 10 } }));
    say(id("Message").id, p.name + " settings saved.");
  }));

  id("CheckButton").addEventListener("click", (e) => guard(e.currentTarget, id("Message").id, async () => {
    say(id("Message").id, "Loading PyTorch and " + p.name + "… the first check can take a minute.");
    const result = await call(p.method + ".check");
    say(id("Message").id, p.name + " is ready. " + (typeof result.report === "string" ? result.report : JSON.stringify(result.report)));
  }));

  id("StopButton").addEventListener("click", (e) => guard(e.currentTarget, id("Message").id, async () => {
    apply(await call(p.method + ".stop"));
    say(id("Message").id, "The model is unloaded and the graphics card is free.");
  }));

  id("VoiceSelect").addEventListener("change", () => { currentId = id("VoiceSelect").value; renderVoices(); });
  id("VoiceKind").addEventListener("change", showKind);

  id("NewButton").addEventListener("click", () => {
    currentId = "";
    id("VoiceSelect").value = "";
    p.fillForm(p.blank);
    showKind();
    say(id("VoiceMessage").id, "Describe the new voice, then Save voice.");
  });

  id("VoiceSaveButton").addEventListener("click", (e) => guard(e.currentTarget, id("VoiceMessage").id, async () => {
    const voice = p.readForm(current() || {});
    if (!voice.name) throw new Error("Give the voice a name.");
    const before = new Set(state.voices.map((v) => v.id));
    const next = await call(p.method + ".voice.save", { voice });
    currentId = voice.id || next.voices.find((v) => !before.has(v.id))?.id || next.voices.find((v) => v.name === voice.name)?.id || "";
    apply(next);
    say(id("VoiceMessage").id, "Voice saved. Pick the provider " + p.display + " in Voice Mapping, then Get Available Voices.");
  }));

  id("VoiceDeleteButton").addEventListener("click", (e) => guard(e.currentTarget, id("VoiceMessage").id, async () => {
    const v = current();
    if (!v) return;
    if (!window.confirm("Delete the voice " + v.name + "? Mappings that use it will need another voice.")) return;
    apply(await call(p.method + ".voice.delete", { id: v.id }));
    say(id("VoiceMessage").id, "Voice deleted.");
  }));

  id("ReferenceButton").addEventListener("click", (e) => guard(e.currentTarget, id("VoiceMessage").id, async () => {
    const path = await call("browseOpenFile", { title: "Select a recording", filterName: "Audio files", filterExtensions: ["wav", "mp3", "flac", "ogg", "m4a"] });
    if (!path) return;
    const result = await call(p.method + ".reference", { path });
    id("VoiceReference").value = result.fileName;
    say(id("VoiceMessage").id, "Recording added. Save the voice to keep it.");
  }));

  id("SampleButton").addEventListener("click", (e) => guard(e.currentTarget, id("VoiceMessage").id, async () => {
    const voice = p.readForm(current() || {});
    p.checkSample(voice);
    say(id("VoiceMessage").id, "Making the sample… the first one loads the model and can take a minute or two.");
    const result = await call(p.method + ".sample", { voice, text: id("SampleText").value, ...p.sampleExtras() });
    audio?.pause?.();
    audio = new Audio("data:audio/wav;base64," + result.audioData);
    await audio.play();
    say(id("VoiceMessage").id, "Playing the sample. Cost: nothing.");
    state.running = true;
    id("State").textContent = "· loaded"; // the form keeps what is being edited
  }));

  $(p.prefix + "Section").addEventListener("toggle", () => {
    if ($(p.prefix + "Section").open) load().catch((e) => say(id("Message").id, e.message, true));
  });

  return { load };
}

// ---- OmniVoice: one drop-down per kind of trait (gender, age, pitch, accent, style); it only accepts its own fixed words ----

let omniTraits = [];

function renderTraits(chosen) {
  const picked = new Set((chosen || "").split(",").map((t) => t.trim()).filter(Boolean));
  $("omniTraits").replaceChildren(...omniTraits.map((g) => {
    const wrap = el("div");
    const label = el("label", g.group);
    label.className = "field-label";
    const select = el("select");
    select.append(option("", "(any)"), ...g.items.map((i) => option(i, i)));
    select.value = g.items.find((i) => picked.has(i)) || "";
    label.append(select);
    wrap.append(label);
    wrap.select = select;
    return wrap;
  }));
}

const readTraits = () => Array.from($("omniTraits").children).map((w) => w.select.value).filter(Boolean).join(", ");

const omni = createCard({
  prefix: "omni", method: "omnivoice", name: "OmniVoice", display: "OmniVoice (local)", providerId: "OmniVoice",
  blank: { name: "", kind: "design", instruct: "", referenceFile: "", referenceText: "", speed: 1 },
  renderLists: (state) => { omniTraits = state.traits || omniTraits; },
  fillForm(v) {
    $("omniVoiceName").value = v.name;
    $("omniVoiceKind").value = v.kind;
    renderTraits(v.instruct);
    $("omniVoiceReference").value = v.referenceFile;
    $("omniVoiceReferenceText").value = v.referenceText;
    $("omniVoiceSpeed").value = String(v.speed || 1);
  },
  showKind(kind) {
    $("omniReferenceField").classList.toggle("hidden", kind !== "clone");
    $("omniReferenceTextField").classList.toggle("hidden", kind !== "clone");
    $("omniTraitField").classList.toggle("hidden", kind !== "design");
  },
  readForm(v) {
    const kind = $("omniVoiceKind").value;
    return { ...v, id: v.id || undefined, name: $("omniVoiceName").value.trim(), kind, instruct: kind === "design" ? readTraits() : "",
      referenceFile: $("omniVoiceReference").value, referenceText: $("omniVoiceReferenceText").value.trim(),
      speed: Math.min(1.6, Math.max(0.6, Number($("omniVoiceSpeed").value) || 1)) };
  },
  checkSample(voice) {
    if (voice.kind === "design" && !voice.instruct) throw new Error("Choose at least one voice trait first, or choose 'Let the model choose'.");
    if (voice.kind === "clone" && !voice.referenceFile) throw new Error("Choose a recording to clone first.");
  },
  sampleExtras: () => ({}),
});

// ---- Qwen3-TTS: free-text descriptions, clones and preset speakers ----

const qwen = createCard({
  prefix: "qwen", method: "qwen3tts", name: "Qwen3-TTS", display: "Qwen3-TTS (local)", providerId: "Qwen3TTS",
  blank: { name: "", kind: "design", instruct: "", speaker: "Ryan", language: "English", referenceFile: "", referenceText: "" },
  renderLists(state) {
    $("qwenVoiceSpeaker").replaceChildren(...(state.speakers || []).map((x) => option(x, x)));
    $("qwenVoiceLanguage").replaceChildren(...(state.languages || []).map((x) => option(x, x)));
    $("qwenSampleEmotion").replaceChildren(...EMOTIONS.map((x) => option(x, x || "(none)")));
  },
  fillForm(v) {
    $("qwenVoiceName").value = v.name;
    $("qwenVoiceKind").value = v.kind;
    $("qwenVoiceInstruct").value = v.instruct;
    $("qwenVoiceSpeaker").value = v.speaker;
    $("qwenVoiceLanguage").value = v.language;
    $("qwenVoiceReference").value = v.referenceFile;
    $("qwenVoiceReferenceText").value = v.referenceText;
  },
  showKind(kind) {
    $("qwenInstructField").classList.toggle("hidden", kind === "clone");
    $("qwenInstructLabel").textContent = kind === "preset" ? "Standing style (optional, for example: calm and measured)" : "Describe the voice in your own words";
    $("qwenSpeakerField").classList.toggle("hidden", kind !== "preset");
    $("qwenReferenceField").classList.toggle("hidden", kind !== "clone");
    $("qwenReferenceTextField").classList.toggle("hidden", kind !== "clone");
  },
  readForm(v) {
    return { ...v, id: v.id || undefined, name: $("qwenVoiceName").value.trim(), kind: $("qwenVoiceKind").value, instruct: $("qwenVoiceInstruct").value.trim(),
      speaker: $("qwenVoiceSpeaker").value, language: $("qwenVoiceLanguage").value,
      referenceFile: $("qwenVoiceReference").value, referenceText: $("qwenVoiceReferenceText").value.trim() };
  },
  checkSample(voice) {
    if (voice.kind === "design" && !voice.instruct) throw new Error("Describe the voice first.");
    if (voice.kind === "clone" && !voice.referenceFile) throw new Error("Choose a recording to clone first.");
  },
  sampleExtras: () => ({ emotion: $("qwenSampleEmotion").value || null }),
});

/** Loads both cards so their voices are known before a card is opened. `changed(providerId, voices)` runs on load and whenever a voice is saved or deleted. */
export async function initLocalVoices(changed) {
  onVoicesChanged = changed || onVoicesChanged;
  await Promise.all([omni.load(), qwen.load()]);
}
