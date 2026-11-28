#!/usr/bin/env python3
"""
Trainiert das neuronale Sprachmodell einer Sprache für zBoard und schreibt es nach Assets/lm/:

    <Sprache>.lm.onnx     LSTM, Gewichte int8 (ONNX Runtime, gelesen von Suggestions/OnnxLanguageModel.cs)
    <Sprache>.vocab.txt   Vokabular "wort<TAB>anteil" (gelesen von Suggestions/NeuralModel.cs)

Aufruf und Quellen: README.md. Gleiche Eingaben und gleicher --seed ergeben (auf demselben Rechner) dasselbe Modell.

Die Wörter werden genau so gelesen wie beim Tippen (WordContext.FromTextWithSentence) und wie in tools/NgramBuilder:
Wörter aus Buchstaben, Ziffern, Apostroph und Bindestrich; Komma als eigenes Zeichen; nach . ! ? … beginnt ein Satz
(<s>); jedes andere Satzzeichen unterbricht den Kontext. Wörter stehen in der Schreibweise der Wortliste.
"""

import argparse
import math
import os
import random
import sys
import time

import numpy as np
import torch
import torch.nn as nn

PAD, UNK, BOS, COMMA = "<pad>", "<unk>", "<s>", ","
SPECIALS = [PAD, UNK, BOS, COMMA]
PAD_ID, UNK_ID, BOS_ID, COMMA_ID = range(4)
MAX_CONTEXT = 24  # wie WordContext.MaxSentenceTokens
CLOSERS = "\"“”‘’»«)]"
SENTENCE_END = ".!?…"
ZH_SENTENCE_END = "。！？…"
ZH_COMMA = "，、；：,;:"


# ---------- Wortliste ----------

def lower(word, lang):
    if lang == "tr":
        word = word.replace("I", "ı").replace("İ", "i")
    return word.lower()


def load_wordlist(path, lang):
    """Liest <Sprache>.txt wie WordDictionary.Load: Häufigkeit je Schreibweise, Schreibweisen je Kleinschreibung."""
    freq = {}
    with open(path, encoding="utf-8-sig") as src:
        for line in src:
            if line.startswith("#"):
                continue
            parts = line.rstrip("\r\n").split("\t")
            if len(parts) < 2:
                continue
            word = parts[0].strip()
            try:
                n = int(parts[1].strip())
            except ValueError:
                continue
            if not word or " " in word:
                continue
            freq[word] = freq.get(word, 0) + n
    forms = {}
    for word in sorted(freq, key=lambda w: -freq[w]):
        forms.setdefault(lower(word, lang), []).append(word)
    return freq, forms


def make_form(forms, lang):
    """Schreibweise der Wortliste für ein Wort aus dem Text, wie Form() in tools/NgramBuilder; None, wenn unbekannt."""
    def form(token, at_sentence_start):
        options = forms.get(lower(token, lang))
        if not options:
            return None
        if len(options) == 1:
            return options[0]
        if not (at_sentence_start and token[0].isupper()) and token in options:
            return token
        return options[0]
    return form


# ---------- Text in Wortfolgen zerlegen ----------

def is_word_char(c):
    return c.isalnum() or c in "'-"


