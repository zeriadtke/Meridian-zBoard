#!/usr/bin/env python3
"""
Erzeugt die zBoard-Wörterbücher Assets/dict/de.txt und Assets/dict/en.txt.

Quellen (per git holen, siehe README "Wörterbücher neu erzeugen"):
  --freq-de / --freq-en  FrequencyWords (OpenSubtitles 2018), CC BY-SA 4.0
                         https://github.com/hermitdave/FrequencyWords
  --nouns                german-nouns (Wiktionary), CC BY-SA 4.0
                         https://github.com/gambolputty/german-nouns
  --hunspell-de          igerman98 / de_DE Hunspell, GPLv2/GPLv3 (nur als Prüfliste, wird nicht ausgeliefert)
                         https://github.com/wooorm/dictionaries

Die Sprachmodelle für die Vorhersage (Assets/dict/<Sprache>.ngrams.bin) erzeugt tools/NgramBuilder.

Die Häufigkeitslisten sind komplett kleingeschrieben. Für Deutsch wird die Großschreibung so bestimmt:
  * Form kommt in Wiktionary als Substantiv vor UND nicht als kleingeschriebenes Wort in Hunspell
    -> nur großgeschrieben ("Haus"); zBoard schreibt "haus" dann automatisch groß.
  * Form kommt in beiden vor ("frage"/"Frage", "essen"/"Essen")
    -> beide Schreibweisen; zBoard lässt das Getippte stehen und bietet die andere an.
  * sonst kleingeschrieben.
Dazu kommen kurze Handlisten für Fälle, die die Regeln nicht erkennen (unregelmäßige Verbformen u. Ä.).

Format der Ausgabe: eine Schreibweise pro Zeile, "wort<TAB>häufigkeit".
"""
import argparse
import csv
import re

WORD = re.compile(r"^[^\W\d_][\w'\-]*$", re.UNICODE)

# Wörter, die die Regeln fälschlich als reine Substantive einstufen (Funktionswörter, unregelmäßige Verbformen)
DE_FORCE_LOWER = set("""
wenn hast nichts muss will etwas hab soll halt hatten würden letzten letzter letztes letzte schau tu werd
rum fürs wegen kamen weh scheiß hau dreh tritt steig fang is to on no my pa aye don van
ich sie ist du die es er ja mir wie aber für von nein habe gut als sein jetzt aus im weiß oder man werden mehr
bei bitte los danke tun einer werde viel weg würde am des weil heute wollen macht hallo seine zwei waren ab wohl
ob selbst gute lange ach denke drei warte bleiben unter rein ganze gegen niemand brauche halten all eins ganzen
suchen lang gewesen versuchen anders fünf hoch vier ersten kleinen allen falls stellen dies ehrlich alt frei
besten alten früh großen zehn sechs lieben leider tat suche groß versuche rufen heiraten super total halte laut
sitzen böse heißen hart tragen stolz schöne schätze anrufen hasse führen schauen perfekt acht rufe bitten hole
liegen gewinnen fort schönen klein süß tolle aller sucht heiße beweisen tief sowieso normal süße sauer näher
jung links reich schade bleibe toten manche gegenüber gehören stecken rechts entscheiden miteinander zweite
verstecken besuchen teilen feiern gelassen gefahren gebeten nahe beginnen neun melden übel leisten drehen
schließen mitten neuer hängen armen schätzen ehren reichen schreien spreche leer weißen weinen nutzen wünschen
zweiten gehalten prima stören küssen schwarzen halbe steigen junger geschossen schwarz fick rot nette langer
scheinen bösen schaffe lauter enden raten wachen verboten dunkel schick trage schicke trotz voraus streiten
schlage matt englisch roten zwölf schrieb abends heiligen leichter halben packen drücken schreibe fremden landen
extra perfekte trug verbunden prost wild träumen hauen dritten gucken anstatt langen rechten harte schwere feuern
blau gemein esse wechseln melde null schlafe starten zwingen tausend gründen frische verdächtigen menschliche
greifen per lecker irren fange real wachsen grün tollen fett achten kurze führe schenken albern starke freien
schneiden besondere rosa französisch kaufe reine linken rund leiten extrem kurzem negativ wenden plus lese laufe
daheim klingen hübscher angeboten ex nass menschlichen störe filmen besitzen süßes wilde heben tauschen senden
schalten spanisch live leihen scheiden positiv nähern
""".split())

