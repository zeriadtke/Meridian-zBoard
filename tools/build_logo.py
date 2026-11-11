#!/usr/bin/env python3
"""
Erzeugt das zBoard-Logo in den Farben von World of Bytes (world-of-bytes.de).

    python3 tools/build_logo.py

Ausgabe:
  docs/logo/zboard-logo.svg                      – Logo als SVG (README, Web)
  docs/logo/zboard-logo-transparent.svg          – Logo ohne Hintergrundfläche
  docs/logo/plugin-<name>.svg                    – Symbole der Plugin-Apps
  docs/store/icon-512.svg                        – Play-Store-Symbol (PNG via tools/render_store_graphics.js)
  Resources/drawable/ic_launcher_*.xml           – adaptives App-Symbol (Hintergrund, Vordergrund, einfarbig)
  Resources/mipmap-anydpi-v26/ic_launcher(.xml|_round.xml)
  plugin-apps/<Name>/Resources/...               – dasselbe für jede Plugin-App, mit Plugin-Abzeichen

Motiv: eine weiße Tasten-Blase mit schwarzem „z“, die unten spitz zuläuft wie ein Cursor und auf eine
Tastatur im Markenverlauf Limette → Grün → Cyan → Blau zeigt. Die Tasten sind Punkte (wie die
Punkt-Weltkugel von World of Bytes). Plugin-Apps tragen zusätzlich unten rechts ein rundes Abzeichen
mit ihrem Symbol.
"""
import math
import os

# Farben von world-of-bytes.de (CSS-Variablen)
BG = "#07090D"
SURFACE = "#0E131B"
LINE = "#1E2733"
GRADIENT = [(0.0, "#B1C740"), (0.35, "#6BA942"), (0.70, "#3FA9D9"), (1.0, "#387ABE")]
BUBBLE_TOP, BUBBLE_BOTTOM = "#FFFFFF", "#E6EBF0"
INK = "#0B0D10"          # das schwarze „z“
KEY_ALPHA = 0.42         # Tastenpunkte: Hintergrundfarbe, halb durchsichtig auf dem Verlauf

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SIZE = 108  # Kantenlänge eines adaptiven Symbols in dp; sichtbare Sicherheitszone: Kreis mit 66 dp

# ---------- Geometrie (Entwurfsraster 512 × 512) ----------

KB = (76, 250, 360, 180, 40)                    # Tastatur: x, y, Breite, Höhe, Eckradius
KEY_R = 10
_ROW = [118 + i * (394 - 118) / 7 for i in range(8)]
KEYS = ([(x, 296) for x in (118, 158, 354, 394)]          # obere Reihe (Mitte liegt unter der Blase)
        + [(x, 340) for x in _ROW]
        + [(x, 386) for x in (118, 158, 354, 394)])
SPACE = (186, 376, 140, 20, 10)                 # Leertaste

# Blase: oben abgerundet, unten spitz zulaufend wie ein Cursor
BUBBLE = [("M", 200, 64), ("L", 312, 64), ("Q", 360, 64, 360, 112), ("L", 360, 204),
          ("Q", 360, 226, 343, 242), ("L", 268, 310), ("Q", 256, 321, 244, 310),
          ("L", 169, 242), ("Q", 152, 226, 152, 204), ("L", 152, 112), ("Q", 152, 64, 200, 64), ("Z",)]
Z = [(204, 110), (308, 110), (308, 136), (240, 190), (308, 190), (308, 216), (204, 216), (204, 190),
     (272, 136), (204, 136)]
Z_ROUND = 6                                     # Strichbreite, mit der die Ecken des „z“ gerundet werden

# Plugin-Abzeichen unten rechts
BADGE = (372, 372, 66)                          # Mittelpunkt x, y, Radius
BADGE_ICON = 80                                 # Kantenlänge des Material-Symbols im Abzeichen