def segments(line):
    """
    Zerlegt eine Zeile in Folgen von Wörtern, wie die Tastatur den Kontext liest. Jede Folge beginnt mit <s> (Satzanfang)
    oder mitten im Satz (nach einem Zeichen, das den Kontext unterbricht).
    """
    line = line.replace("’", "'").strip()
    if " " not in line:
        return []  # leer oder ein einzelnes Wort (Listen von Städten u. Ä.), wie im NgramBuilder
    result, current = [], [BOS]
    pos, prev_end, n = 0, 0, len(line)
    while pos < n:
        if not is_word_char(line[pos]):
            pos += 1
            continue
        start = pos
        while pos < n and is_word_char(line[pos]):
            pos += 1
        gap = line[prev_end:start]
        prev_end = pos
        if start == 0:
            pass  # Zeilenanfang = Satzanfang, <s> steht schon
        elif not gap or not gap[-1].isspace():
            result.append(current)  # kein Leerraum davor: Kontext unterbrochen
            current = []
        else:
            core = gap.strip()
            if core:
                stripped = core.rstrip(CLOSERS)
                if stripped and stripped[-1] in SENTENCE_END:
                    result.append(current)
                    current = [BOS]
                elif core == ",":
                    current.append(COMMA)
                elif core.endswith(","):
                    result.append(current)
                    current = [COMMA]
                else:
                    result.append(current)
                    current = []
        current.append(line[start:pos])
    result.append(current)
    return [s for s in result if len(s) >= 2]


def segments_zh(line, jieba):
    """Chinesisch: Wörter mit jieba trennen (die Wortliste zh.txt kennt dieselben Wörter)."""
    line = line.strip()
    if len(line) < 2:
        return []
    result, current = [], [BOS]
    for piece in jieba.lcut(line, HMM=False):
        piece = piece.strip()
        if not piece:
            continue
        if piece[-1] in ZH_SENTENCE_END:
            result.append(current)
            current = [BOS]
        elif piece in ZH_COMMA:
            current.append(COMMA)
        elif all(is_word_char(c) for c in piece):
            current.append(piece)
        else:
            result.append(current)
            current = []
    result.append(current)
    return [s for s in result if len(s) >= 2]


# ---------- Quellen ----------

def common_voice_sources(folder):
    """Wie tools/NgramBuilder --common-voice: eingesandte Sätze 10, Europarl 1, Wikipedia 0,15; ohne Einzelwörter."""
    sources = []
    for name in sorted(os.listdir(folder)):
        if not name.endswith(".txt") or name.startswith("singleword"):
            continue
        weight = 0.15 if name.startswith("wiki") else 1.0 if "europarl" in name else 10.0
        sources.append((os.path.join(folder, name), weight))
    return sources


def parse_source(text):
    path, _, weight = text.rpartition(":")
    try:
        return (path, float(weight)) if path else (text, 1.0)
    except ValueError:
        return (text, 1.0)


def read_corpus(sources, lang, form, vocab_ids, max_lines):
    """Liest alle Quellen; liefert Wortfolgen als Id-Listen mit Gewicht und die Häufigkeit jeder Schreibweise."""
    jieba = None
    if lang == "zh":
        import jieba as jieba_module  # pip install jieba
        jieba_module.setLogLevel(60)
        jieba = jieba_module
    sequences, weights = [], []
    counts = {}
    for path, weight in sources:
        lines = kept = tokens = 0
        with open(path, encoding="utf-8", errors="replace") as src:
            for raw in src:
                lines += 1
                if max_lines and lines > max_lines:
                    break
                parts = segments_zh(raw, jieba) if jieba else segments(raw)
                for seg in parts:
                    ids, previous = [], None
                    for token in seg:
                        if token in (BOS, COMMA):
                            ids.append(BOS_ID if token == BOS else COMMA_ID)
                            previous = token
                            continue
                        word = form(token, previous == BOS)
                        previous = token
                        if word is not None:
                            counts[word] = counts.get(word, 0) + weight
                        ids.append(vocab_ids.get(word, UNK_ID) if word is not None else UNK_ID)
                    sequences.append(np.array(ids, dtype=np.int32))
                    weights.append(weight)
                    kept += 1
                    tokens += len(ids)
        print(f"  {os.path.basename(path)}: {lines} Zeilen, {kept} Wortfolgen, {tokens} Wörter, Gewicht {weight}")
    return sequences, np.array(weights, dtype=np.float64), counts


# ---------- Modell ----------

