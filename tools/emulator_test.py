#!/usr/bin/env python3
"""
Emulator-Test für zBoard (läuft in GitHub Actions, geht aber auch lokal mit laufendem Emulator).

    python3 tools/emulator_test.py <pfad-zur-debug.apk> <ausgabeordner>

Installiert die Debug-APK, macht zBoard zur aktiven Tastatur, öffnet das Testfeld (TypingTestActivity) und
tippt echte Fingertipps und eine Wischgeste. Geprüft wird der Text im Eingabefeld. Die Tastenpositionen schreibt
die Debug-Version von zBoard ins Log (Tag "zBoardTest"). Screenshots, Log und eine Zusammenfassung landen im
Ausgabeordner; in GitHub Actions erscheint die Zusammenfassung zusätzlich auf der Seite des Laufs.
"""
import os
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
import zlib

PACKAGE = "de.World_of_Bytes.zBoard"
IME = f"{PACKAGE}/.ZBoardImeService"
TEST_ACTIVITY = f"{PACKAGE}/{PACKAGE}.TypingTestActivity"


def adb(*args, check=True, timeout=120):
    result = subprocess.run(["adb", *args], capture_output=True, text=True, timeout=timeout)
    if check and result.returncode != 0:
        raise RuntimeError(f"adb {' '.join(args)} fehlgeschlagen: {result.stderr.strip()}")
    return result.stdout


def shell(command, check=True):
    return adb("shell", command, check=check)


class Keyboard:
    """Tastenpositionen aus dem Log der Debug-Version."""

    def __init__(self):
        self.layouts = {}

    def refresh(self):
        log = adb("logcat", "-d", "-s", "zBoardTest:I", check=False)
        for line in log.splitlines():
            match = re.search(r"KEYS (\S+) (.*)$", line)
            if not match:
                continue
            keys = {}
            for item in match.group(2).split():
                label, _, pos = item.rpartition("@")
                x, y = pos.split(",")
                keys[label] = (int(x), int(y))
            self.layouts[match.group(1)] = keys  # spätere Zeilen überschreiben frühere

    def wait_for(self, layout, timeout=150):
        """Wartet, bis das Layout gemeldet ist und seine Position 2 s lang unverändert bleibt."""
        end = time.time() + timeout
        last, stable_since = None, None
        while time.time() < end:
            self.refresh()
            current = self.layouts.get(layout)
            if current is not None:
                if current != last:
                    last, stable_since = current, time.time()
                elif time.time() - stable_since >= 2:
                    return current
            time.sleep(0.5)
        print("--- Diagnose ---")
        print(shell("dumpsys input_method | grep -E 'mInputShown|mCurMethodId|mCurId|mShowRequested|mVisibleBound|isShown'", check=False))
        print(shell("dumpsys activity activities | grep -E 'mResumedActivity|topResumedActivity'", check=False))
        print("Absturz-Log:\n" + adb("logcat", "-d", "-b", "crash", check=False)[-4000:])
        log = adb("logcat", "-d", check=False).splitlines()
        noise = r"InputMethodEntryManager|GoogleInputMethodService|LatinCommon|SystemSubtypes|DictionarySuperpacks|ApkAssets"
        relevant = [l for l in log
                    if re.search(r"zboard|ZBoard|monodroid|AndroidRuntime|mono|DOTNET|InputMethodManagerService|ImeTracker|FATAL", l)
                    and not re.search(noise, l)]
        print("Log (Auszug):\n" + "\n".join(relevant[-120:]))
        raise RuntimeError(f"Tastatur mit Layout '{layout}' erscheint nicht (keine Tastenpositionen im Log)")


def wait_for_ime(timeout=60):
    """Android registriert eine frisch installierte Tastatur mit Verzögerung."""
    end = time.time() + timeout
    while time.time() < end:
        if IME in shell("ime list -a -s", check=False):
            return
        time.sleep(2)
    # Diagnose fürs Log
    print("Installierte Tastaturen:\n" + shell("ime list -a -s", check=False))
    print("Paket:\n" + shell(f"pm list packages {PACKAGE}", check=False))
    print("Dienste im Paket:\n" + shell(f"dumpsys package {PACKAGE} | grep -i -A4 'Service'", check=False)[:3000])
    raise RuntimeError(f"{IME} taucht nicht in der Liste der Tastaturen auf")


