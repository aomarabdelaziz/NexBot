"""Builds docs/NexBot-Guide.html: template.html with every @@name@@ replaced by screenshots/name.png (as WebP, embedded).

Make the screenshots with:  NexBot.exe --screenshots docs/guide/screenshots
Then run:                   python docs/guide/build.py
"""
import base64, io, os, re, sys
from PIL import Image

here = os.path.dirname(os.path.abspath(__file__))
shots = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, "screenshots")
out = os.path.join(here, "..", "NexBot-Guide.html")

html = io.open(os.path.join(here, "template.html"), encoding="utf-8").read()
cache = {}

def image(match):
    name = match.group(1)
    if name not in cache:
        path = os.path.join(shots, name + ".png")
        buffer = io.BytesIO()
        Image.open(path).convert("RGB").save(buffer, "WEBP", quality=86, method=6)
        cache[name] = "data:image/webp;base64," + base64.b64encode(buffer.getvalue()).decode("ascii")
    return cache[name]

html = re.sub(r"@@([a-z0-9_-]+)@@", image, html)
io.open(out, "w", encoding="utf-8", newline="\n").write(html)
print(f"{os.path.normpath(out)}: {len(html) / 1024 / 1024:.1f} MB, {len(cache)} images")