# Material Symbols (Apache License 2.0), viewBox 0 -960 960 960
PLUGINS = {
    "DeepL": ("translate",
              "m476-80 182-480h84L924-80h-84l-43-122H603L560-80h-84ZM160-200l-56-56 202-202q-35-35-63.5-80T190-640h84q20 39 40 68t48 58q33-33 68.5-92.5T484-720H40v-80h280v-80h80v80h280v80H564q-21 72-63 148t-83 116l96 98-30 82-122-125-202 201Zm468-72h144l-72-204-72 204Z"),
    "Sync": ("cloud_sync",
             "M160-160v-80h109q-51-44-80-106t-29-134q0-112 68-197.5T400-790v84q-70 25-115 86.5T240-480q0 54 21.5 99.5T320-302v-98h80v240H160Zm440 0q-50 0-85-35t-35-85q0-48 33-82.5t81-36.5q17-36 50.5-58.5T720-480q53 0 91.5 34.5T858-360q42 0 72 29t30 70q0 42-29 71.5T860-160H600Zm116-360q-7-41-27-76t-49-62v98h-80v-240h240v80H691q43 38 70.5 89T797-520h-81ZM600-240h260q8 0 14-6t6-14q0-8-6-14t-14-6h-70v-50q0-29-20.5-49.5T720-400q-29 0-49.5 20.5T650-330v10h-50q-17 0-28.5 11.5T560-280q0 17 11.5 28.5T600-240Zm120-80Z"),
}


class Space:
    """Bildet das 512er-Entwurfsraster auf ein Zielraster ab (Skalierung + Verschiebung)."""

    def __init__(self, scale=1.0, dx=0.0, dy=0.0):
        self.s, self.dx, self.dy = scale, dx, dy

    def p(self, x, y):
        return f"{x * self.s + self.dx:.2f},{y * self.s + self.dy:.2f}"

    def n(self, v):
        return f"{v * self.s:.2f}"

    def xy(self, x, y):
        return x * self.s + self.dx, y * self.s + self.dy


DESIGN = Space()
# Android: Motiv (inkl. Abzeichen) in den Sicherheitskreis einpassen; (256, 277) ist der Mittelpunkt
# des kleinsten Kreises um Blase und Tastatur
A_SCALE, A_CENTER = 0.148, (256, 277)
ANDROID = Space(A_SCALE, 54 - A_CENTER[0] * A_SCALE, 54 - A_CENTER[1] * A_SCALE)


def bubble_path(sp, dy=0.0):
    out = []
    for cmd in BUBBLE:
        if cmd[0] == "Z":
            out.append("Z")
        elif cmd[0] == "Q":
            out.append(f"Q{sp.p(cmd[1], cmd[2] + dy)} {sp.p(cmd[3], cmd[4] + dy)}")
        else:
            out.append(f"{cmd[0]}{sp.p(cmd[1], cmd[2] + dy)}")
    return "".join(out)


def rrect_path(sp, x, y, w, h, r):
    rr = sp.n(r)
    return (f"M{sp.p(x + r, y)}L{sp.p(x + w - r, y)}A{rr},{rr} 0 0,1 {sp.p(x + w, y + r)}"
            f"L{sp.p(x + w, y + h - r)}A{rr},{rr} 0 0,1 {sp.p(x + w - r, y + h)}"
            f"L{sp.p(x + r, y + h)}A{rr},{rr} 0 0,1 {sp.p(x, y + h - r)}"
            f"L{sp.p(x, y + r)}A{rr},{rr} 0 0,1 {sp.p(x + r, y)}Z")


def circle_path(sp, cx, cy, r):
    rr = sp.n(r)
    return f"M{sp.p(cx - r, cy)}A{rr},{rr} 0 1,0 {sp.p(cx + r, cy)}A{rr},{rr} 0 1,0 {sp.p(cx - r, cy)}Z"


def z_path(sp):
    return "M" + "L".join(sp.p(x, y) for x, y in Z) + "Z"