# Mehrdeutig, aber das Substantiv dominiert so stark, dass nur die Großschreibung aufgenommen wird
DE_FORCE_NOUN = set("""
zeit angst freund ende ruhe tage bett schule teil menge plan licht erde nähe meister ärger gedanken partner
klappe rolle reise weile kraft ehre liste rücken wunder wette sonne richter soldaten schritt band feind wand
angriff ecke pause fahrt zustand geschichten reihe dienst knie grenze braut mühe hexe aussage regen fahrer
flucht song schüler präsidenten filme fälle schnauze decke gründe strafe agenten minister quelle schiffe hütte
stärke bürger klang punkte tränen schreie schnitt kaiser künstler anklage uniform zeuge verbrecher führer
verdacht probe ritter viertel miete wüste planet wissenschaftler morde dusche mitarbeiter ratte verdienst wunde
fische berge zentrale jäger indianer stahl aussagen söhne anzeige eile diamanten schütze kette terroristen
arbeiter widerstand haken berichte reporter hexen legende besucher schwäche flotte hinweise botschafter köpfe
knarre personal sinne ratten wunden beute orten kugeln dutzend lehrer patienten hure fresse arsch abend hause
""".split())

# Wörter, die als Substantiv UND klein häufig sind, von den Regeln aber nicht erkannt werden
DE_FORCE_BOTH = set("""
vertrauen verschwinden versprechen vermögen lauf ruf brauch mal morgen leben essen wissen
""".split())

# Anteil der Häufigkeit für die zweite Schreibweise bei mehrdeutigen Wörtern
SECOND_FORM_SHARE = 0.6

EN_CAPITALIZED = {"i": "I", "i'm": "I'm", "i'll": "I'll", "i've": "I've", "i'd": "I'd"}

# Die Quelle zerlegt englische Kurzformen in zwei Einträge ("don" + "'t", "i" + "'m"); die zweite Hälfte beginnt mit
# einem Apostroph und fällt als Wort heraus. Ohne die Kurzformen hält zBoard ein richtig getipptes "don't" für einen
# Tippfehler. Sie werden deshalb aus den Hälften wieder zusammengesetzt.
#
# "n't": Stämme, die nur in der Kurzform vorkommen – die Häufigkeit des Stamms ist die der Kurzform, der Stamm entfällt.
EN_NT_STEMS = """
didn doesn isn wasn wouldn couldn aren shouldn weren hasn hadn mustn needn shan daren mightn ain
""".split()
# Stämme, die auch eigene Wörter sind: (Stamm, geschätzter Anteil der Kurzform an der Häufigkeit des Stamms)
EN_NT_SHARED = {"don": 0.98, "haven": 0.98}
# Der Rest von "'t" gehört zu "can't" und "won't" (in der Quelle "can" + "'t" und "won" + "'t"); geschätzte Aufteilung.
EN_NT_REST = {"can": 0.75, "won": 0.25}
EN_NT_IRREGULAR = {"can": "can't", "won": "won't"}