KEYBOARD = Keyboard()


def tap(pos):
    shell(f"input tap {pos[0]} {pos[1]}")
    time.sleep(0.25)


def long_press(pos, ms=700):
    shell(f"input swipe {pos[0]} {pos[1]} {pos[0]} {pos[1]} {ms}")
    time.sleep(0.4)


def type_text(keys, text):
    keys = KEYBOARD.wait_for("de")
    for ch in text:
        if ch == " ":
            tap(keys["Space"])
        else:
            tap(keys[ch.lower()])


def glide(keys, word, steps=4):
    """Wischgeste über die Buchstaben als eine Folge von Touch-Ereignissen."""
    keys = KEYBOARD.wait_for("de")
    points = [keys[c] for c in word]
    commands = [f"input motionevent DOWN {points[0][0]} {points[0][1]}"]
    for (x1, y1), (x2, y2) in zip(points, points[1:]):
        for i in range(1, steps + 1):
            x = x1 + (x2 - x1) * i // steps
            y = y1 + (y2 - y1) * i // steps
            commands.append(f"input motionevent MOVE {x} {y}")
    commands.append(f"input motionevent UP {points[-1][0]} {points[-1][1]}")
    shell("; ".join(commands))
    time.sleep(0.8)


def field_text():
    for attempt in range(3):
        for node in ui_nodes():
            if node.get("class") == "android.widget.EditText":
                return node.get("text", "")
        # Ein Systemdialog des Emulators („… isn't responding“) kann das Feld verdecken
        dismiss_system_dialogs()
        time.sleep(1)
    raise RuntimeError("Eingabefeld nicht gefunden")


def ui_nodes():
    shell("uiautomator dump /sdcard/zboard-ui.xml")
    xml = adb("exec-out", "cat", "/sdcard/zboard-ui.xml")
    return list(ET.fromstring(xml).iter("node"))


def ui_texts():
    return [n.get("text", "") for n in ui_nodes()]


