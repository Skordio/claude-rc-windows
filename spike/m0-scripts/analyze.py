import re
from pathlib import Path

here = Path(__file__).parent
for f in "BDE":
    raw = (here / "logs" / f"{f}.raw").read_bytes()
    ev = (here / "logs" / f"{f}.events.txt").read_text(encoding="utf-8")
    chunks = [(float(m[0]), int(m[1]), int(m[2])) for m in re.findall(r"^\s*(\d+) ms .*out offset=(\d+) len=(\d+)", ev, re.M)]
    def t_of(marker):
        i = raw.find(marker)
        for t, off, n in chunks:
            if off <= i < off + n:
                return t
    print(f, "first 'Connected' at", t_of(b"Connected"), "ms; first 'Continue coding' at", t_of(b"Continue coding"),
          "ms; session name/Attached at", t_of(b"Attached"), "ms")

# SPEC section 8 heuristic, as written
choice = re.compile(r"\[1/2\]|\[1/2/3\]|\[y/n\]|\[yes/no\]|\(y/n\)|\(yes/no\)", re.I)
def is_prompt(line):
    s = line.strip()
    return bool(choice.search(s)) and s[-1:] in ":?])"
fx = Path(r"C:\Users\Steven\Github\claude-rc-windows\tests\fixtures")
for p in sorted(fx.glob("*.txt")):
    lines = [l for l in p.read_text(encoding="utf-8").splitlines() if l.strip()]
    hits = [l for l in lines if is_prompt(l)]
    print(f"{p.name:36} last-line prompt={is_prompt(lines[-1])!s:5}  any-line hits={hits}")
