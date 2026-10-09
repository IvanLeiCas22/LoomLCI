#Requires -Version 5.1
<#
.SYNOPSIS
  Valida integridad estructural de la documentacion y enlaces Obsidian.
.DESCRIPTION
  Read-only: comprueba títulos unicos dentro de docs/, frontmatter de las
  notas vigentes, wikilinks Obsidian y enlaces Markdown relativos a archivos.
  No toma contenido historico como estado operativo actual.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$docsRoot = Join-Path $repoRoot 'docs'
$historyRoot = Join-Path $docsRoot '90-Historial'

if (-not (Test-Path -LiteralPath $docsRoot -PathType Container)) {
    throw 'Falta docs/.'
}
$notes = @(Get-ChildItem -LiteralPath $docsRoot -Filter '*.md' -File -Recurse)
$rootReadme = Join-Path $repoRoot 'README.md'
if (-not (Test-Path -LiteralPath $rootReadme -PathType Leaf)) {
    throw 'Falta README.md de la raiz.'
}

$titles = @{}
$errors = [Collections.Generic.List[string]]::new()
foreach ($note in $notes) {
    if ($titles.ContainsKey($note.BaseName)) {
        $errors.Add("Titulo duplicado: $($note.BaseName) en $($note.FullName) y $($titles[$note.BaseName])")
    }
    else {
        $titles[$note.BaseName] = $note.FullName
    }
}

$linkCount = 0
foreach ($note in @($notes) + @(Get-Item -LiteralPath $rootReadme)) {
    $content = [IO.File]::ReadAllText($note.FullName)
    if ($note.FullName.StartsWith($docsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -and
        -not $note.FullName.StartsWith($historyRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -and
        -not [regex]::IsMatch($content, '^---\r?\n')) {
        $errors.Add("Falta frontmatter en $($note.FullName)")
    }

    foreach ($match in [regex]::Matches($content, '\[\[([^\]]+)\]\]')) {
        $target = ($match.Groups[1].Value.Split('|')[0].Split('#')[0] -split '[/\\]')[-1]
        if ($target.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) {
            $target = $target.Substring(0, $target.Length - 3)
        }
        if ([string]::IsNullOrWhiteSpace($target)) { continue }
        $linkCount++
        if (-not $titles.ContainsKey($target)) {
            $errors.Add("Wikilink sin destino en $($note.FullName): $($match.Groups[1].Value)")
        }
    }

    foreach ($match in [regex]::Matches($content, '(?<!\!)\[[^\]]+\]\(([^)]+)\)')) {
        $target = $match.Groups[1].Value
        if ($target -match '^(https?://|mailto:|#)') { continue }
        $target = [Uri]::UnescapeDataString($target.Split('#')[0])
        if ([string]::IsNullOrWhiteSpace($target)) { continue }
        $path = [IO.Path]::GetFullPath((Join-Path $note.DirectoryName $target))
        if (-not (Test-Path -LiteralPath $path)) {
            $errors.Add("Markdown link sin destino en $($note.FullName): $target")
        }
    }
}

if ($errors.Count -gt 0) {
    foreach ($message in $errors) { Write-Error $message -ErrorAction Continue }
    throw "Documentacion: $($errors.Count) errores."
}

$historicCount = @(Get-ChildItem -LiteralPath $historyRoot -Filter '*.md' -Recurse -File |
    Where-Object { $_.BaseName -ne 'Indice historico' }).Count
if ($historicCount -lt 58) {
    throw "Faltan notas historicas originales: $historicCount de 58 como minimo."
}
Write-Host "DOCS_OK notes=$($notes.Count + 1) wikilinks=$linkCount archived_originals=$historicCount"