# Übrige Kurzformen: geschätzter Anteil an der Häufigkeit der abgetrennten Endung. Die Quelle nennt nur die Summe je
# Endung; die Aufteilung ist eine Schätzung und bestimmt nur die Reihenfolge der Vorschläge. Was bei "'s" übrig
# bleibt, sind Genitive ("John's"); die erkennt zBoard an der Form.
EN_SUFFIX_SHARES = {
    "'m": {"i": 1.0},
    "'re": {"you": 0.62, "we": 0.20, "they": 0.15, "what": 0.01, "who": 0.005},
    "'ll": {"i": 0.45, "you": 0.16, "we": 0.14, "it": 0.06, "he": 0.06, "they": 0.06, "she": 0.03, "that": 0.03,
            "there": 0.01},
    "'ve": {"i": 0.52, "you": 0.20, "we": 0.14, "they": 0.07, "would": 0.02, "could": 0.02, "should": 0.02,
            "might": 0.005, "must": 0.005},
    "'d": {"i": 0.42, "you": 0.15, "he": 0.12, "we": 0.09, "she": 0.08, "they": 0.08, "it": 0.03, "that": 0.01,
           "who": 0.01, "there": 0.01},
    "'s": {"it": 0.21, "that": 0.15, "what": 0.08, "he": 0.07, "there": 0.055, "let": 0.05, "she": 0.04, "here": 0.03,
           "who": 0.018, "where": 0.014, "how": 0.008, "everything": 0.003, "something": 0.003, "when": 0.002},
}
# Wörter, die die Quelle am Apostroph trennt: Kurzform -> Hälfte, deren Häufigkeit übernommen wird
EN_SPLIT_WORDS = {"o'clock": "'clock", "ma'am": "'am", "y'all": "'all", "c'mon": "'mon"}
# Bruchstücke von Kurzformen, die ohne Apostroph in der Quelle stehen und keine Wörter sind
EN_FRAGMENTS = {"ll", "ve"}


def load_frequencies(path, min_freq):
    entries = []
    with open(path, encoding="utf-8") as src:
        for line in src:
            parts = line.split()
            if len(parts) < 2 or not parts[-1].isdigit():
                continue
            word, freq = parts[0], int(parts[-1])
            if freq < min_freq or not WORD.match(word) or len(word) > 40:
                continue
            entries.append((word, freq))
    entries.sort(key=lambda e: e[1], reverse=True)
    return entries


def is_nominalization(row):
    """Substantiviertes Adjektiv ("das Gute") oder substantivierter Infinitiv ("das Haben")."""
    lemma = row["lemma"]
    if "adjektivische Deklination" in row["pos"].split(","):
        return True
    return row["genus"] == "n" and re.search(r"(en|ern|eln)$", lemma) and row["nominativ plural"] in ("", lemma)


def noun_forms(path, skip_nominalizations=False):
    """Kleingeschriebene Form -> großgeschriebene Substantivform (alle Kasus, Singular und Plural)."""
    forms = {}
    with open(path, encoding="utf-8") as src:
        for row in csv.DictReader(src):
            if "Wortverbindung" in row["pos"].split(","):
                continue
            lemma = row["lemma"]
            if not lemma or not lemma[0].isupper() or " " in lemma:
                continue
            if skip_nominalizations and is_nominalization(row):
                continue
            values = [lemma] + [
                v for k, v in row.items()
                if k not in ("lemma", "pos") and v and k.split()[0] in ("nominativ", "genitiv", "dativ", "akkusativ")
            ]
            for v in values:
                if v[0].isupper() and " " not in v:
                    forms.setdefault(v.lower(), v)
    return forms


ADJ_SUFFIXES = ["e", "er", "es", "en", "em", "ere", "eren", "erer", "eres", "erem", "ste", "sten", "ster", "stes", "stem"]
VERB_SUFFIXES = ["e", "st", "t", "et", "est", "te", "test", "ten", "tet", "end", "ende", "enden", "ender", "endes"]


