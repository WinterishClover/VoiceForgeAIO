# Changelog

## 1.1.0

The biggest update so far: VoiceForge can now read your mod's dialogue straight from the plugin (no xEdit script, no CSV), run two free voice engines on your own graphics card, and has a new look that matches StoryLoom. This release also fixes a long list of Voice Mapping and batch problems.

### New

**Plugin Dialogue: no more CSVs**
- New **Plugin Dialogue** page reads a mod's voiced lines directly from its `.esp`, `.esm` or `.esl`.
- Finds your Skyrim Special Edition **Data folder** automatically (Steam, GOG or registry), or you can point it at one. An optional extra folder supports **Mod Organizer 2** (its `mods` folder is scanned, one level deep). Your folders are remembered.
- Pick a plugin from the list, or **Open a plugin file** from anywhere. Skyrim's own files and Creation Club content are hidden unless you ask for them.
- Masters are loaded automatically, so speakers defined in `Skyrim.esm` and other masters resolve correctly.
- Lines are grouped by **VoiceType** with line counts, speakers and the current Voice Mapping status. Untick any VoiceType you don't want voiced, search and preview every line, and add all missing VoiceTypes to Voice Mapping in one click.
- Speakers are worked out the same way the xEdit exporter scripts do it: the line's Speaker, then its voice-type, alias and ID conditions, then the quest's conditions. Scenes use the scene action's actor alias, and NPCs that take their traits from a template use the template's voice. Lines with no resolvable speaker are listed as `Unknown` and start unticked.
- Files are named the way the game expects (`Quest_Topic_FormID_Response`), saved under `Sound\Voice\<Plugin>\<VoiceType>`.
- **Generate** can now use the loaded plugin as its source instead of a CSV. LIP, FUZ, overwrite, pause and stop all work the same.
- **Export as CSV** is still there, in the same format as the xEdit scripts, and the xEdit scripts and CSV workflow still work.
- Localized plugins are read when their `Strings` folder is beside the plugin; otherwise you get a clear warning (extract it from the `.bsa`).

**Free local voices: OmniVoice and Qwen3-TTS**
- Two new providers that run on your own graphics card: **no API key, no per-line cost, nothing leaves your computer.**
  - **OmniVoice (local):** voices described from its list of traits (gender, age, pitch, accent, whisper), cloned from a short recording, or chosen by the model. Choose 32 steps (best) or 16 steps (fast).
  - **Qwen3-TTS (local):** voices described in your own words (any language), cloned from a short recording, or one of nine built-in speakers.
- New **Local Voices** page: point it at each engine's Python (optionally run inside WSL), **Check the install**, make voices, and **Hear sample**. Models load on first use and unload after a quiet spell so your graphics card is free again.
- Local voices work everywhere cloud voices do: Voice Mapping, Single Line and Generate. Install guides: `docs/omnivoice.md` and `docs/qwen3-tts.md`.

**ElevenLabs Eleven v4, and a model for every voice**
- **Eleven v4** (`eleven_v4`) and **Eleven v4 Turbo** (`eleven_v4_turbo`) are now available for ElevenLabs. The default model is unchanged.
- **Settings has a new "Model for each provider" section** (Inworld, ElevenLabs, MiniMax, Cartesia, Fish Audio and OmniVoice). The model you pick there is the one **Generate** runs, from a CSV or from a plugin, and Single Line starts on it too. It applies to every voice of that provider unless a Voice Mapping row picks its own model. Each selector also shows the provider's built-in default.
- Voice Mapping has a new **Model** column. The model you pick for a VoiceType is used everywhere, including **Generate** (CSV or plugin); previously only Single Line let you choose and every batch silently used the provider's default. Leave it on "Default" to use the Settings model (or the provider's built-in default if you haven't chosen one), so existing mappings behave as before.
- It works for every provider, so you can also choose per voice, for example OmniVoice 32 or 16 steps.
- Mapping CSV export/import carries the model as a new `ModelId` column. Older mapping files still import.

**Single Line: listen right away**
- The generated line now **plays automatically** in a player on the page (with Play again), and **Show in folder** opens Explorer on the saved file, so there's no hunting for it.
- **Load voices** button on the page fetches the selected provider's voices; you no longer have to go to Voice Mapping first. It tells you if the provider has no key saved.
- The output folder is optional now: leave it blank and test lines go to `Documents\VoiceForgeAIO\Samples`.

**New look**
- The interface now matches StoryLoom: dark panels with an amber accent, serif page titles, rounded cards and a collapsible sidebar.
- Settings is now a page rather than a pop-up, and each API key shows whether one is entered.
- The log is now a bottom dock you can resize or hide, which gives every page more room.
- **CSV Batch Generate** is now simply **Generate**, and the sidebar shows when a batch is running.
- No web fonts or other outside resources are loaded.
- The welcome card no longer flashes up on every launch for returning users.

### Fixed

**Voice Mapping**
- VoiceTypes with a stray trailing space no longer fail to match the CSV; names are trimmed.
- VoiceTypes that differ only by letter case (`Dup` / `dup`) can no longer both be saved; the save tells you which clash.
- Rows with no voice, an unknown provider, or a voice that no longer exists are now marked in amber and listed when you save, instead of failing silently later.
- Rows can now be multi-selected: click a row, Ctrl-click or Shift-click for several. **Remove Selected** now explains itself when nothing is selected.
- Voice lists are remembered between launches, so a mapping works straight away without another **Get Available Voices**. A provider that fails to refresh keeps the list it had, instead of every list being wiped.
- Importing an older 3-column mapping CSV (`VoiceType,VoiceId,VoiceDisplay`) no longer corrupts the rows. Lowercase provider names import correctly, and rows that can't be placed are reported.
- Local voices show by name instead of a generated ID, and a deleted voice is flagged on the row.
- **Save Mapping** shows a `*` while there are unsaved edits, and starting a batch saves them first.
- The mapping file is written safely, so a crash mid-save can't leave it unreadable.

**Generate (formerly CSV Batch)**
- The progress bar now reaches 100% (lines that failed up front weren't counted).
- **Stop** now reports how many lines were really made and says the batch was stopped, instead of claiming everything succeeded.
- `_failures.csv` is now written even when the voice folder doesn't exist yet, and an old one is removed after a clean run.
- CSV lines containing line breaks inside quotes are now read correctly, and a header row (`EditorID,VoiceType,Text`) is skipped instead of being treated as a line to voice.
- Two lines that would write the same file (same EditorID and VoiceType) are reported instead of silently overwriting each other.

**Other**
- Single Line keeps your chosen model and voice when voice lists refresh.
- Opening a Local Voices card no longer reloads it and wipes what you just typed, and a click ignored while a save is in progress now says so.

### For builders
- New requirement for building: the Mutagen package (restored automatically).
- Fixed invalid XML in `FolderProfile.pubxml` that stopped `dotnet publish` from loading it.
- Added `tests/e2e`: end-to-end tests that drive the real backend and UI against generated plugins and fake providers (96 checks). See its README.

### Known limits
- Only Skyrim Special Edition plugins are read.
- File names for light (ESL) plugins use the plugin's own FormID (e.g. `00000800`).
- Local voices live on the computer that made them, so an exported mapping CSV that uses them only works there.
- Local engines make one line at a time, whatever the Concurrency setting is.

## 1.0.0
- Initial public release: multi-provider TTS (Inworld, ElevenLabs, MiniMax, Cartesia, Fish Audio, Hume Octave), voice mapping, CSV batch generation with LIP and FUZ output, and xEdit dialogue exporter scripts.
