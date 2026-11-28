# Vergleicht je Sprache die Vorschläge mit und ohne neuronales Sprachmodell (tools/SuggestionEval).
# Testtext: 300 Sätze aus einem kleinen Korpus (10K) der Leipzig Corpora Collection, das keins der beiden Modelle
# kennt: andere Quelle oder anderes Jahr als das Trainingskorpus (..\lm-daten\leipzig-<Sprache>.source), und nicht
# Common Voice, aus dem das N-Gramm-Modell gebaut ist. Aufruf aus dem zBoard-Ordner:
#   powershell -ExecutionPolicy Bypass -File tools\LanguageModel\compare_all.ps1
param(
    [string[]]$Languages = @("de", "en", "ar", "bg", "cs", "el", "es", "fr", "hu", "it", "nl", "pl", "ro", "ru", "tr", "uk", "vi"),
    [string]$Work = "..\lm-daten"
)
$iso3 = @{ ar = "ara"; bg = "bul"; cs = "ces"; de = "deu"; el = "ell"; en = "eng"; es = "spa"; fr = "fra"; hu = "hun"
           it = "ita"; nl = "nld"; pl = "pol"; ro = "ron"; ru = "rus"; tr = "tur"; uk = "ukr"; vi = "vie"; zh = "zho" }
$countries = @{ ar = @("sa", "eg", "ae", "ma"); bg = @("bg"); cs = @("cz"); de = @("de"); el = @("gr"); en = @("us", "uk", "gb")
                es = @("es"); fr = @("fr"); hu = @("hu"); it = @("it"); nl = @("nl"); pl = @("pl"); ro = @("ro"); ru = @("ru")
                tr = @("tr"); uk = @("ua"); vi = @("vn"); zh = @("cn") }
$base = "https://downloads.wortschatz-leipzig.de/corpora"

function Test-Corpus([string]$name) {
    $code = & curl.exe -s -o NUL -r 0-0 -L -w "%{http_code}" "$base/$name.tar.gz"
    return $code -eq "200" -or $code -eq "206"
}

# Kleines Korpus, aber nicht das Trainingskorpus (dessen 10K wäre ein Ausschnitt daraus): Nachrichten vor Web, neuestes Jahr zuerst
function Find-TestCorpus([string]$lang, [string]$training) {
    $c = $iso3[$lang]
    $candidates = @()
    foreach ($y in 2026..2012) { $candidates += "${c}_news_${y}_10K"; $candidates += "${c}_newscrawl_${y}_10K" }
    foreach ($y in 2026..2012) { foreach ($cc in $countries[$lang]) { $candidates += "$c-${cc}_web_${y}_10K" }; $candidates += "${c}_web_${y}_10K" }
    foreach ($name in $candidates) {
        if ($training -and $name -eq ($training -replace '_1M$|_300K$', '_10K')) { continue }
        if (Test-Corpus $name) { return $name }
    }
    return $null
}

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
dotnet build -c Release tools/SuggestionEval -v q | Out-Null

function Get-Scores([string]$lang, [string]$text, [string[]]$extra) {
    $out = & dotnet run -c Release --no-build --project tools/SuggestionEval -- --lang $lang --text $text @extra 2>&1 | ForEach-Object { "$_" }
    function Value([string]$pattern) {
        $line = $out | Where-Object { $_ -match $pattern } | Select-Object -First 1
        if ($line) { (($line -split '\|')[2]).Trim() -replace ' \(.*\)', '' -replace '\*', '' } else { "?" }
    }
    [pscustomobject]@{ Top3 = Value 'ersten 3 \|'; Gespart = Value 'Gesparte'; Falsch = Value 'Falsche W.*Korrektur' }
}

$rows = foreach ($lang in $Languages) {
    Write-Host "=== $lang ===" -ForegroundColor Cyan
    $text = Join-Path $Work "test-$lang.txt"
    $sourceFile = Join-Path $Work "test-$lang.source"
    if (-not (Test-Path $sourceFile)) {
        $training = if ($lang -eq "de") { "deu-de_web_2021_1M" } else { Get-Content (Join-Path $Work "leipzig-$lang.source") -ErrorAction SilentlyContinue }
        $corpus = Find-TestCorpus $lang $training
        if (-not $corpus) { Write-Warning "Kein Testkorpus gefunden"; continue }
        Write-Host "Testtext aus $corpus"
        $archive = Join-Path $Work "$corpus.tar.gz"
        & curl.exe -s -L --fail -o $archive "$base/$corpus.tar.gz"
        & tar -xzf $archive -C $Work
        $file = Get-ChildItem (Join-Path $Work $corpus) -Recurse -Filter "*-sentences.txt" | Select-Object -First 1
        [System.IO.File]::ReadLines($file.FullName, [System.Text.Encoding]::UTF8) |
            ForEach-Object { ($_ -split "`t", 2)[1] } | Where-Object { $_ -and ($_ -split ' ').Count -ge 4 } |
            Get-Random -Count 300 -SetSeed 1 | Set-Content $text -Encoding UTF8  # die Dateien sind teils alphabetisch sortiert
        Set-Content $sourceFile $corpus
    }
    $without = Get-Scores $lang $text @("--no-neural")
    $with = Get-Scores $lang $text @()
    [pscustomobject]@{
        Sprache = $lang
        Testkorpus = Get-Content $sourceFile
        "Top 3 ohne/mit" = "$($without.Top3) / $($with.Top3)"
        "Gespart ohne/mit" = "$($without.Gespart) / $($with.Gespart)"
        "Falsche Wörter ohne/mit" = "$($without.Falsch) / $($with.Falsch)"
    }
}

Write-Host ""
$rows | Format-Table -AutoSize