def keys_path(sp):
    return "".join(circle_path(sp, x, y, KEY_R) for x, y in KEYS) + rrect_path(sp, *SPACE)


def keyboard_path(sp):
    return rrect_path(sp, *KB)


def grad_ends(sp):
    x, y, w, h, _ = KB
    return sp.xy(x, y), sp.xy(x + w, y + h)


def icon_transform(sp):
    """Material-Symbol (0 -960 960 960) mittig ins Abzeichen: Skalierung und Verschiebung."""
    cx, cy, _ = BADGE
    k = BADGE_ICON / 960 * sp.s
    tx, ty = sp.xy(cx - BADGE_ICON / 2, cy + BADGE_ICON / 2)
    return k, tx, ty


# ---------- SVG ----------

def svg(px=512, background="rounded", plugin=None):
    """background: "rounded" (Kachel mit runden Ecken), "square" (Play Store) oder None (transparent)."""
    sp = DESIGN
    stops = "".join(f'<stop offset="{o}" stop-color="{c}"/>' for o, c in GRADIENT)
    (gx1, gy1), (gx2, gy2) = grad_ends(sp)
    rx = 112 if background == "rounded" else 0
    tile = ""
    if background:
        tile = f'''  <rect width="512" height="512" rx="{rx}" fill="{BG}"/>
  <rect width="512" height="512" rx="{rx}" fill="url(#glow)"/>
'''
        if background == "rounded":
            tile += f'  <rect x="1.5" y="1.5" width="509" height="509" rx="110.5" fill="none" stroke="{LINE}" stroke-width="3"/>\n'
    badge = ""
    if plugin:
        name, icon = PLUGINS[plugin]
        k, tx, ty = icon_transform(sp)
        cx, cy, r = BADGE
        badge = f'''  <circle cx="{cx}" cy="{cy}" r="{r + 8}" fill="{BG}"/>
  <circle cx="{cx}" cy="{cy}" r="{r}" fill="{SURFACE}" stroke="url(#wob)" stroke-width="8"/>
  <path transform="translate({tx:.2f} {ty:.2f}) scale({k:.5f})" fill="#FFFFFF" d="{icon}"/>
'''
    title = "zBoard" + (f" – {plugin}" if plugin else "")
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="{px}" height="{px}">
  <title>{title}</title>
  <defs>
    <linearGradient id="wob" gradientUnits="userSpaceOnUse" x1="{gx1}" y1="{gy1}" x2="{gx2}" y2="{gy2}">{stops}</linearGradient>
    <radialGradient id="glow" gradientUnits="userSpaceOnUse" cx="430" cy="70" r="300">
      <stop offset="0" stop-color="#387ABE" stop-opacity="0.35"/><stop offset="1" stop-color="#387ABE" stop-opacity="0"/>
    </radialGradient>
    <linearGradient id="bubble" gradientUnits="userSpaceOnUse" x1="0" y1="64" x2="0" y2="320">
      <stop offset="0" stop-color="{BUBBLE_TOP}"/><stop offset="1" stop-color="{BUBBLE_BOTTOM}"/>
    </linearGradient>
    <filter id="shadow" x="-30%" y="-30%" width="160%" height="170%">
      <feDropShadow dx="0" dy="10" stdDeviation="12" flood-color="#000" flood-opacity="0.45"/>
    </filter>
  </defs>
{tile}  <path d="{keyboard_path(sp)}" fill="url(#wob)"/>
  <path d="{keys_path(sp)}" fill="{BG}" fill-opacity="{KEY_ALPHA}"/>
  <path d="{bubble_path(sp)}" fill="url(#bubble)" filter="url(#shadow)"/>
  <path d="{z_path(sp)}" fill="{INK}" stroke="{INK}" stroke-width="{Z_ROUND}" stroke-linejoin="round"/>
{badge}</svg>
'''


# ---------- Android VectorDrawables ----------

def linear_gradient_xml(attr, start, end, items, indent=4):
    pad = " " * indent
    rows = "\n".join(f'{pad}    <item android:offset="{o}" android:color="{c}"/>' for o, c in items)
    return (f'{pad}<aapt:attr name="android:{attr}">\n'
            f'{pad}  <gradient android:type="linear" android:startX="{start[0]:.2f}" android:startY="{start[1]:.2f}"'
            f' android:endX="{end[0]:.2f}" android:endY="{end[1]:.2f}">\n{rows}\n{pad}  </gradient>\n{pad}</aapt:attr>')


def vector(body, aapt=False):
    ns = ' xmlns:aapt="http://schemas.android.com/aapt"' if aapt else ""
    return f'''<?xml version="1.0" encoding="utf-8"?>