def lowercase_forms(path):
    """Alle Formen, die im Hunspell-Wörterbuch kleingeschrieben vorkommen (grob flektiert)."""
    out = set()
    with open(path, encoding="utf-8") as src:
        for line in src:
            parts = line.strip().split("/", 1)
            stem, flags = parts[0], (parts[1] if len(parts) > 1 else "")
            if not stem or not stem[0].islower() or not stem.isalpha():
                continue
            if "z" in flags or flags == "hke":  # Kompositions-Bestandteile, keine eigenständigen Wörter
                continue
            out.add(stem)
            if "A" in flags:  # Adjektiv
                out.update(stem + s for s in ADJ_SUFFIXES)
            if any(f in flags for f in "DIY") and stem.endswith("n"):  # Verb im Infinitiv
                if stem.endswith(("ern", "eln")):
                    root = stem[:-1]
                elif stem.endswith("en"):
                    root = stem[:-2]
                else:
                    root = stem[:-1]
                out.update(root + s for s in VERB_SUFFIXES)
                out.add("ge" + root + "t")
                out.add("ge" + root + "en")
    return out


def build_german(args):
    nouns = noun_forms(args.nouns)
    real_nouns = noun_forms(args.nouns, skip_nominalizations=True)
    lower = lowercase_forms(args.hunspell_de)
    lines = []
    count = 0
    for word, freq in load_frequencies(args.freq_de, args.min_freq):
        if len(word) < 2:
            continue
        noun = nouns.get(word)
        if noun is None or word in DE_FORCE_LOWER:
            lines.append((word, freq))
        elif word in DE_FORCE_NOUN:
            lines.append((noun, freq))
        elif word in DE_FORCE_BOTH or (word in lower and word in real_nouns):
            lines.append((word, freq))
            lines.append((noun, max(1, int(freq * SECOND_FORM_SHARE))))
        elif word in lower:
            lines.append((word, freq))  # nur Nominalisierung ("haben"/"das Haben"): klein lassen
        else:
            lines.append((noun, freq))
        count += 1
        if count >= args.limit_de:
            break
    return lines


def raw_counts(path, wanted):
    """Häufigkeiten einzelner Einträge direkt aus der Quelle (auch solcher, die load_frequencies verwirft)."""
    counts = {}
    with open(path, encoding="utf-8") as src:
        for line in src:
            parts = line.split()
            if len(parts) == 2 and parts[0] in wanted and parts[1].isdigit():
                counts[parts[0]] = int(parts[1])
    return counts


