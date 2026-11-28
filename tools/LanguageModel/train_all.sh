#!/usr/bin/env bash
# Trainiert die neuronalen Sprachmodelle aller (oder der angegebenen) Sprachen nach Assets/lm/.
# Aufruf aus dem zBoard-Ordner: tools/LanguageModel/train_all.sh [de en …] [-- weitere Optionen für train_lm.py]
# Die Satzsammlung von Common Voice landet in ../common-voice (CV_DIR=… zum Ändern).
set -euo pipefail

ALL=(ar bg cs de el en es fr hu it nl pl ro ru tr uk vi zh)
CV_DIR="${CV_DIR:-../common-voice}"

languages=()
extra=()
while [[ $# -gt 0 ]]; do
  if [[ "$1" == "--" ]]; then shift; extra=("$@"); break; fi
  languages+=("$1"); shift
done
[[ ${#languages[@]} -eq 0 ]] && languages=("${ALL[@]}")

# Ordnername bei Common Voice
cv_name() { [[ "$1" == "zh" ]] && echo "zh-CN" || echo "$1"; }

if [[ ! -d "$CV_DIR/.git" ]]; then
  git clone --depth 1 --filter=blob:none --sparse https://github.com/common-voice/common-voice "$CV_DIR"
fi
patterns=()
for lang in "${languages[@]}"; do patterns+=("/server/data/$(cv_name "$lang")/*"); done
git -C "$CV_DIR" sparse-checkout add --no-cone "${patterns[@]}" 2>/dev/null \
  || git -C "$CV_DIR" sparse-checkout set --no-cone "${patterns[@]}"

# Protokolle nicht in Assets/ (käme sonst ins App-Paket)
mkdir -p Assets/lm tools/LanguageModel/logs
for lang in "${languages[@]}"; do
  echo "=== $lang ==="
  python3 tools/LanguageModel/train_lm.py --lang "$lang" \
    --common-voice "$CV_DIR/server/data/$(cv_name "$lang")" "${extra[@]}" 2>&1 | tee "tools/LanguageModel/logs/$lang.log" \
    || echo "!!! $lang fehlgeschlagen"
done
