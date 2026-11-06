"""Copy M0 captures into tests/fixtures with account ids redacted (same length), and render .txt screens."""
import re
import subprocess
import sys
from pathlib import Path

here = Path(__file__).parent
dest = Path(r"C:\Users\Steven\Github\claude-rc-windows\tests\fixtures")
dest.mkdir(parents=True, exist_ok=True)
pairs = {
    "claude-rc-ready": "B.ready",                 # git folder, both presets: straight to ready
    "claude-rc-ready-nongit": "C.ready",          # non-git folder (after an answered trust prompt)
    "claude-rc-ready-after-prompts": "A.ready",   # git folder, both prompts answered by hand, then ready
    "claude-rc-stopping": "B",                    # ready, then one Ctrl+C, until exit
    "prompt-trust": "A.trust",                    # trust prompt, unanswered
    "prompt-spawn-mode": "A.spawn",               # spawn-mode prompt, unanswered
}


def redact(data: bytes) -> bytes:
    def same_len(prefix: bytes):
        return lambda m: prefix + b"X" * (len(m.group(0)) - len(prefix))
    data = re.sub(rb"env_[A-Za-z0-9]{20,}", same_len(b"env_01"), data)
    data = re.sub(rb"session_[A-Za-z0-9]{20,}", same_len(b"session_01"), data)
    return data


for name, src in pairs.items():
    raw = redact((here / "logs" / f"{src}.raw").read_bytes())
    (dest / f"{name}.raw").write_bytes(raw)
    txt = subprocess.run([str(here / "venv" / "Scripts" / "python.exe"), str(here / "render.py"), str(dest / f"{name}.raw")],
                         capture_output=True, check=True).stdout
    (dest / f"{name}.txt").write_bytes(txt.replace(b"\r\n", b"\n"))
    last = [l for l in txt.decode("utf-8").splitlines() if l.strip()][-1]
    sys.stdout.buffer.write(f"{name}: {len(raw)} bytes, last line: {last!r}\n".encode("utf-8"))
