param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
$trackedFiles = @(& git -C $resolvedRoot ls-files)
if ($LASTEXITCODE -ne 0) {
    throw "Failed to enumerate tracked files in $resolvedRoot."
}

foreach ($relative in $trackedFiles) {
    $attribute = & git -C $resolvedRoot check-attr eol -- $relative
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to read the eol attribute for $relative."
    }

    if (-not $attribute.EndsWith(': crlf', [StringComparison]::Ordinal)) {
        continue
    }

    $path = Join-Path $resolvedRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        continue
    }

    $bytes = [IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and
        $bytes[0] -eq 0xef -and
        $bytes[1] -eq 0xbb -and
        $bytes[2] -eq 0xbf
    $offset = if ($hasBom) { 3 } else { 0 }
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $text = $utf8.GetString($bytes, $offset, $bytes.Length - $offset)
    $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n").Replace("`n", "`r`n")
    if ($normalized.Equals($text, [StringComparison]::Ordinal)) {
        continue
    }

    [IO.File]::WriteAllText($path, $normalized, [Text.UTF8Encoding]::new($hasBom))
}
