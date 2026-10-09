#!/usr/bin/env python3
"""VoiceForge AIO's bridge to a local Qwen3-TTS install (https://github.com/QwenLM/Qwen3-TTS, pip package qwen-tts).

    python qwen3tts_bridge.py check <job.json>    report whether Qwen3-TTS and a CUDA GPU can be used
    python qwen3tts_bridge.py serve <job.json>    speak lines until stdin closes (one model is loaded at a time: design, clone or preset)

Job file (JSON): designModel, cloneModel, presetModel (Hugging Face ids), dtype (bfloat16), device (cuda:0).

"serve" reads one JSON request per line on stdin and answers on stdout:
    request   {"id": "...", "text": "...", "mode": "design|clone|preset", "language": "English", "instruct": "...", "speaker": "Ryan",
               "refAudio": "path", "refText": "...", "temperature": 0.9, "topP": 0.9}
    answer    RESULT {"id": "...", "wav": "<base64 16-bit mono WAV>", "seconds": 1.7, "sampleRate": 24000}
              ERROR {"id": "...", "message": "..."}
Lines that are not RESULT, ERROR or READY are the libraries' own output and are ignored by VoiceForge AIO.
"""
import base64
import io
import json
import os
import sys
import traceback
import wave


def say(kind, text):
    # Always start on a fresh line: libraries print progress bars without a line break.
    sys.stdout.write("\n%s %s\n" % (kind, " ".join(str(text).split())))
    sys.stdout.flush()


def load_job(path):
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def torch_info():
    import torch
    info = {"python": sys.version.split()[0], "torch": torch.__version__, "cuda": bool(torch.cuda.is_available())}
    if info["cuda"]:
        info["gpu"] = torch.cuda.get_device_name(0)
        info["vramGb"] = round(torch.cuda.get_device_properties(0).total_memory / 1024 ** 3, 1)
    return info


def check(job):
    info = torch_info()
    try:
        import qwen_tts
        info["qwen_tts"] = getattr(qwen_tts, "__version__", "installed")
        from qwen_tts import Qwen3TTSModel  # noqa: F401
    except Exception as error:  # noqa: BLE001
        raise RuntimeError("Qwen3-TTS could not be loaded (is the qwen-tts package installed in this Python?): %s" % error)
    if not info["cuda"]:
        raise RuntimeError("Qwen3-TTS would run on the processor only: this Python cannot see an NVIDIA GPU. " + json.dumps(info))
    say("RESULT", json.dumps({"id": "check", **info}))


def to_wav_bytes(audio, sample_rate):
    import numpy as np
    samples = np.asarray(audio, dtype="float32").reshape(-1)
    pcm = (np.clip(samples, -1.0, 1.0) * 32767.0).astype("<i2")
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(sample_rate)
        out.writeframes(pcm.tobytes())
    return buffer.getvalue(), len(samples) / float(sample_rate)


def serve(job):
    import gc
    import torch
    from qwen_tts import Qwen3TTSModel
    dtype = getattr(torch, job.get("dtype") or "bfloat16")
    device = job.get("device") or "cuda:0"
    names = {"design": job.get("designModel"), "clone": job.get("cloneModel"), "preset": job.get("presetModel")}
    state = {"mode": None, "model": None}
    prompts = {}

    def load(mode):
        # Only one 1.7B model fits comfortably beside everything else, so a change of mode unloads the previous one.
        if state["mode"] == mode:
            return state["model"]
        state["model"] = None
        gc.collect()
        torch.cuda.empty_cache()
        name = names.get(mode)
        if not name:
            raise RuntimeError("No Qwen3-TTS model is set for '%s' voices." % mode)
        say("LOG", "loading %s" % name)
        kwargs = {"device_map": device, "dtype": dtype}
        try:
            import flash_attn  # noqa: F401
            kwargs["attn_implementation"] = "flash_attention_2"
        except Exception:  # noqa: BLE001
            pass
        state["model"] = Qwen3TTSModel.from_pretrained(name, **kwargs)
        state["mode"] = mode
        prompts.clear()
        return state["model"]

    def clone_prompt(model, path, text):
        key = (path, os.path.getmtime(path), text)
        if key not in prompts:
            prompts[key] = model.create_voice_clone_prompt(ref_audio=path, ref_text=text or None, x_vector_only_mode=not text)
        return prompts[key]

    def generate(model, call, **kwargs):
        try:
            return call(**kwargs)
        except TypeError:
            # An older release may not pass sampling options on; the line is still spoken, just without the emotion's sampling.
            for key in ("temperature", "top_p"):
                kwargs.pop(key, None)
            return call(**kwargs)

    say("READY", json.dumps({**torch_info(), "models": names}))
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        request_id = ""
        try:
            request = json.loads(line)
            request_id = str(request.get("id", ""))
            if request.get("op") == "quit":
                break
            mode = request.get("mode", "design")
            language = request.get("language") or "English"
            text = request["text"]
            instruct = (request.get("instruct") or "").strip()
            model = load(mode)
            if mode == "clone":
                path = request.get("refAudio") or ""
                if not os.path.isfile(path):
                    raise RuntimeError("The reference recording '%s' is missing." % path)
                ref_text = (request.get("refText") or "").strip()
                options = {"text": text, "language": language, "voice_clone_prompt": clone_prompt(model, path, ref_text)}
                if request.get("temperature"):
                    options["temperature"] = float(request["temperature"])
                if request.get("topP"):
                    options["top_p"] = float(request["topP"])
                wavs, rate = generate(model, model.generate_voice_clone, **options)
            elif mode == "preset":
                options = {"text": text, "language": language, "speaker": request.get("speaker") or "Ryan"}
                if instruct:
                    options["instruct"] = instruct
                wavs, rate = model.generate_custom_voice(**options)
            else:
                if not instruct:
                    raise RuntimeError("A designed voice needs a description.")
                wavs, rate = model.generate_voice_design(text=text, language=language, instruct=instruct)
            wav, seconds = to_wav_bytes(wavs[0], int(rate))
            say("RESULT", json.dumps({"id": request_id, "wav": base64.b64encode(wav).decode("ascii"), "seconds": round(seconds, 2), "sampleRate": int(rate)}))
        except Exception as error:  # noqa: BLE001
            traceback.print_exc(file=sys.stderr)
            say("ERROR", json.dumps({"id": request_id, "message": "%s: %s" % (type(error).__name__, error)}))
            try:
                torch.cuda.empty_cache()
            except Exception:  # noqa: BLE001
                pass


def main():
    if len(sys.argv) != 3 or sys.argv[1] not in ("check", "serve"):
        say("ERROR", json.dumps({"id": "", "message": "usage: qwen3tts_bridge.py check|serve <job.json>"}))
        return 2
    try:
        job = load_job(sys.argv[2])
        (check if sys.argv[1] == "check" else serve)(job)
        return 0
    except Exception as error:  # noqa: BLE001
        traceback.print_exc(file=sys.stderr)
        say("ERROR", json.dumps({"id": "", "message": "%s: %s" % (type(error).__name__, error)}))
        return 1


if __name__ == "__main__":
    sys.exit(main())