def tap_text(text):
    for node in ui_nodes():
        if node.get("text") == text:
            x1, y1, x2, y2 = map(int, re.findall(r"\d+", node.get("bounds")))
            tap(((x1 + x2) // 2, (y1 + y2) // 2))
            return
    raise RuntimeError(f"„{text}“ nicht auf dem Bildschirm")


def dismiss_system_dialogs():
    """Der Emulator zeigt gelegentlich „… isn't responding“ für System-Apps: mit „Wait“ schließen."""
    try:
        for node in ui_nodes():
            if node.get("text") in ("Wait", "Warten"):
                x1, y1, x2, y2 = map(int, re.findall(r"\d+", node.get("bounds")))
                shell(f"input tap {(x1 + x2) // 2} {(y1 + y2) // 2}")
                print("Systemdialog geschlossen")
                time.sleep(1)
                return True
    except Exception:  # noqa: BLE001
        pass
    return False


def screenshot(out_dir, name):
    path = os.path.join(out_dir, f"{name}.png")
    with open(path, "wb") as f:
        f.write(subprocess.run(["adb", "exec-out", "screencap", "-p"], capture_output=True, timeout=60).stdout)
    return path


def read_png(path):
    """Liest einen Screenshot (8 Bit RGB/RGBA, ohne Interlacing) ohne Zusatzpakete: (Breite, Höhe, Bytes/Pixel, Zeilen)."""
    with open(path, "rb") as f:
        data = f.read()
    pos, idat, width = 8, b"", 0
    while pos < len(data):
        length = int.from_bytes(data[pos:pos + 4], "big")
        kind, body = data[pos + 4:pos + 8], data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            width, height = int.from_bytes(body[0:4], "big"), int.from_bytes(body[4:8], "big")
            bpp = {2: 3, 6: 4}[body[9]]
        elif kind == b"IDAT":
            idat += body
        pos += 12 + length
    raw, stride, rows, prev = zlib.decompress(idat), width * bpp, [], bytearray(width * bpp)
    for y in range(height):
        start = y * (stride + 1)
        kind, line = raw[start], bytearray(raw[start + 1:start + 1 + stride])
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0
            b, c = prev[i], prev[i - bpp] if i >= bpp else 0
            if kind == 1:
                line[i] = (line[i] + a) & 255
            elif kind == 2:
                line[i] = (line[i] + b) & 255
            elif kind == 3:
                line[i] = (line[i] + (a + b) // 2) & 255
            elif kind == 4:
                p_ = a + b - c
                pa, pb, pc = abs(p_ - a), abs(p_ - b), abs(p_ - c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(line)
        prev = line
    return width, height, bpp, rows


def ink(path, top, bottom):
    """Anteil der Pixel zwischen top und bottom, die sich deutlich von der häufigsten Farbe abheben."""
    width, _, bpp, rows = read_png(path)
    top, bottom = max(0, int(top)), min(len(rows), int(bottom))
    pixels = [tuple(rows[y][x * bpp:x * bpp + 3]) for y in range(top, bottom, 2) for x in range(0, width, 2)]
    background = max(set(pixels), key=pixels.count)
    return sum(1 for p in pixels if sum(abs(u - v) for u, v in zip(p, background)) > 120) / len(pixels)


def keyboard_top(keys):
    density = int(re.findall(r"(\d+)", shell("wm density"))[-1]) / 160
    q, a = keys.get("q") or keys["Q"], keys.get("a") or keys["A"]
    return q[1] - (a[1] - q[1]) / 2, density


def app_ink(path, keys):
    """Schrift in der App über der Tastatur (Testfeld mit getipptem Text). Das Tastaturfenster ist so hoch wie der
    Bildschirm (Platz für Popups); wäre es dort nicht durchsichtig, wäre die App verdeckt → 0."""
    top, density = keyboard_top(keys)
    return ink(path, 0, top - 60 * density)


def bar_ink(path, keys):
    """Anteil der Pixel im Streifen der Vorschlagsleiste (über der obersten Tastenreihe), die sich deutlich vom
    Hintergrund abheben. Leer gezeichnete oder übermalte Leiste → 0."""
    top, density = keyboard_top(keys)
    return ink(path, top - 44 * density, top - 10 * density)


def crashes():
    log = adb("logcat", "-d", "-b", "crash", check=False)
    return [line for line in log.splitlines() if PACKAGE in line or "FATAL" in line]


def main():
    apk, out_dir = sys.argv[1], sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)
    results = []

    def check(name, condition, detail=""):
        results.append((name, bool(condition), detail))
        print(f"{'OK  ' if condition else 'FEHL'} {name} {detail}")

    try:
        print(adb("install", "-r", apk, timeout=300).strip())
        wait_for_ime()
        shell(f"ime enable {IME}")
        shell(f"ime set {IME}")
        check("zBoard als Tastatur aktiv", IME.split("/")[0] in shell("settings get secure default_input_method"))

        adb("logcat", "-c", check=False)
        dismiss_system_dialogs()
        shell(f"am start -W -n {TEST_ACTIVITY}")
        kb = KEYBOARD
        try:
            keys = kb.wait_for("de", timeout=60)
        except RuntimeError:
            # Android hält manchmal noch die vorherige Tastatur gebunden: zBoard erneut setzen, Testfeld neu öffnen
            print("Tastatur nicht erschienen, zweiter Versuch")
            dismiss_system_dialogs()
            shell(f"ime set {IME}")
            shell(f"am start -W -S -n {TEST_ACTIVITY}")
            keys = kb.wait_for("de", timeout=120)
        time.sleep(4)  # Wörterbuch lädt im Hintergrund
        screenshot(out_dir, "1-tastatur")
        check("Tastatur erscheint", True, f"({len(keys)} Tasten)")
        check("Keine Sprachtaste (Wechsel per langem Druck auf die Leertaste)", "Language" not in keys)
        check("Deutsch wie Gboard: keine eigenen Umlaut-Tasten", not any(k in keys for k in ("ü", "ö", "ä")))

        bounds = re.findall(r"BOUNDS de bottom=(\d+) limit=(\d+) nav=(\d+)", adb("logcat", "-d", "-s", "zBoardTest:I", check=False))
        if bounds:
            bottom, limit, nav = map(int, bounds[-1])
            check("Tastatur über der Navigationsleiste", bottom <= limit,
                  f"(Unterkante {bottom}, erlaubt bis {limit}, Leiste {nav} px)")
            # Fenster in voller Höhe (Platz für Popups): die Tastatur muss trotzdem unten sitzen
            check("Tastatur sitzt unten am Bildschirm", bottom >= limit - 150, f"(Unterkante {bottom}, unten {limit})")
        else:
            check("Tastatur über der Navigationsleiste", False, "(keine Lageangabe im Log)")

        type_text(keys, "hallo ")
        text = field_text()
        check("Tippen + Großschreibung am Satzanfang", text == "Hallo ", f"→ „{text}“")

        type_text(keys, "dsa ")
        text = field_text()
        check("Autokorrektur (dsa → das)", text.endswith("das "), f"→ „{text}“")

        type_text(keys, "haus ")
        text = field_text()
        check("Substantiv großschreiben (haus → Haus)", text.endswith("Haus "), f"→ „{text}“")

        glide(keys, "gut")
        tap(keys["Space"])
        text = field_text()
        check("Wischeingabe (g-u-t)", text.endswith("gut "), f"→ „{text}“")
        visible = app_ink(screenshot(out_dir, "2-getippt"), keys)
        check("App über der Tastatur sichtbar (Fenster dort durchsichtig)", visible > 0.002, f"({visible:.2%} Schrift)")

        tap(kb.wait_for("de")["Delete"])
        time.sleep(0.5)
        text = field_text()
        check("Löschen", text.endswith("gut"), f"→ „{text}“")

        # Wie Gboard: Das häufigste Zusatzzeichen ist vorausgewählt – langer Druck auf o ohne Bewegung ergibt ö
        o = kb.wait_for("de")["o"]
        holding = subprocess.Popen(["adb", "shell", f"input swipe {o[0]} {o[1]} {o[0]} {o[1]} 2500"])
        time.sleep(1.4)
        popup_shot = screenshot(out_dir, "2b-popup-o")  # Popup sichtbar, solange der Finger liegt
        holding.wait(timeout=30)
        time.sleep(0.5)
        text = field_text()
        check("Langer Druck auf o → ö (vorausgewählt)", text.endswith("gutö"), f"→ „{text}“")
        # Wo liegt der Popup? Pro Streifen (8 dp) Anteil der Pixel, die sich vom Bild ohne Popup unterscheiden
        top, density = keyboard_top(keys)
        width, _, bpp, before = read_png(os.path.join(out_dir, "2-getippt.png"))
        _, _, _, during = read_png(popup_shot)
        x_from, x_to = max(0, int(o[0] - 5 * 40 * density)), min(width, int(o[0] + 40 * density))
        profile = []
        for y in range(int(top - 200 * density), int(top + 30 * density), int(8 * density)):
            diff = sum(1 for x in range(x_from, x_to, 3)
                       if sum(abs(a - b) for a, b in zip(before[y][x * bpp:x * bpp + 3], during[y][x * bpp:x * bpp + 3])) > 60)
            profile.append((int((y - top) / density), diff * 3 * 100 // (x_to - x_from)))
        print("  Popup-Profil (relativ zur Tastatur-Oberkante):", " ".join(f"{d:+d}dp:{p}%" for d, p in profile))
        # Zweite Zeile des Popups (9 Zeichen auf o) liegt über der Vorschlagsleiste (48 dp)
        above_bar = max((p for d, p in profile if d < -50), default=0)
        check("Popup ragt über die Vorschlagsleiste (obere Zeile sichtbar)", above_bar >= 5, f"({above_bar}% Popup-Pixel oberhalb)")
        tap(kb.wait_for("de")["Delete"])
        time.sleep(0.5)

        # Über der Tastatur ist das Fenster durchsichtig und darf Berührungen nicht schlucken: Antippen des
        # Feldanfangs in der App setzt den Cursor dorthin, der nächste Buchstabe landet vorne.
        tap(kb.wait_for("de")["Space"])  # Wort abschließen, damit nichts mehr „in Arbeit“ ist
        time.sleep(0.5)
        edit = next(n for n in ui_nodes() if n.get("class") == "android.widget.EditText")
        x1, y1, x2, y2 = map(int, re.findall(r"\d+", edit.get("bounds")))
        tap((x1 + 8, y1 + 400))  # unterhalb der Statusleiste (Testfeld reicht bis an den oberen Rand)
        time.sleep(0.8)
        tap(kb.wait_for("de")["x"])
        time.sleep(0.5)
        text = field_text()
        check("App über der Tastatur antippbar (Cursor an den Anfang)", text[:1].lower() == "x", f"→ „{text}“")
        if text[:1].lower() != "x":
            log = adb("logcat", "-d", "-s", "zBoardTest:I", check=False)
            insets = re.findall(r"INSETS .*", log)
            print("  Insets laut zBoard:", insets[-3:] if insets else "keine")
            print("  Cursor laut App (letzte):", re.findall(r"SEL .*", log)[-4:])
            print("  Antippen bei:", (x1 + 8, y1 + 400), "Feld:", (x1, y1, x2, y2))
            windows = shell("dumpsys window windows", check=False)
            for block in windows.split("Window #"):
                if "ty=INPUT_METHOD" in block:
                    print("  IME-Fenster:", " | ".join(l.strip() for l in block.splitlines()
                          if re.search(r"(?i)touchable|givencontent|givenvisible|mFrame|mAttrs|fl=|pfl=", l))[:2500])
            touch = shell("dumpsys input", check=False)
            print("  Input:", " | ".join(l.strip() for l in touch.splitlines()
                  if re.search(r"(?i)InputMethod|zboard|testfeld|TypingTest", l))[:2500])
        tap(kb.wait_for("de")["Delete"])
        time.sleep(0.5)

        tap(kb.wait_for("de")["ToSymbols"])
        symbols = kb.wait_for("symbols1", timeout=15)
        screenshot(out_dir, "3-symbole")
        check("Symbolebene", "@" in symbols)
        tap(symbols["ToAlpha"])
        time.sleep(0.5)

        tap(kb.wait_for("de")["Emoji"])
        time.sleep(2)
        screenshot(out_dir, "4-emoji")
        shell("input keyevent KEYCODE_BACK", check=False)
        time.sleep(1)

        # Suchleiste wie in der Google-App: Feld meldet NO_SUGGESTIONS, Gboard zeigt trotzdem Wortvorschläge
        shell(f"am start -W --activity-clear-task -n {TEST_ACTIVITY} --ez search true")
        time.sleep(3)
        type_text(keys, "hau")
        time.sleep(1)
        log = adb("logcat", "-d", "-s", "zBoardTest:I", check=False)
        shown = re.findall(r"SUGGESTIONS (.*)$", log, re.M)
        last = shown[-1].strip() if shown else ""
        text = field_text()
        check("Suchleiste: Wortvorschläge trotz NO_SUGGESTIONS", text == "hau" and last.strip("|") != "",
              f"→ Feld „{text}“, Leiste „{last}“")
        ink = bar_ink(screenshot(out_dir, "5-suchleiste"), keys)
        check("Vorschlagsleiste sichtbar (Screenshot)", ink > 0.002, f"({ink:.2%} Schrift im Leistenbereich)")

        # Passwortfeld: Buchstaben und Ziffern müssen ankommen (Android zeigt sie maskiert)
        shell(f"am start -W --activity-clear-task -n {TEST_ACTIVITY} --es password text")
        time.sleep(3)
        layout = KEYBOARD.wait_for("de")
        for ch in "ab":
            tap(layout[ch])
        long_press(layout["q"])  # langer Druck auf q → 1
        time.sleep(1)
        text = field_text()
        log = adb("logcat", "-d", "-s", "zBoardTest:I", check=False)
        plain = re.findall(r"FIELD (.*)$", log, re.M)
        plain = plain[-1].strip() if plain else ""
        check("Passwortfeld: Buchstaben und Ziffern", plain == "ab1", f"→ „{plain}“ (angezeigt „{text}“)")
        screenshot(out_dir, "5b-passwort")
        bounds = re.findall(r"BOUNDS de bottom=(\d+) limit=(\d+) nav=(\d+)", log)
        if bounds:
            bottom, limit, nav = map(int, bounds[-1])
            check("Nach erneutem Öffnen über der Navigationsleiste", bottom <= limit,
                  f"(Unterkante {bottom}, erlaubt bis {limit})")

        # PIN-Feld: Ziffernfeld wie bei Gboard
        shell(f"am start -W --activity-clear-task -n {TEST_ACTIVITY} --es password number")
        pad = KEYBOARD.wait_for("numpad", timeout=30)
        for ch in "12":
            tap(pad[ch])
        time.sleep(1)
        log = adb("logcat", "-d", "-s", "zBoardTest:I", check=False)
        plain = re.findall(r"FIELD (.*)$", log, re.M)
        plain = plain[-1].strip() if plain else ""
        check("PIN-Feld: Ziffernfeld", "a" not in pad and plain == "12", f"→ „{plain}“")
        screenshot(out_dir, "5c-pin")

        # Einstellungen öffnen (Startseite und eine Unterseite)
        shell("input keyevent KEYCODE_BACK", check=False)
        shell(f"am start -W --activity-clear-task -n {PACKAGE}/{PACKAGE}.SettingsActivity")
        time.sleep(3)
        screenshot(out_dir, "6-einstellungen")
        settings_ui = ui_texts()
        # Oberfläche auf Deutsch oder Englisch, je nach Systemsprache des Emulators
        german = "zBoard-Einstellungen" in settings_ui
        check("Einstellungen: Startseite", german or "zBoard settings" in settings_ui,
              "(Deutsch)" if german else "(Englisch)")
        tap_text("Korrekturen & Vorschläge" if german else "Text correction")
        time.sleep(1.5)
        screenshot(out_dir, "7-korrekturen")
        check("Einstellungen: Unterseite", ("Autokorrektur" if german else "Auto-correction") in ui_texts())
        shell("input keyevent KEYCODE_BACK", check=False)
        time.sleep(1.5)
        check("Einstellungen: Zurück zur Startseite", ("Glide Typing" if german else "Glide typing") in ui_texts())

        found = crashes()
        check("Keine Abstürze", not found, "; ".join(found[:3]))
    except Exception as e:  # noqa: BLE001 – jeder Fehler ist ein Testfehler
        check("Testablauf", False, str(e))
        try:
            screenshot(out_dir, "fehler")
        except Exception:  # noqa: BLE001
            pass
    finally:
        if os.environ.get("ZBOARD_LOG_SCREENSHOTS"):
            import base64
            for name in sorted(os.listdir(out_dir)):
                if name.endswith(".png"):
                    with open(os.path.join(out_dir, name), "rb") as f:
                        data = base64.b64encode(f.read()).decode()
                    for i in range(0, len(data), 3000):  # lange Zeilen kürzt das Log
                        print(f"SCREENSHOT {name} {i // 3000} {data[i:i + 3000]}")
        with open(os.path.join(out_dir, "logcat.txt"), "w", encoding="utf-8") as f:
            f.write(adb("logcat", "-d", check=False))
        write_summary(out_dir, results)

    sys.exit(0 if results and all(ok for _, ok, _ in results) else 1)


def write_summary(out_dir, results):
    lines = ["## zBoard im Emulator", "", "| Prüfung | Ergebnis | Detail |", "| --- | --- | --- |"]
    for name, ok, detail in results:
        lines.append(f"| {name} | {'✅' if ok else '❌'} | {detail.replace('|', '/')} |")
    lines += ["", "Screenshots und Log: Artefakt **emulator-test**."]
    summary = "\n".join(lines) + "\n"
    with open(os.path.join(out_dir, "zusammenfassung.md"), "w", encoding="utf-8") as f:
        f.write(summary)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as f:
            f.write(summary)


if __name__ == "__main__":
    main()