def english_contractions(path):
    """
    Setzt die englischen Kurzformen aus den Hälften der Quelle wieder zusammen.
    Ergebnis: (Kurzform -> Häufigkeit, Stamm -> Häufigkeit, die ihm abgezogen wird; None = Stamm entfällt).
    """
    suffixes = set(EN_SUFFIX_SHARES) | {"'t"} | set(EN_SPLIT_WORDS.values())
    stems = set(EN_NT_STEMS) | set(EN_NT_SHARED) | set(EN_NT_REST)
    counts = raw_counts(path, suffixes | stems)

    contractions, deduct = {}, {}
    rest = counts.get("'t", 0)
    for stem in EN_NT_STEMS:
        freq = counts.get(stem, 0)
        contractions[stem + "'t"] = freq
        deduct[stem] = None
        rest -= freq
    for stem, share in EN_NT_SHARED.items():
        freq = round(counts.get(stem, 0) * share)
        contractions[stem + "'t"] = freq
        deduct[stem] = freq
        rest -= freq
    for stem, share in EN_NT_REST.items():
        freq = min(round(max(rest, 0) * share), counts.get(stem, 0) // 2)
        contractions[EN_NT_IRREGULAR[stem]] = freq
        deduct[stem] = freq

    for suffix, shares in EN_SUFFIX_SHARES.items():
        for stem, share in shares.items():
            contractions[stem + suffix] = round(counts.get(suffix, 0) * share)
    for word, half in EN_SPLIT_WORDS.items():
        contractions[word] = counts.get(half, 0)
    return {w: f for w, f in contractions.items() if f > 0}, deduct


def build_english(args):
    contractions, deduct = english_contractions(args.freq_en)
    entries = []
    for word, freq in load_frequencies(args.freq_en, args.min_freq):
        if len(word) < 2 and word not in ("a", "i"):
            continue
        if word in EN_FRAGMENTS or word in contractions:
            continue
        if word in deduct:
            if deduct[word] is None:
                continue
            freq -= deduct[word]
        entries.append((word, freq))
    entries.extend(contractions.items())
    entries.sort(key=lambda e: e[1], reverse=True)  # stabil: gleiche Häufigkeit behält die Reihenfolge der Quelle
    return [(EN_CAPITALIZED.get(word, word), freq) for word, freq in entries[:args.limit_en]]


TYPO_KEEP = {"beriet", "gnu"}


def drop_transposition_typos(lines, skip_capitalized):
    """
    Entfernt Tippfehler aus den Untertiteln ("nciht", "dsa", "waht"): seltene Wörter (< 100), die durch Tauschen
    zweier Nachbarbuchstaben zu einem mindestens 1000-mal häufigeren Wort werden.
    """
    freq, capitalized = {}, {}
    for word, f in lines:
        key = word.lower()
        freq[key] = max(freq.get(key, 0), f)
        capitalized[key] = capitalized.get(key, False) or word[0].isupper()

    typos = set()
    for key, f in freq.items():
        if f >= 100 or len(key) < 3 or key in TYPO_KEEP or (skip_capitalized and capitalized[key]):
            continue
        for i in range(len(key) - 1):
            if key[i] == key[i + 1]:
                continue
            swapped = key[:i] + key[i + 1] + key[i] + key[i + 2:]
            if freq.get(swapped, 0) >= 1000 * f:
                typos.add(key)
                break

    print(f"  {len(typos)} Tippfehler entfernt")
    return [(w, f) for w, f in lines if w.lower() not in typos]


def build_other(path, limit, min_freq):
    """Weitere Sprachen (Französisch, Spanisch, Italienisch …): Häufigkeitsliste, Einbuchstabenwörter nur mit Akzent/Apostroph."""
    lines = []
    for word, freq in load_frequencies(path, min_freq):
        if len(word) < 2 and word not in ("a", "y", "o", "e", "à", "è", "é"):
            continue
        lines.append((word, freq))
        if len(lines) >= limit:
            break
    return lines


def write(path, lines, source_note):
    with open(path, "w", encoding="utf-8") as dst:
        dst.write(f"# {source_note}\n")
        for word, freq in lines:
            dst.write(f"{word}\t{freq}\n")
    print(f"{path}: {len(lines)} Zeilen")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--freq-de", required=True)
    p.add_argument("--freq-en", required=True)
    p.add_argument("--nouns", required=True)
    p.add_argument("--hunspell-de", required=True)
    p.add_argument("--out-dir", default="Assets/dict")
    p.add_argument("--limit-de", type=int, default=120_000)
    p.add_argument("--limit-en", type=int, default=100_000)
    p.add_argument("--min-freq", type=int, default=3)
    p.add_argument("--extra", action="append", default=[], metavar="SPRACHE:HÄUFIGKEITEN",
                   help="weitere Sprache, z. B. fr:fr_full.txt (mehrfach möglich)")
    p.add_argument("--limit-extra", type=int, default=80_000)
    args = p.parse_args()

    write(f"{args.out_dir}/de.txt", drop_transposition_typos(build_german(args), skip_capitalized=True),
          "Quellen: FrequencyWords (OpenSubtitles 2018, CC BY-SA 4.0), Großschreibung nach german-nouns/Wiktionary (CC BY-SA 4.0)")
    write(f"{args.out_dir}/en.txt", drop_transposition_typos(build_english(args), skip_capitalized=False),
          "Quelle: FrequencyWords (OpenSubtitles 2018, CC BY-SA 4.0)")
    for spec in args.extra:
        lang, freq_path = spec.split(":")[:2]
        write(f"{args.out_dir}/{lang}.txt",
              drop_transposition_typos(build_other(freq_path, args.limit_extra, args.min_freq), skip_capitalized=False),
              "Quelle: FrequencyWords (OpenSubtitles 2018, CC BY-SA 4.0)")


if __name__ == "__main__":
    main()
