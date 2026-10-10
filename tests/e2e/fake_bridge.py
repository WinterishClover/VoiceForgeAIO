import sys, json, base64, io, wave, math, time
def say(k, t): sys.stdout.write("\n%s %s\n" % (k, t)); sys.stdout.flush()
mode = sys.argv[1]
if mode == "check": say("RESULT", json.dumps({"id": "check", "gpu": "Fake GPU"})); sys.exit(0)
say("READY", json.dumps({"model": "fake"}))
for line in sys.stdin:
    line = line.strip()
    if not line: continue
    r = json.loads(line)
    if "FAIL" in r["text"]: say("ERROR", json.dumps({"id": r["id"], "message": "RuntimeError: fake failure"})); continue
    if "SLOW" in r["text"]: time.sleep(1.5)
    b = io.BytesIO(); w = wave.open(b, "wb"); w.setnchannels(1); w.setsampwidth(2); w.setframerate(24000)
    w.writeframes(b"".join(int(math.sin(i * 0.05) * 8000).to_bytes(2, "little", signed=True) for i in range(12000))); w.close()
    say("RESULT", json.dumps({"id": r["id"], "wav": base64.b64encode(b.getvalue()).decode(), "echo": r}))