<!-- Erzeugt von tools/build_logo.py – nicht von Hand ändern. -->
<vector xmlns:android="http://schemas.android.com/apk/res/android"{ns}
    android:width="{SIZE}dp" android:height="{SIZE}dp"
    android:viewportWidth="{SIZE}" android:viewportHeight="{SIZE}">
{body}
</vector>
'''


def foreground(plugin=None):
    sp = ANDROID
    start, end = grad_ends(sp)
    wob = linear_gradient_xml("fillColor", start, end, GRADIENT)
    bubble = linear_gradient_xml("fillColor", sp.xy(0, 64), sp.xy(0, 320),
                                 [(0, BUBBLE_TOP), (1, BUBBLE_BOTTOM)])
    body = f'''  <path android:pathData="{keyboard_path(sp)}">
{wob}
  </path>
  <path android:fillColor="{BG}" android:fillAlpha="{KEY_ALPHA}" android:pathData="{keys_path(sp)}"/>
  <path android:fillColor="#000000" android:fillAlpha="0.28" android:pathData="{bubble_path(sp, dy=9)}"/>
  <path android:pathData="{bubble_path(sp)}">
{bubble}
  </path>
  <path android:fillColor="{INK}" android:strokeColor="{INK}" android:strokeWidth="{sp.n(Z_ROUND)}"
      android:strokeLineJoin="round" android:pathData="{z_path(sp)}"/>'''
    if plugin:
        _, icon = PLUGINS[plugin]
        cx, cy, r = BADGE
        k, tx, ty = icon_transform(sp)
        ring = linear_gradient_xml("strokeColor", start, end, GRADIENT)
        body += f'''
  <path android:fillColor="{BG}" android:pathData="{circle_path(sp, cx, cy, r + 8)}"/>
  <path android:fillColor="{SURFACE}" android:strokeWidth="{sp.n(8)}" android:pathData="{circle_path(sp, cx, cy, r)}">
{ring}
  </path>
  <group android:translateX="{tx:.2f}" android:translateY="{ty:.2f}" android:scaleX="{k:.5f}" android:scaleY="{k:.5f}">
    <path android:fillColor="#FFFFFFFF" android:pathData="{icon}"/>
  </group>'''
    return vector(body, aapt=True)


def background():
    return vector(f'''  <path android:fillColor="{BG}" android:pathData="M0,0h{SIZE}v{SIZE}h-{SIZE}z"/>
  <path android:pathData="M0,0h{SIZE}v{SIZE}h-{SIZE}z">
    <aapt:attr name="android:fillColor">
      <gradient android:type="radial" android:centerX="90" android:centerY="14" android:gradientRadius="64">
        <item android:offset="0" android:color="#59387ABE"/>
        <item android:offset="1" android:color="#00387ABE"/>
      </gradient>
    </aapt:attr>
  </path>''', aapt=True)


def monochrome(plugin=None):
    """Einfarbige Ebene (Android 13+): Tastatur halb deckend mit ausgesparten Tasten, Blase mit ausgespartem „z“."""
    sp = ANDROID
    body = f'''  <path android:fillColor="#FFFFFFFF" android:fillAlpha="0.55" android:fillType="evenOdd"
      android:pathData="{keyboard_path(sp)}{keys_path(sp)}"/>
  <path android:fillColor="#FFFFFFFF" android:fillType="evenOdd" android:pathData="{bubble_path(sp)}{z_path(sp)}"/>'''
    if plugin:
        _, icon = PLUGINS[plugin]
        k, tx, ty = icon_transform(sp)
        body += f'''
  <group android:translateX="{tx:.2f}" android:translateY="{ty:.2f}" android:scaleX="{k:.5f}" android:scaleY="{k:.5f}">
    <path android:fillColor="#FFFFFFFF" android:pathData="{icon}"/>
  </group>'''
    return vector(body)


ADAPTIVE = '''<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
  <background android:drawable="@drawable/ic_launcher_background"/>
  <foreground android:drawable="@drawable/ic_launcher_foreground"/>
  <monochrome android:drawable="@drawable/ic_launcher_monochrome"/>
