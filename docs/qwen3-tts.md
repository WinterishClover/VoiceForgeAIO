# Qwen3-TTS (local text-to-speech with free-text voice design)

[Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS) (Apache-2.0) runs on your own graphics card. In VoiceForge AIO it is the provider **Qwen3-TTS (local)**: no API key, no credits.

Three ways to make a voice (**Local Voices → Qwen3-TTS**):

- **A description** (VoiceDesign model): any words, any language, for example "a gruff old man with a slow, tired voice and a northern accent".
- **A recording** (Base model): clone from 3 to 10 seconds of clean speech. Type what it says for a more accurate clone.
- **A built-in speaker** (CustomVoice model): Ryan, Aiden, Vivian, Serena, Uncle_Fu, Dylan, Eric, Ono_Anna, Sohee.

Only the model for the voice in use is loaded; switching kind unloads one and loads the other (the first line after a switch is slow). The models download from Hugging Face on first use (several GB each).

In **Voice Mapping**, pick the provider **Qwen3-TTS (local)** and choose the voice.

## Emotion

**Hear sample** has an emotion box. Designed and preset voices are told to speak in that tone. Cloned voices take no instructions, so the emotion only nudges the sampling (hotter and looser for energetic feelings, steadier for quiet ones); that is a small effect, so for a strong change clone a second recording in the tone you want. Batch lines are made without an emotion.

## Install (Windows or WSL)

Give it its own environment. In WSL (in PowerShell: `wsl -l` lists the distros, `wsl -d <name>` opens one):

```bash
/root/miniconda3/bin/conda create -n qwen3tts python=3.12 -y
/root/miniconda3/envs/qwen3tts/bin/pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu128
/root/miniconda3/envs/qwen3tts/bin/pip install -U qwen-tts
/root/miniconda3/envs/qwen3tts/bin/python -c "import torch, qwen_tts; print(torch.cuda.is_available())"   # must print True
```

Then in **Local Voices → Qwen3-TTS**: Python `/root/miniconda3/envs/qwen3tts/bin/python`, tick **Run inside WSL**, distro name (blank = your default distro), **Save**, **Check the install**. FlashAttention is optional and used only if installed. A native Windows Python works too: leave **Run inside WSL** unticked and give the full path to that environment's `python.exe`.

## Notes

- The model is unloaded after the idle time (default 10 minutes); **Unload model** frees the card at once.
- Bracketed delivery cues in a line (`[sigh]`) are removed, not read aloud.
- Lines are made one at a time by the local model, whatever the batch Concurrency is set to.
- Voices are stored on this computer (`%AppData%\VoiceForgeAIO\qwen3tts.json`), so a mapping CSV that uses them only works on the machine that made them.
- Do not run Qwen3-TTS and OmniVoice (or other GPU-heavy tools) at the same time on a 12 GB card.
