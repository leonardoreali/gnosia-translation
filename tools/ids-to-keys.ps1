# Expands a translation written per unique line ("id|text", see make-unique.ps1) into the
# per-row file the patcher reads ("key <TAB> text").
# Usage: tools\ids-to-keys.ps1 -Lang es -Source _work\es    (a folder of *.tsv files with id|text lines)
param(
    [Parameter(Mandatory)][string]$Lang,
    [Parameter(Mandatory)][string]$Source,
    [string]$Reference = "$PSScriptRoot\..\_work\reference.tsv",
    [string]$Unique = "$PSScriptRoot\..\_work\unique.tsv"
)
$utf8 = New-Object Text.UTF8Encoding($false)
$enById = @{}
foreach ($line in [IO.File]::ReadLines((Resolve-Path $Unique), $utf8)) { $p = $line.Split("`t"); $enById[[int]$p[0]] = $p[2] }

$byEnglish = @{}
$bad = 0
foreach ($file in Get-ChildItem $Source -Filter *.tsv | Sort-Object Name) {
    foreach ($line in [IO.File]::ReadLines($file.FullName, $utf8)) {
        if ($line.Trim().Length -eq 0 -or $line.StartsWith('#')) { continue }
        $i = $line.IndexOfAny([char[]]"|`t")
        $id = 0
        if ($i -le 0 -or -not [int]::TryParse($line.Substring(0, $i), [ref]$id) -or -not $enById.ContainsKey($id)) {
            Write-Warning "$($file.Name): bad line: $line"; $bad++; continue
        }
        $byEnglish[$enById[$id]] = $line.Substring($i + 1)
    }
}

$out = New-Object System.Collections.Generic.List[string]
$out.Add("# Gnosia fan translation: $Lang")
foreach ($line in [IO.File]::ReadLines((Resolve-Path $Reference), $utf8)) {
    if ($line.StartsWith('#')) { continue }
    $p = $line.Split("`t")
    if ($byEnglish.ContainsKey($p[1])) { $out.Add("$($p[0])`t$($byEnglish[$p[1]])") }
}
$target = "$PSScriptRoot\..\translations\$Lang.tsv"
# words for the game's code (Code/was...) are not rows of the game: keep them from the current file
if (Test-Path $target) {
    foreach ($line in [IO.File]::ReadLines((Resolve-Path $target), $utf8)) { if ($line.StartsWith('Code/')) { $out.Add($line) } }
}
[IO.File]::WriteAllLines($target, $out, $utf8)
"$Lang : $($byEnglish.Count) unique lines -> $($out.Count - 1) rows written to translations\$Lang.tsv ($bad bad lines)"