class LanguageModel(nn.Module):
    """
    Wort-Einbettung → LSTM → Projektion → Wahrscheinlichkeit jedes Vokabular-Eintrags. Mit --tie teilen sich Ein- und
    Ausgabe eine Matrix W (Einbettung × Vokabular): halb so groß bei kaum schlechterer Vorhersage.
    """

    def __init__(self, vocab, embed, hidden, tie):
        super().__init__()
        self.tie = tie
        self.W = nn.Parameter(torch.randn(embed, vocab) * 0.05)
        if not tie:
            self.out_W = nn.Parameter(torch.randn(embed, vocab) * 0.05)
        self.out_b = nn.Parameter(torch.zeros(vocab))
        self.lstm = nn.LSTM(embed, hidden, batch_first=True)
        self.proj = nn.Linear(hidden, embed)

    def embed(self, ids):
        # Spalten aus W statt einer eigenen Einbettungsmatrix: so teilen sich Ein- und Ausgabe dieselben Gewichte.
        return torch.index_select(self.W, 1, ids.reshape(-1)).t().reshape(*ids.shape, -1)

    def forward(self, ids):
        h, _ = self.lstm(self.embed(ids))
        z = self.proj(h)
        return torch.matmul(z, self.W if self.tie else self.out_W) + self.out_b


class Exported(nn.Module):
    """Für ONNX: tokens [1, Länge] → probs [1, Vokabular] für das Wort nach dem letzten Token."""

    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, tokens):
        h, _ = self.model.lstm(self.model.embed(tokens))
        z = self.model.proj(h[:, -1:, :])
        logits = torch.matmul(z, self.model.W if self.model.tie else self.model.out_W) + self.model.out_b
        return torch.softmax(logits[:, 0, :], dim=-1)


# ---------- Training ----------

def make_batch(sequences, picks, rng):
    """Fenster von höchstens MAX_CONTEXT+1 Wörtern; das Netz lernt jedes Wort aus den Wörtern davor."""
    windows = []
    for i in picks:
        seq = sequences[i]
        if len(seq) > MAX_CONTEXT + 1:
            # Lange Folgen: meist vom Anfang (Satzanfang), sonst ein zufälliges Stück
            start = 0 if rng.random() < 0.5 else rng.integers(0, len(seq) - MAX_CONTEXT - 1)
            seq = seq[start:start + MAX_CONTEXT + 1]
        windows.append(seq)
    length = max(len(w) for w in windows)
    batch = np.zeros((len(windows), length), dtype=np.int64)
    for row, w in enumerate(windows):
        batch[row, :len(w)] = w
    batch = torch.from_numpy(batch)
    return batch[:, :-1], batch[:, 1:]


