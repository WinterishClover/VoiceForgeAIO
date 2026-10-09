# OmniVoice (local text-to-speech)

[OmniVoice](https://github.com/k2-fsa/OmniVoice) speaks lines on your own graphics card. In VoiceForge AIO it is the provider **OmniVoice (local)**: no API key, no credits, nothing leaves your computer.

## Install (Windows or WSL)

OmniVoice needs its **own** Python environment. The commands below use conda inside WSL (the usual setup for a CUDA card on Windows). Open your Linux machine (in PowerShell: `wsl -l` lists the distros, `wsl -d <name>` opens one) and call conda by its full path, because a system Python usually refuses `pip install` ("externally managed"):

```bash
/root/miniconda3/bin/conda create -n omnivoice python=3.10 -y
/root/miniconda3/envs/omnivoice/bin/pip install torch==2.8.0+cu128 torchaudio==2.8.0+cu128 --extra-index-url https://download.pytorch.org/whl/cu128
/root/miniconda3/envs/omnivoice/bin/pip install omnivoice
/root/miniconda3/envs/omnivoice/bin/python -c "import torch; print(torch.cuda.is_available())"   # must print True
```

Then in VoiceForge AIO open **Local Voices → OmniVoice**: set Python to `/root/miniconda3/envs/omnivoice/bin/python`, tick **Run inside WSL**, give the distro name (blank = your default distro), **Save**, then **Check the install**. The first check downloads the model (a few GB).

A native Windows Python works too: leave **Run inside WSL** unticked and give the full path to that environment's `python.exe`.

## Voices

Make voices in the same card:

- **A description** — pick from OmniVoice's own trait lists (gender, age, pitch, accent, whisper). OmniVoice only understands these words, not free text.
- **A recording (clone)** — 3 to 10 seconds of clean speech. Type what it says, or leave it blank and OmniVoice listens and writes it itself.
- **Let the model choose.**

**Hear sample** plays the voice. In **Voice Mapping**, pick the provider **OmniVoice (local)** and choose the voice. The model choice sets the quality: *32 steps* (best, default) or *16 steps* (faster).

## Notes

- The model stays loaded while lines are being made and is unloaded after the idle time (default 10 minutes) so the graphics card is free again. **Unload model** does it at once.
- Delivery tags OmniVoice knows (`[laughter]`, `[sigh]`, `[surprise-ah]`, …) are spoken; common aliases such as `[laughs]` are mapped to them; any other bracketed tag is removed so it is never read aloud.
- Lines are made one at a time by the local model, whatever the batch Concurrency is set to.
- Voices are stored on this computer (`%AppData%\VoiceForgeAIO\omnivoice.json`), so a mapping CSV that uses them only works on the machine that made them.
- A 12 GB card is enough. Do not run two GPU-heavy tools at once.
