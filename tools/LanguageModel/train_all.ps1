# Trainiert die neuronalen Sprachmodelle (KI-Vorhersage) mit Common Voice plus einem Korpus der
# Leipzig Corpora Collection (CC BY 4.0) je Sprache und schreibt sie nach Assets\lm\.
#
# Aufruf aus dem zBoard-Ordner:
#   powershell -ExecutionPolicy Bypass -File tools\LanguageModel\train_all.ps1                 # alle ohne Modell
#   powershell -ExecutionPolicy Bypass -File tools\LanguageModel\train_all.ps1 -Languages en,fr
#   ... -Force      vorhandene Modelle neu trainieren
#
# Voraussetzungen: py (Python) mit torch, onnx, onnxruntime, numpy, jieba; Common Voice unter ..\common-voice
# (siehe README.md). Heruntergeladene Korpora landen in ..\lm-daten und werden beim nächsten Lauf wiederverwendet.

param(
    [string[]]$Languages = @("ar", "bg", "cs", "el", "en", "es", "fr", "hu", "it", "nl", "pl", "ro", "ru", "tr", "uk", "vi", "zh"),
    [string]$Work = "..\lm-daten",
    [string]$CommonVoice = "..\common-voice\server\data",
    [double]$Weight = 2,
    [switch]$Force
)

$iso3 = @{ ar = "ara"; bg = "bul"; cs = "ces"; de = "deu"; el = "ell"; en = "eng"; es = "spa"; fr = "fra"; hu = "hun"
           it = "ita"; nl = "nld"; pl = "pol"; ro = "ron"; ru = "rus"; tr = "tur"; uk = "ukr"; vi = "vie"; zh = "zho" }
# Land des Web-Korpus: das Land, in dem die Sprache hauptsächlich gesprochen wird (wie "deu-de" für Deutsch)
$countries = @{ ar = @("sa", "eg", "ae", "ma"); bg = @("bg"); cs = @("cz"); de = @("de"); el = @("gr"); en = @("us", "uk", "gb")
                es = @("es"); fr = @("fr"); hu = @("hu"); it = @("it"); nl = @("nl"); pl = @("pl"); ro = @("ro"); ru = @("ru")
                tr = @("tr"); uk = @("ua"); vi = @("vn"); zh = @("cn") }
$base = "https://downloads.wortschatz-leipzig.de/corpora"
$years = 2026..2014

function Test-Corpus([string]$name) {
    # Nur das erste Byte holen: funktioniert auch bei Servern, die HEAD nicht beantworten
    $code = & curl.exe -s -o NUL -r 0-0 -L -w "%{http_code}" "$base/$name.tar.gz"
    return $code -eq "200" -or $code -eq "206"
}

# Bevorzugt: Web-Korpus des Landes (Alltagssprache), dann Web ohne Land, dann Nachrichten; neuestes Jahr zuerst; 1M vor 300K
function Find-Corpus([string]$lang) {
    $codes = @($iso3[$lang])
    if ($lang -eq "zh") { $codes += "cmn" }
    foreach ($size in "1M", "300K") {
        $candidates = @()
        foreach ($c in $codes) { foreach ($cc in $countries[$lang]) { foreach ($y in $years) { $candidates += "$c-${cc}_web_${y}_$size" } } }
        foreach ($type in "web", "news", "newscrawl", "mixed") { foreach ($c in $codes) { foreach ($y in $years) { $candidates += "${c}_${type}_${y}_$size" } } }
        foreach ($name in $candidates) { if (Test-Corpus $name) { return $name } }
    }
    return $null
}

# Zweite Spalte (der Satz) aus <Korpus>-sentences.txt, schnell über .NET statt Pipeline
function Export-Sentences([string]$folder, [string]$target) {
    $file = Get-ChildItem $folder -Recurse -Filter "*-sentences.txt" | Select-Object -First 1
    if (-not $file) { return $false }
    $writer = New-Object System.IO.StreamWriter($target, $false, (New-Object System.Text.UTF8Encoding $false))
    try {
        foreach ($line in [System.IO.File]::ReadLines($file.FullName, [System.Text.Encoding]::UTF8)) {
            $tab = $line.IndexOf("`t")
            if ($tab -ge 0) { $writer.WriteLine($line.Substring($tab + 1)) }
        }
    } finally { $writer.Close() }
    return $true
}

New-Item -ItemType Directory -Force $Work, "Assets\lm", "tools\LanguageModel\logs" | Out-Null
# Python soll UTF-8 ausgeben und PowerShell es so lesen, sonst werden Umlaute verstümmelt ("Pr³ftext")
$env:PYTHONIOENCODING = "utf-8"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$summary = @()

foreach ($lang in $Languages) {
    Write-Host "=== $lang ===" -ForegroundColor Cyan
    if ((Test-Path "Assets\lm\$lang.lm.onnx") -and -not $Force) {
        Write-Host "Modell vorhanden, übersprungen (-Force zum Neutrainieren)"
        continue
    }

    $cv = if ($lang -eq "zh") { "zh-CN" } else { $lang }
    $arguments = @("tools\LanguageModel\train_lm.py", "--lang", $lang, "--common-voice", "$CommonVoice\$cv")

    $text = Join-Path $Work "leipzig-$lang.txt"
    $corpus = $null
    if (-not (Test-Path $text)) {
        Write-Host "Suche Korpus ..."
        $corpus = Find-Corpus $lang
        if ($corpus) {
            Write-Host "Lade $corpus"
            & curl.exe -L --fail -o (Join-Path $Work "$corpus.tar.gz") "$base/$corpus.tar.gz"
            & tar -xzf (Join-Path $Work "$corpus.tar.gz") -C $Work
            if (-not (Export-Sentences (Join-Path $Work $corpus) $text)) { Write-Warning "Keine Sätze in $corpus" }
            Set-Content (Join-Path $Work "leipzig-$lang.source") $corpus
        } else {
            Write-Warning "Kein Leipzig-Korpus gefunden: nur Common Voice"
        }
    }
    if (Test-Path $text) {
        $arguments += @("--source", "${text}:$Weight")
        $corpus = Get-Content (Join-Path $Work "leipzig-$lang.source") -ErrorAction SilentlyContinue
    }

    & py @arguments 2>&1 | ForEach-Object { "$_" } | Tee-Object "tools\LanguageModel\logs\$lang.log"
    $last = Select-String -Path "tools\LanguageModel\logs\$lang.log" -Pattern "Perplexit" | Select-Object -Last 1
    $summary += [pscustomobject]@{ Sprache = $lang; Korpus = $corpus; Ergebnis = if ($last) { ($last.Line -split '\| ')[-1] } else { "FEHLER, siehe Log" } }
}

Write-Host ""
Write-Host "Zusammenfassung (Prüftext am Ende des Trainings):" -ForegroundColor Cyan
$summary | Format-Table -AutoSize -Wrap
Write-Host "Korpora für THIRD_PARTY_NOTICES.md stehen in $Work\leipzig-<Sprache>.source"
