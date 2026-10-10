# End-to-end tests

Runs the app's **real C# backend** (ApiBridge, BatchProcessor, the OmniVoice / Qwen3-TTS
services, persistence) behind a small HTTP host and drives the **real web UI** in Chromium.
Only the outside world is faked:

- `Shim.cs` stands in for the Photino window (file dialogs, message channel) and Windows DPAPI.
- `Registry.cs` adds a **Fake Cloud** provider (fails on text containing `FAIL`, slow on `SLOW`).
- `fake_bridge.py` speaks the real OmniVoice / Qwen3-TTS bridge protocol without needing a GPU,
  so process start-up, voices, samples and batch lines go through the production code.

No real TTS provider, GPU, WSL, FaceFXWrapper or xWMAEncode is exercised.

```
npm i playwright            # once, anywhere on NODE_PATH
tests/e2e/run-all.sh        # builds the harness, runs both suites; set CHROMIUM_PATH if Playwright's browser isn't installed
```

- `verify.cjs` -- Voice Mapping edits, CSV import/export, batch progress/stop/failure log, local voices, restart persistence.
- `verify-plugin.cjs` -- Plugin Dialogue: `PluginMaker.cs` writes real plugins with Mutagen (a master + a plugin covering Speaker,
  GetIsVoiceType / GetIsAliasRef / GetIsID conditions, template NPCs, scenes, quest conditions, master quests, long-name file
  shortening, a localized plugin with and without its Strings), then the suite drives the page: find, read, map, generate, export.

Both print PASS/FAIL per behaviour and exit non-zero on any failure.
