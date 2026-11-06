"""Render a ConPTY raw capture to the final screen text. Usage: render.py <raw> [cols rows] [--upto N] [--stats]"""
import re
import sys

import pyte

args = sys.argv[1:]
path = args[0]
cols, rows = 120, 40
upto = None
stats = "--stats" in args
if "--upto" in args:
    upto = int(args[args.index("--upto") + 1])
nums = [a for a in args[1:3] if a.isdigit()]
if len(nums) == 2:
    cols, rows = int(nums[0]), int(nums[1])

data = open(path, "rb").read()
if upto is not None:
    data = data[:upto]
screen = pyte.Screen(cols, rows)
stream = pyte.ByteStream(screen)
# pyte loses the rest of a line after a variation selector (U+FE0E/U+FE0F); drop them.
data = data.replace(bytes.fromhex("efb88e"), b"").replace(bytes.fromhex("efb88f"), b"")
stream.feed(data)
lines = [l.rstrip() for l in screen.display]
while lines and not lines[-1]:
    lines.pop()
out = "\n".join(lines) + "\n"
sys.stdout.buffer.write(out.encode("utf-8"))
if stats:
    text = data.decode("utf-8", "replace")
    modes = sorted(set(re.findall(r"\x1b\[\?[0-9;]+[hl]", text)))
    sys.stderr.write(f"bytes={len(data)} private modes={modes!r}\n")
    sys.stderr.write(f"clears(2J)={text.count(chr(27)+'[2J')} homes(H)={len(re.findall(chr(27)+r'\[H', text))} "
                     f"cursor-moves={len(re.findall(chr(27)+r'\[[0-9]+;[0-9]+H', text))} "
                     f"newlines={text.count(chr(10))} scroll-region={len(re.findall(chr(27)+r'\[[0-9]*;[0-9]*r', text))}\n")
    sys.stderr.write(f"cursor=({screen.cursor.x},{screen.cursor.y})\n")