def evaluate(model, sequences, device, batch_size=256):
    """Perplexität und Treffer des nächsten Worts (unter den ersten 1 und 3; ohne <unk> und Komma)."""
    model.eval()
    total_loss = total = top1 = top3 = 0
    loss_fn = nn.CrossEntropyLoss(ignore_index=PAD_ID, reduction="sum")
    rng = np.random.default_rng(0)
    with torch.no_grad():
        for start in range(0, len(sequences), batch_size):
            x, y = make_batch(sequences, range(start, min(start + batch_size, len(sequences))), rng)
            x, y = x.to(device), y.to(device)
            logits = model(x)
            total_loss += loss_fn(logits.reshape(-1, logits.shape[-1]), y.reshape(-1)).item()
            logits[:, :, :4] = -1e9  # Sonderzeichen sagt die Tastatur nicht vorher
            best = logits.topk(3, dim=-1).indices
            mask = y >= 4
            total += mask.sum().item()
            top1 += ((best[:, :, 0] == y) & mask).sum().item()
            top3 += ((best == y.unsqueeze(-1)).any(-1) & mask).sum().item()
    tokens = sum(len(s) - 1 for s in sequences)
    model.train()
    return math.exp(total_loss / max(1, tokens)), top1 / max(1, total), top3 / max(1, total)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--lang", required=True, help="Sprachcode wie in Assets/dict (de, en, zh …)")
    p.add_argument("--words", help="Wortliste; Vorgabe Assets/dict/<Sprache>.txt")
    p.add_argument("--common-voice", help="Ordner einer Sprache aus der Satzsammlung von Common Voice")
    p.add_argument("--source", action="append", default=[], help="Textdatei[:Gewicht], ein Satz pro Zeile (mehrfach möglich)")
    p.add_argument("--out", default="Assets/lm", help="Zielordner; Vorgabe Assets/lm")
    p.add_argument("--vocab", type=int, default=16000, help="so viele Wörter kennt das Netz (häufigste der Wortliste)")
    p.add_argument("--embed", type=int, default=128)
    p.add_argument("--hidden", type=int, default=256)
    p.add_argument("--no-tie", dest="tie", action="store_false", help="eigene Ausgabematrix (doppelt so groß)")
    p.add_argument("--steps", type=int, default=30000, help="Trainingsschritte")
    p.add_argument("--batch", type=int, default=128)
    p.add_argument("--lr", type=float, default=2e-3)
    p.add_argument("--max-lines", type=int, default=0, help="höchstens so viele Zeilen je Datei (zum Ausprobieren)")
    p.add_argument("--seed", type=int, default=1)
    p.add_argument("--device", default="cuda" if torch.cuda.is_available() else "cpu")
    p.add_argument("--threads", type=int, default=0, help="CPU-Threads für PyTorch (0 = Vorgabe)")
    args = p.parse_args()

    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)
    if args.threads:
        torch.set_num_threads(args.threads)
    rng = np.random.default_rng(args.seed)

    lang = args.lang
    words_path = args.words or os.path.join("Assets", "dict", f"{lang}.txt")
    sources = [parse_source(s) for s in args.source]
    if args.common_voice:
        sources = common_voice_sources(args.common_voice) + sources
    if not sources:
        p.error("keine Texte: --common-voice und/oder --source angeben")

    # Vokabular: die häufigsten Schreibweisen der Wortliste
    print(f"Wortliste {words_path}")
    freq, forms = load_wordlist(words_path, lang)
    form = make_form(forms, lang)
    top = sorted(freq, key=lambda w: (-freq[w], w))[: max(0, args.vocab - len(SPECIALS))]
    vocab = SPECIALS + top
    vocab_ids = {w: i for i, w in enumerate(vocab)}

    print("Texte:")
    sequences, weights, counts = read_corpus(sources, lang, form, vocab_ids, args.max_lines)
    if not sequences:
        sys.exit("Keine Wortfolgen gefunden")
    words_total = int(sum(len(s) for s in sequences))
    unknown = sum(int((s == UNK_ID).sum()) for s in sequences)
    print(f"{len(sequences)} Wortfolgen, {words_total} Wörter, davon {unknown / words_total:.1%} außerhalb des Vokabulars")
    if words_total < 1_000_000:
        print("Hinweis: wenig Text für ein neuronales Modell – mit --source weitere Sätze dazugeben (siehe README.md)")

    # Ein kleiner Teil (nach Gewicht gezogen, wie im Training) dient nur der Messung.
    order = rng.permutation(len(sequences))
    n_valid = min(5000, max(200, len(sequences) // 50))
    probs = weights[order] / weights[order].sum()
    valid_idx = set(rng.choice(order, size=min(n_valid, len(order)), replace=False, p=probs).tolist())
    train_idx = np.array([i for i in order if i not in valid_idx])
    valid = [sequences[i] for i in sorted(valid_idx)]
    train_p = weights[train_idx] / weights[train_idx].sum()

    device = torch.device(args.device)
    model = LanguageModel(len(vocab), args.embed, args.hidden, args.tie).to(device)
    params = sum(p.numel() for p in model.parameters())
    print(f"Netz: {params / 1e6:.2f} Mio. Gewichte, Vokabular {len(vocab)}, Gerät {device}")

    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=0.01)
    scheduler = torch.optim.lr_scheduler.OneCycleLR(optimizer, max_lr=args.lr, total_steps=args.steps, pct_start=0.05)
    loss_fn = nn.CrossEntropyLoss(ignore_index=PAD_ID)
    started = time.time()
    for step in range(1, args.steps + 1):
        picks = train_idx[rng.choice(len(train_idx), size=args.batch, p=train_p)]
        x, y = make_batch(sequences, picks, rng)
        x, y = x.to(device), y.to(device)
        logits = model(x)
        loss = loss_fn(logits.reshape(-1, logits.shape[-1]), y.reshape(-1))
        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        nn.utils.clip_grad_norm_(model.parameters(), 1.0)
        optimizer.step()
        scheduler.step()
        if step % 500 == 0 or step == args.steps:
            line = f"Schritt {step}/{args.steps}: Verlust {loss.item():.3f}, {time.time() - started:.0f} s"
            if step % 5000 == 0 or step == args.steps:
                ppl, t1, t3 = evaluate(model, valid, device)
                line += f" | Prüftext: Perplexität {ppl:.1f}, nächstes Wort auf Platz 1 {t1:.1%}, unter den ersten 3 {t3:.1%}"
            print(line, flush=True)

    os.makedirs(args.out, exist_ok=True)
    write_vocab(os.path.join(args.out, f"{lang}.vocab.txt"), vocab, counts)
    export(model.cpu().eval(), os.path.join(args.out, f"{lang}.lm.onnx"), len(vocab))


def write_vocab(path, vocab, counts):
    """Anteil jedes Worts am Trainingstext (geglättet); daran misst die Tastatur, wie gut ein Wort in den Satz passt."""
    total = sum(counts.get(w, 0) + 0.5 for w in vocab[len(SPECIALS):])
    with open(path, "w", encoding="utf-8", newline="\n") as dst:
        for i, word in enumerate(vocab):
            share = 0.0 if i < len(SPECIALS) else (counts.get(word, 0) + 0.5) / total
            dst.write(f"{word}\t{share:.6g}\n")
    print(f"{path}: {len(vocab)} Einträge")


def export(model, path, vocab_size):
    from onnxruntime.quantization import QuantType, quantize_dynamic
    import onnxruntime

    fp32 = path + ".fp32.onnx"
    example = torch.tensor([[BOS_ID, 10, 11]], dtype=torch.long)
    torch.onnx.export(
        Exported(model), (example,), fp32,
        input_names=["tokens"], output_names=["probs"],
        dynamic_axes={"tokens": {1: "length"}},
        opset_version=17, do_constant_folding=True, dynamo=False)
    quantize_dynamic(fp32, path, weight_type=QuantType.QInt8, op_types_to_quantize=["MatMul", "LSTM", "Gather"])

    # Gegenprobe: quantisiertes Modell gegen PyTorch, mit verschieden langen Eingaben
    session = onnxruntime.InferenceSession(path, providers=["CPUExecutionProvider"])
    with torch.no_grad():
        for length in (1, 5, MAX_CONTEXT):
            tokens = torch.randint(4, vocab_size, (1, length))
            tokens[0, 0] = BOS_ID
            expected = Exported(model)(tokens).numpy()[0]
            actual = session.run(["probs"], {"tokens": tokens.numpy()})[0][0]
            if actual.shape != (vocab_size,) or abs(actual.sum() - 1) > 1e-3:
                sys.exit(f"Export fehlerhaft: Ausgabe {actual.shape}, Summe {actual.sum()}")
            same = len(set(np.argsort(-expected)[:10]) & set(np.argsort(-actual)[:10]))
            print(f"  Gegenprobe Länge {length}: {same}/10 der besten Wörter wie in PyTorch")
    os.remove(fp32)
    print(f"{path}: {os.path.getsize(path) / 1024:.0f} KiB")


if __name__ == "__main__":
    main()
