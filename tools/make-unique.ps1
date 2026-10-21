# Builds the de-duplicated source list translators work from.
# Input : reference.tsv  (from: GnosiaTranslationPatcher.exe --export reference.tsv)
# Output: unique.tsv     ("id <TAB> asset:sheet <TAB> english"), one line per distinct English text.
param(
    [string]$Reference = "$PSScriptRoot\..\_work\reference.tsv",
    [string]$Out = "$PSScriptRoot\..\_work\unique.tsv"
)
$ids = @{}
$lines = New-Object System.Collections.Generic.List[string]
foreach ($line in [IO.File]::ReadLines((Resolve-Path $Reference), [Text.Encoding]::UTF8)) {
    if ($line.StartsWith('#')) { continue }
    $p = $line.Split("`t")
    $en = $p[1]
    if ($en.Length -eq 0 -or $ids.ContainsKey($en)) { continue }
    $ids[$en] = $ids.Count + 1
    $asset, $sheet = $p[0].Split('/')[0..1]
    $lines.Add("$($ids[$en])`t$asset`:$($sheet.Split(':',2)[1])`t$en")
}
[IO.File]::WriteAllLines($Out, $lines, (New-Object Text.UTF8Encoding($false)))
"unique lines: $($lines.Count) -> $Out"
