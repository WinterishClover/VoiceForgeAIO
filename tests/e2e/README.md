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
dotnet build tests/e2e/E2E.Harness.csproj -c Debug -o tests/e2e/out
npm i playwright            # once, anywhere on NODE_PATH
node tests/e2e/verify.cjs   # set CHROMIUM_PATH if Playwright's browser isn't installed
```

`verify.cjs` prints PASS/FAIL per behaviour (Voice Mapping edits, CSV import/export, batch
progress/stop/failure log, local voices, restart persistence) and exits non-zero on any failure.
