#!/usr/bin/env python3
"""VoiceForge AIO's bridge to a local OmniVoice install (https://github.com/k2-fsa/OmniVoice).

    python omnivoice_bridge.py check <job.json>    report whether OmniVoice and a CUDA GPU can be used
    python omnivoice_bridge.py serve <job.json>    load the model once, then speak lines until stdin closes

Job file (JSON): model (default k2-fsa/OmniVoice), dtype (float16), device (cuda:0).

"serve" reads one JSON request per line on stdin and answers on stdout:
    request   {"id": "...", "text": "...", "mode": "design|clone|auto", "instruct": "...", "refAudio": "path", "refText": "...", "steps": 32, "speed": 1.0}
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
        import omnivoice
        info["omnivoice"] = getattr(omnivoice, "__version__", "installed")
        from omnivoice import OmniVoice  # noqa: F401
    except Exception as error:  # noqa: BLE001
        raise RuntimeError("OmniVoice could not be loaded: %s" % error)
    try:
        import transformers
        info["transformers"] = transformers.__version__
    except Exception:  # noqa: BLE001
        pass
    if not info["cuda"]:
        raise RuntimeError("OmniVoice would run on the processor only: this Python cannot see an NVIDIA GPU. " + json.dumps(info))
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
    import torch
    from omnivoice import OmniVoice
    model_name = job.get("model") or "k2-fsa/OmniVoice"
    dtype = getattr(torch, job.get("dtype") or "float16")
    model = OmniVoice.from_pretrained(model_name, device_map=job.get("device") or "cuda:0", dtype=dtype)
    sample_rate = int(getattr(model, "sampling_rate", 24000) or 24000)
    prompts = {}

    def clone_prompt(path, text):
        key = (path, os.path.getmtime(path), text)
        if key not in prompts:
            prompts[key] = model.create_voice_clone_prompt(ref_audio=path, ref_text=text) if text else model.create_voice_clone_prompt(ref_audio=path)
        return prompts[key]

    say("READY", json.dumps({**torch_info(), "model": model_name, "sampleRate": sample_rate}))
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
            options = {"text": request["text"], "num_step": int(request.get("steps") or 32)}
            speed = float(request.get("speed") or 1.0)
            if abs(speed - 1.0) > 0.01:
                options["speed"] = speed
            mode = request.get("mode", "auto")
            if mode == "clone":
                path = request.get("refAudio") or ""
                if not os.path.isfile(path):
                    raise RuntimeError("The reference recording '%s' is missing." % path)
                options["voice_clone_prompt"] = clone_prompt(path, (request.get("refText") or "").strip())
            elif mode == "design" and (request.get("instruct") or "").strip():
                options["instruct"] = request["instruct"].strip()
            audio = model.generate(**options)
            wav, seconds = to_wav_bytes(audio[0], sample_rate)
            say("RESULT", json.dumps({"id": request_id, "wav": base64.b64encode(wav).decode("ascii"), "seconds": round(seconds, 2), "sampleRate": sample_rate}))
        except Exception as error:  # noqa: BLE001
            traceback.print_exc(file=sys.stderr)
            say("ERROR", json.dumps({"id": request_id, "message": "%s: %s" % (type(error).__name__, error)}))
            try:
                torch.cuda.empty_cache()
            except Exception:  # noqa: BLE001
                pass


def main():
    if len(sys.argv) != 3 or sys.argv[1] not in ("check", "serve"):
        say("ERROR", json.dumps({"id": "", "message": "usage: omnivoice_bridge.py check|serve <job.json>"}))
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
