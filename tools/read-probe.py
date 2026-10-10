"""Select a small part of the private runtime FSM inventory (never package the dump)."""
import sys, re
from pathlib import Path

blocks = re.split(r"^FSM\t", Path(sys.argv[1]).read_text(encoding="utf-8-sig"), flags=re.MULTILINE)[1:]
for pattern in sys.argv[2:]:
    hits = [b for b in blocks if pattern.casefold() in b.splitlines()[0].casefold()]
    print("MATCH", pattern, len(hits))
    for b in hits[:2]:
        lines = b.splitlines()
        if "--details" not in sys.argv:
            lines = [lines[0]] + [x for x in lines[1:] if x.startswith(("STATE\t", "TRANS\t", "VAR\t"))]
        print("FSM\t" + "\n".join(lines)[:24000])