</adaptive-icon>
'''


def write(rel, text):
    path = os.path.join(ROOT, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(rel)


def check_safe_zone():
    """Alles muss im Sicherheitskreis (Radius 33–34 dp) liegen, sonst schneiden runde Masken das Motiv an."""
    x, y, w, h, r = KB
    pts = [(x + r - r * math.cos(a), y + h - r + r * math.sin(a)) for a in [i * math.pi / 40 for i in range(21)]]
    pts += [(x + w - r + r * math.cos(a), y + h - r + r * math.sin(a)) for a in [i * math.pi / 40 for i in range(21)]]
    pts += [(x + r - r * math.cos(a), y + r - r * math.sin(a)) for a in [i * math.pi / 40 for i in range(21)]]
    pts += [(x + w - r + r * math.cos(a), y + r - r * math.sin(a)) for a in [i * math.pi / 40 for i in range(21)]]
    pts += [(166, 78), (346, 78), (200, 64), (312, 64)]
    cx, cy, br = BADGE
    pts += [(cx + (br + 8) * math.cos(a), cy + (br + 8) * math.sin(a)) for a in [i * math.pi / 36 for i in range(72)]]
    worst = max(math.dist(ANDROID.xy(px, py), (54, 54)) for px, py in pts)
    # 33 dp garantiert jede Maske, runde Masken zeigen 36 dp – 34 dp lässt nur den Schatten der Ecken knapp anschneiden
    assert worst <= 34.2, f"Motiv ragt aus dem Sicherheitskreis: {worst:.1f} dp"


if __name__ == "__main__":
    check_safe_zone()
    write("docs/logo/zboard-logo.svg", svg())
    write("docs/logo/zboard-logo-transparent.svg", svg(background=None))
    # Play Store: quadratisch ohne eigene Rundung (Google rundet selbst ab); PNG via tools/render_store_graphics.js
    write("docs/store/icon-512.svg", svg(512, background="square"))
    write("Resources/drawable/ic_launcher_foreground.xml", foreground())
    write("Resources/drawable/ic_launcher_background.xml", background())
    write("Resources/drawable/ic_launcher_monochrome.xml", monochrome())
    write("Resources/mipmap-anydpi-v26/ic_launcher.xml", ADAPTIVE)
    write("Resources/mipmap-anydpi-v26/ic_launcher_round.xml", ADAPTIVE)
    for plugin in PLUGINS:
        base = f"plugin-apps/{plugin}/Resources"
        write(f"docs/logo/plugin-{plugin.lower()}.svg", svg(plugin=plugin))
        write(f"{base}/drawable/ic_launcher_foreground.xml", foreground(plugin))
        write(f"{base}/drawable/ic_launcher_background.xml", background())
        write(f"{base}/drawable/ic_launcher_monochrome.xml", monochrome(plugin))
        write(f"{base}/mipmap-anydpi-v26/ic_launcher.xml", ADAPTIVE)
