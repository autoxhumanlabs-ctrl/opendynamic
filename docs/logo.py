"""Draws the OpenDynamic logo: a Japanese dragon (ryu) coiled around a Dynamic Island pill.

Bold one-colour silhouette with negative-space details, in the style of minimalist animal marks.
The body is a tapering spiral computed below, with dorsal spines along its back; the head, horns, mane
and whiskers are hand-placed shapes attached to the neck.

    python docs/logo.py          -> docs/logo.svg (black), docs/logo-white.svg (white)
"""
import math
from pathlib import Path

S = 512                     # canvas
CX, CY = 256, 300           # coil centre
R0, R1 = 160, 104           # spiral radius at the neck -> at the tail
W0, W1 = 66, 4              # body width at the neck -> at the tail
A0, A1 = math.radians(-62), math.radians(-62 + 285)   # neck (upper right) -> tail (upper left), clockwise
HEAD_TILT, HEAD_SCALE = 14, 1.2                       # head faces left toward the tail, snout tilted up
N = 160


def lerp(a, b, t):
    return a + (b - a) * t


def spine(t):
    """Centre point, unit outward normal and half-width at body parameter t (0 = neck, 1 = tail)."""
    a = lerp(A0, A1, t)
    r = lerp(R0, R1, t ** 0.9)
    w = lerp(W0, W1, t ** 0.8) / 2
    return (CX + r * math.cos(a), CY + r * math.sin(a)), (math.cos(a), math.sin(a)), w


def body():
    outer, inner = [], []
    for i in range(N + 1):
        (x, y), (nx, ny), w = spine(i / N)
        outer.append((x + nx * w, y + ny * w))
        inner.append((x - nx * w, y - ny * w))
    pts = outer + inner[::-1]
    return "M" + " L".join(f"{x:.1f},{y:.1f}" for x, y in pts) + " Z"


def spines():
    """Saw-tooth dorsal fin on the outside of the coil, shrinking toward the tail."""
    d = []
    k = 0.10
    while k < 0.80:
        (x, y), (nx, ny), w = spine(k)
        (x2, y2), _, _ = spine(k + 0.045)
        h = lerp(30, 8, k / 0.8)
        bx, by = x + nx * (w - 2), y + ny * (w - 2)
        ex, ey = x2 + nx * (w - 2), y2 + ny * (w - 2)
        # tip leans back toward the tail, like a swept fin
        tx, ty = ex + nx * h + (ex - bx) * 0.35, ey + ny * h + (ey - by) * 0.35
        d.append(f"M{bx:.1f},{by:.1f} L{tx:.1f},{ty:.1f} L{ex:.1f},{ey:.1f} Z")
        k += 0.062
    return " ".join(d)


# Head in its own frame: neck at the origin, snout pointing +x, y down. Neck width matches W0.
HEAD = " ".join([
    # skull + upper jaw + lower jaw (open mouth is the notch between them)
    "M-10,-33 C20,-40 40,-50 62,-46 C74,-44 80,-38 92,-36 L132,-34 C140,-34 146,-42 152,-38"
    " C156,-34 152,-26 146,-22 L144,-12 L112,-10 L104,-4 L84,-2 L124,12 C126,18 120,24 112,24"
    " L78,22 C60,30 40,38 20,40 L-10,33 Z",
    # two horns sweeping back like antlers
    "M54,-44 C30,-72 0,-92 -46,-100 C-8,-84 18,-66 38,-40 Z",
    "M30,-42 C14,-60 -6,-70 -30,-74 C-6,-60 6,-50 16,-38 Z",
    # flame mane behind the jaw
    "M10,-30 L-34,-62 L-12,-30 L-48,-40 L-14,-14 L-46,-10 L-10,4 L-40,22 L-6,24 Z",
    # whisker: long tapered stroke from the nose, sweeping down and back under the jaw
    "M146,-24 C166,12 136,48 76,58 C40,64 6,56 -24,40 C8,48 42,50 72,44 C112,36 134,10 138,-14 Z",
    # chin beard
    "M100,22 C96,40 84,54 66,60 C78,46 82,34 82,22 Z",
])
EYE = "M72,-30 C80,-36 92,-34 96,-28 C88,-24 78,-24 72,-30 Z"     # punched out (negative space)
NOSTRIL = "M136,-30 C140,-32 143,-30 142,-27 C139,-26 137,-27 136,-30 Z"


def head_transform():
    (x, y), _, _ = spine(0)
    # mirrored so the snout points left, over the coil, chasing its own tail
    return f"translate({x:.1f},{y:.1f}) rotate({HEAD_TILT}) scale({-HEAD_SCALE},{HEAD_SCALE})"


def island():
    w, h = 132, 46
    return f'<rect x="{CX - w / 2}" y="{CY - h / 2 + 6}" width="{w}" height="{h}" rx="{h / 2}"/>'


def svg(fg, bg=None):
    tile = f'<rect width="{S}" height="{S}" rx="96" fill="{bg}"/>' if bg else ""
    cut = bg or "#000"
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {S} {S}" width="{S}" height="{S}">
<defs><mask id="m"><rect width="{S}" height="{S}" fill="#fff"/>
<g transform="{head_transform()}"><path d="{EYE}" fill="#000"/><path d="{NOSTRIL}" fill="#000"/></g></mask></defs>
{tile}<g fill="{fg}" mask="url(#m)" transform="translate(-18,0)">
<path d="{body()}"/><path d="{spines()}"/>
<g transform="{head_transform()}"><path d="{HEAD}"/></g>
{island()}
</g></svg>"""


def social():
    """1280x640 GitHub social preview: mark on the left, name + tagline on the right."""
    mark = svg("#F4F1EA").split(">", 1)[1].rsplit("</svg>", 1)[0]     # inner of the white-ish mark
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1280 640" width="1280" height="640">
<rect width="1280" height="640" fill="#0B0B0F"/>
<g transform="translate(110,96) scale(0.875)">{mark}</g>
<g font-family="Segoe UI, Inter, Arial, sans-serif" fill="#F4F1EA">
<text x="600" y="300" font-size="92" font-weight="700" letter-spacing="-2">OpenDynamic</text>
<text x="604" y="360" font-size="34" fill="#9A968E">Dynamic Island for Windows</text>
<text x="604" y="430" font-size="26" fill="#6E6A63">Music · Liquid Glass · Clipboard · Markets</text>
</g></svg>"""


if __name__ == "__main__":
    out = Path(__file__).parent
    (out / "logo.svg").write_text(svg("#0B0B0F"), encoding="utf-8")
    (out / "logo-white.svg").write_text(svg("#FFFFFF"), encoding="utf-8")
    (out / "logo-tile.svg").write_text(svg("#F4F1EA", "#0B0B0F"), encoding="utf-8")
    (out / "social-preview.svg").write_text(social(), encoding="utf-8")
    print("wrote", ", ".join(p.name for p in sorted(out.glob("logo*.svg"))))
