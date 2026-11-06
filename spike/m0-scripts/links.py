import re, sys
ESC, BS = b"\x1b", b"\x5c"
pat = re.compile(re.escape(ESC + b"]8;") + rb"id=[^;]*;([^\x1b]*)" + re.escape(ESC + BS) + rb"([^\x1b]*)" + re.escape(ESC + b"]8;;"))
for f in sys.argv[1:]:
    d = open(f"logs/{f}.raw", "rb").read()
    links = sorted({(a.decode(), b.decode("utf-8")) for a, b in pat.findall(d)})
    env = sorted({e.decode() for e in re.findall(rb"environment=(env_[A-Za-z0-9]+)", d)})
    print(f, env, links)
