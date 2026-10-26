# VoiceForge AIO

## New in this version: free local voices (OmniVoice and Qwen3-TTS)

Two new providers that run on your own graphics card -- **no API key, no
per-line cost, nothing leaves your computer**:

- **OmniVoice (local)** -- voices described from a fixed list of traits
  (gender, age, pitch, accent, whisper), cloned from a short recording, or
  chosen by the model. Quality choice: 32 steps (best) or 16 steps (fast).
- **Qwen3-TTS (local)** -- voices described in your own words (any language),
  cloned from a short recording, or one of nine built-in speakers.

Set them up on the new **Local Voices** page (point it at the Python of each
tool's own environment, optionally run inside WSL, then *Check the install*),
make voices and audition them with *Hear sample*, then pick the provider in
**Voice Mapping**, **Single Line** or **Generate** like any other.
Install steps: [docs/omnivoice.md](docs/omnivoice.md) and
[docs/qwen3-tts.md](docs/qwen3-tts.md). The Python bridge scripts they use
live in `VoiceForgeAIO/tools/` and ship next to the exe.

## Models per provider and per voice

**Settings → Model for each provider** picks the model Generate (CSV or plugin) and Single Line use for that provider
(Inworld, ElevenLabs, MiniMax, Cartesia, Fish Audio, OmniVoice). Order of precedence for a line: the model chosen on its
Voice Mapping row, then the Settings model, then the provider's built-in default.

Voice Mapping also has a **Model** column next to the provider. The chosen model is used for that VoiceType everywhere,
including **Generate** (CSV or plugin); "Default" uses the provider's default model, so older mappings behave as before.
ElevenLabs now offers `eleven_v4` and `eleven_v4_turbo` alongside v3 and the v2.5 models. Mapping CSVs carry a
`ModelId` column (older files without it still import).

## Look and feel

The interface now matches StoryLoom: the same dark navy panels and amber accent, serif page titles with
small spaced section labels, a collapsible branded sidebar (Plugin Dialogue, Voice Mapping, Single Line,
Generate, Local Voices; Settings, Help and About at the bottom), rounded cards, and a resizable log dock
along the bottom that can be hidden. Settings is a page instead of a pop-up, each API key shows whether
one is entered, and the sidebar shows when a batch is generating. No web fonts or other outside resources are loaded.

## Read dialogue straight from a plugin (no xEdit, no CSV)

The **Plugin Dialogue** page reads a mod's voiced lines directly from its `.esp` / `.esm` / `.esl`,
the way StoryLoom does, so the xEdit exporter scripts and CSV step are optional now:

1. **Where are your plugins?** VoiceForge AIO auto-detects the Skyrim Special Edition **Data**
   folder (Steam, GOG or the Bethesda registry entry); otherwise type or Browse to it. For Mod
   Organizer 2, also set the **extra plugin folder** to its `mods` folder (each mod's folder is
   scanned). The folders are remembered. **Open a plugin file...** reads one from anywhere.
2. **Choose a plugin** from the list (Skyrim's own files and Creation Club content are hidden unless
   you ask for them) and **Read dialogue**. Masters are loaded from the same folders so speakers
   defined in Skyrim.esm and other masters resolve.
3. **Voice types**: every line is grouped by VoiceType with its line count, speakers and current
   Voice Mapping. Untick any you don't want voiced. **Add missing VoiceTypes to Voice Mapping**
   creates the rows; pick a provider and voice for each.
4. **Generate audio for ticked VoiceTypes** switches **Generate** to the plugin as its source
   (everything there -- LIP, FUZ, overwrite, pause/stop, `_failures.csv` -- works the same). The output
   folder is suggested as `Data\Sound\Voice\<Plugin>`; change it if you'd rather not write into
   Data directly.

How a line's VoiceType is found matches the xEdit exporters: the INFO's **Speaker**, then its
**GetIsVoiceType**, **GetIsAliasRef** and **GetIsID** conditions (in that order), then the quest's
dialogue conditions; topics a **scene** plays use the scene action's actor alias. An NPC without a
voice of its own that takes its traits from a template uses the template's. Lines with no speaker the
plugin can resolve are listed as `Unknown` (unticked). File names are
`Quest_Topic_FormID_Response`, with the quest and topic EditorIDs shortened to fit 25 characters the
way the exporters do. **Export as CSV** writes the same rows as the scripts, if you still want a file.

Notes: only Skyrim Special Edition plugins are read. **Localized** plugins keep their text in
`Strings\*.STRINGS` files; they are read when that folder sits next to the plugin, otherwise you get a
warning saying so (extract it from the `.bsa`). For light (ESL) plugins the FormID in the file name is
the plugin's own 24-bit ID (e.g. `00000800`).

## Voice Mapping and batch fixes

Found by running the app end to end (see `tests/e2e`):

- **Voice Mapping**
  - VoiceTypes are trimmed (a trailing space used to make a name never match the CSV).
  - Names that differ only by letter case can't both be saved; the save says which.
  - Rows with no voice, an unknown provider, or a voice that no longer exists are marked orange and listed when you save.
  - Click a row to select it; Ctrl-click and Shift-click select several. Remove Selected explains itself when nothing is selected.
  - The table shows `* ` on **Save Mapping** while there are unsaved edits, and a batch saves them first.
  - Voice lists from **Get Available Voices** are remembered between launches, and a provider that fails to refresh keeps the list it had.
- **Mapping CSV import** understands the older `VoiceType,VoiceId,VoiceDisplay` format (the provider is filled in when only one loaded provider offers that voice, otherwise flagged), is case-insensitive about provider names, and warns about rows it can't place.
- **CSV Batch**
  - Quoted text may span several lines; a `EditorID,VoiceType,Text` header row is skipped.
  - Two lines that would write the same file (same EditorID and VoiceType) are reported instead of overwriting each other.
  - Progress reaches 100%, Stop reports how many lines were really made, `_failures.csv` is written even if the voice folder doesn't exist yet, and an old one is removed after a clean run.

## Earlier change: web-based UI (Photino.NET)

The UI is now built with real HTML/CSS/JavaScript, rendered through the OS's
native WebView2 control via Photino.NET, instead of WinForms. This is why:
proper CSS means the Ink Wash theme can actually look intentional (hover
states, smooth transitions, real rounded corners) rather than fighting
WinForms' native control rendering.

**Nothing about the actual functionality changed.** Every provider,
BatchProcessor, FaceFXWrapper/xWMAEncode/LipFuzer integration, and the
persistence layer is the exact same C# code as before -- only the UI layer
was replaced. The new files are `Program.cs` (window bootstrap),
`ApiBridge.cs` (a small JSON message bridge connecting the web UI to those
same backend classes), and everything under `wwwroot/`.

**New requirement**: the WebView2 runtime, which ships with Windows 10/11 by
default in most cases. If it's ever missing, grab it from
https://developer.microsoft.com/microsoft-edge/webview2/

**Honest flag**: a few of Photino's dialog method signatures
(`ShowOpenFile`/`ShowOpenFolder`/`ShowSaveFile`, used for all the Browse
buttons) were inferred from its source/docs rather than confirmed against a
real build, since I don't have a Windows/WebView2 environment to test
against. If the first build complains about those specifically, it's very
likely just a parameter order/type mismatch, not a deeper problem -- send me
the exact error and it should be a quick fix.

---

## Distributing to other people (not just building for yourself)

If you're handing this to a tester or releasing it, three things come up:

### "This requires .NET 8" on a machine that doesn't have it

A plain `dotnet build`/F5 produces a **framework-dependent** build, which
expects .NET 8 already installed. Use the included publish profile instead to
produce a **self-contained** build that bundles the runtime:

```
dotnet publish -c Release -p:PublishProfile=FolderProfile
```

(or in Visual Studio: right-click the project → Publish → pick the
`FolderProfile`). Output lands in
`VoiceForgeAIO\bin\Release\net8.0-windows\publish\` and needs nothing
preinstalled except WebView2 (see below) -- bigger folder, zero prerequisite.

### What files to actually ship

Ship the **entire output folder, minus `.pdb` files** -- don't hand-pick
individual DLLs. Photino brings in both a managed DLL and a native one, and
guessing wrong means a launch failure for whoever you send it to. This
includes the `wwwroot` subfolder, which is required (the app loads
`wwwroot/index.html` from disk at a relative path).

### "Windows protected your PC" (SmartScreen)

Expected for any new unsigned executable -- it's about download reputation,
not a missing file. Options, roughly cheapest to most effective:

- **Do nothing**: testers click "More info" → "Run anyway." Fine for a
  community tool, just mention it in your release notes so it's not a
  surprise.
- **Standard code-signing certificate** (~$100-300/year): shows your actual
  name instead of "Unknown publisher," but SmartScreen trust still builds up
  gradually with download volume.
- **EV code-signing certificate** (pricier, requires business verification):
  the only option that removes the warning immediately, no waiting period.

### Building an installer

`installer/VoiceForgeAIO.iss` is an [Inno Setup](https://jrsoftware.org/isinfo.php)
script (free) that packages the self-contained publish output into a proper
`VoiceForgeAIO-Setup.exe` -- Start Menu shortcut, optional desktop shortcut,
uninstaller, and a WebView2 runtime check/install step. Read the comment
block at the top of that file for the two setup steps needed before
compiling it (publishing first, and grabbing Microsoft's small WebView2
bootstrapper). (InstallShield's free tier isn't really available in modern
Visual Studio anymore, which is why this uses Inno Setup instead -- it's the
standard free choice for this kind of distribution.)

A WinForms tool (.NET 8, Visual Studio) for batch-generating Skyrim dialogue
audio and lip-sync data across multiple TTS providers, with automatic
per-VoiceType output routing.

## What's new in this version

- **Multi-provider TTS**: Inworld, ElevenLabs, MiniMax, Cartesia, Fish Audio,
  Hume Octave, and the local OmniVoice and Qwen3-TTS, all behind one interface. Each VoiceType in the mapping
  table can point at a different provider.
- **Encrypted settings**: every provider's API key is encrypted at rest with
  Windows DPAPI (tied to your Windows account), not just Base64-obscured.
- **LIP generation**: an opt-in checkbox shells out to FaceFXWrapper to
  produce a matching `.lip` file alongside each generated WAV.

## Building

1. Open `VoiceForgeAIO.sln` in Visual Studio 2022 (17.8+) with the **.NET
   desktop development** workload installed.
2. Build/Run (F5). This version has **one** NuGet dependency
   (`System.Security.Cryptography.ProtectedData`, for real key encryption) --
   Visual Studio should restore it automatically the first time, provided
   you have an internet connection. Everything else (HttpClient,
   System.Text.Json, WinForms) ships in the `net8.0-windows` shared framework.

**Note:** built without a Windows machine available to me, so I couldn't
compile-test it. If the first build turns up an error, send it to me and
I'll fix it immediately -- past experience with this tool says that's a fast
turnaround.

## First-time setup

1. **Settings**: enter the API key for whichever provider(s) you use.
   - **MiniMax** additionally needs its **Group ID** (a separate credential
     from the API key -- both are on your MiniMax account dashboard).
   - Check **"Show keys"** to verify what you typed instead of squinting at
     dots.
   - If you want **LIP generation**, also set **FaceFXWrapper.exe** and
     **FonixData.cdf** here (see "LIP generation" below for where to get
     these -- neither is bundled with this tool).
   - Click **Save Settings**.
2. **Voice Mapping**: click **Get Available Voices** -- this pulls the voice
   list from every provider that currently has an API key entered. Click
   **Add Row**, type a VoiceType (must match your CSV's VoiceType column and
   the Skyrim voice folder name you want), pick a **Provider**, then pick
   the **Voice** for it (the dropdown repopulates based on whichever
   provider you picked for that row). Repeat per character, then
   **Save Mapping**.
3. **Generate**: pick your CSV, pick the mod's `.esp` Voice folder,
   optionally check **Generate .lip files**, and hit **Generate audio**.

## CSV format (unchanged)

```
EditorID,VoiceType,"Line text"
```

No header row. The VoiceType column is looked up in the Voice Mapping table
to find both the Provider and the Voice to use for that line.

## LIP generation

Off by default -- check **"Generate .lip files"** on the Generate page to
turn it on. When enabled, after each WAV is written, VoiceForge AIO shells
out to **FaceFXWrapper.exe** (https://github.com/Nukem9/FaceFXWrapper, MIT
licensed) to produce a matching `.lip` file in the same VoiceType folder.

**Neither FaceFXWrapper.exe nor FonixData.cdf are bundled with this tool** --
both are third-party/Bethesda files that community tools consistently avoid
redistributing for licensing reasons, so you point Settings at your own
local copies:

- **FaceFXWrapper.exe**: build it yourself from the GitHub source, or grab a
  compiled release from the project's GitHub Releases page.
- **FonixData.cdf**: not distributed with FaceFXWrapper. You likely already
  have a copy if you've done Creation Kit work -- check
  `...\Skyrim Special Edition\Tools\LipGen\LipGenerator\FonixData.cdf` in
  your Steam install, or grab it from the Creation Kit / G.E.C.K. itself.

**Not every provider supports LIP generation.** FaceFXWrapper needs
uncompressed WAV/PCM audio. Inworld, ElevenLabs, MiniMax, Cartesia, Fish
Audio, OmniVoice, and Qwen3-TTS all provide that (some natively, some via a raw-PCM request that
VoiceForge AIO wraps in a WAV header itself). **Hume Octave only returns
compressed audio (mp3) in this version**, so LIP generation is automatically
skipped for any line using a Hume Octave voice -- the WAV still generates
normally, just without a matching `.lip`, and the log will say so.

**Sample rate handling**: FaceFXWrapper expects 16kHz/16-bit/mono audio for
lip-sync analysis, but providers export at whatever rate they use natively
(Cartesia at 44.1kHz, MiniMax at 32kHz, etc.) -- and FaceFXWrapper's own
built-in resampling doesn't reliably handle all of those. VoiceForge AIO
resamples a throwaway temp copy to 16kHz itself before handing it to
FaceFXWrapper, then deletes the temp copy. **The actual WAV that ships with
your mod (and gets encoded to XWM for FUZ) is untouched and keeps its
original quality** -- only the internal LIP-analysis copy is downsampled.

FUZ packaging (combining WAV+LIP into Bethesda's `.fuz` container) is now
supported -- see "FUZ generation" below.

## FUZ generation

Off by default, and **only enabled once "Generate .lip files" is checked** --
a `.fuz` needs both the WAV and the LIP to exist, so the checkbox is disabled
until LIP generation is on. When both are checked, VoiceForge AIO runs two
more tools per VoiceType folder, after every line's WAV/LIP is done:

1. **xWMAEncode** (`input.wav output.xwm`) -- converts each WAV to XWM
   (xWMA-compressed audio), which is what Skyrim's `.fuz` container actually
   expects internally. Feeding it raw WAV/PCM directly wouldn't produce a
   correctly-playable `.fuz`.
2. **LipFuzer** (`-s <folder> -d <folder>`) -- combines every matching
   `.xwm`+`.lip` pair in that folder into a `.fuz`. Unlike FaceFXWrapper/
   xWMAEncode (one file per call), LipFuzer works on a whole directory at
   once, so this runs once per VoiceType folder touched by the batch, not
   once per line.

The WAV, LIP, and XWM files are all left in place alongside the FUZ --
nothing is deleted or replaced automatically.

Neither tool is bundled, same reasoning as FaceFXWrapper/FonixData.cdf:

- **xWMAEncode.exe**: from the legacy DirectX SDK (June 2010), or the Xbox
  ADK/XDK. Also commonly found bundled with various Bethesda-game modding
  toolsets on Nexus.
- **LipFuzer.exe**: found alongside `LipGenerator.exe` in Skyrim SE's own
  `Tools\LipGen\LipGenerator` folder (Steam install), or from community
  packages like Runalip.

Set both paths in Settings before checking "Generate .fuz files."

## Behavior notes (carried over from earlier versions)

- **Resumable**: a row is skipped if its WAV (and `.lip`, if LIP generation
  is on) already exists, unless "Overwrite existing files" is checked.
- **Unmapped VoiceTypes / missing API keys**: fail clearly per-row rather
  than silently skipping.
- **Failures log**: `_failures.csv` is written to the mod Voice folder after
  a batch run listing every failed row and why -- fix the issue and re-run
  the same CSV; successes are skipped automatically.
- **Rate limits**: every provider retries automatically on HTTP 429/5xx with
  exponential backoff + jitter. Concurrency is adjustable (default 3).
- **Voice mapping export/import**: the grid's Export/Import CSV buttons now
  write/read `VoiceType,ProviderId,VoiceId,VoiceDisplay` (a Provider column
  was added to the earlier `VoiceType,VoiceId,VoiceDisplay` format).

## Not included in this version

- Batch File Renaming / CSV File Copying / hotkey listening (dropped in an
  earlier pass, per original scope discussion)
- OpenAI TTS, Amazon Polly, Azure, Google Cloud (considered, but judged too
  expensive/lower-quality for this use case for now -- easy to add later if
  that changes)
- Other local TTS engines beyond OmniVoice and Qwen3-TTS
