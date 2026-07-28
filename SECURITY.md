# Security

## How API keys are handled

VoiceForge AIO stores API keys for each TTS provider (Inworld, ElevenLabs,
MiniMax, Cartesia, Fish Audio, Hume Octave) entirely on your own machine.
Specifically:

- **Never transmitted anywhere except the provider itself.** When you
  generate a line of audio, your key is sent directly to that provider's own
  API (e.g. `api.elevenlabs.io`) over HTTPS -- exactly as if you'd called
  their API yourself. Nothing passes through any server operated by
  VoiceForge AIO or its author, because no such server exists; this is a
  fully local, offline-except-for-your-own-API-calls desktop application.
- **Encrypted at rest with Windows DPAPI** (`ProtectedData.Protect`, tied to
  your Windows user account) before being written to
  `%AppData%\VoiceForgeAIO\settings.json`. This means the file is
  cryptographically meaningless to anyone without your exact Windows login
  on your exact machine -- copying the file to another PC, or reading it
  under a different Windows account on the same PC, will not decrypt it.
- **No telemetry, no analytics, no phone-home of any kind.** The app makes
  network requests only to the TTS providers you've configured, and to
  Google Fonts (for the UI's typeface, falls back gracefully if unreachable).

### What this does *not* protect against

Being honest about the limits: DPAPI protects against someone casually
copying the settings file off your disk or another user account on the same
machine reading it. It does **not** protect against malware or another
process already running *as you* on your own Windows session -- nothing
short of a hardware security module fully defeats that threat model, and
this app doesn't claim to.

### Why you can verify this yourself

This is open source specifically so this claim doesn't have to be taken on
faith -- `PersistenceService.cs` contains the entire encryption
implementation, and every provider's HTTP calls live in `Providers/*.cs`.
There is no hidden network call anywhere in this codebase; if you find one,
please open an issue immediately.

## Reporting a vulnerability

If you find a security issue, please open a GitHub issue, or reach out
directly via [Patreon](https://www.patreon.com/cw/winterishclover) if it's
sensitive enough that you'd rather not post it publicly first.
