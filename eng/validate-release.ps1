param(
    [Parameter(Mandatory = $true)]
    [string] $Tag
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $root 'eng/Version.props'
[xml] $versionDocument = Get-Content -LiteralPath $versionFile -Raw
$version = [string] $versionDocument.Project.PropertyGroup.VersionPrefix

if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'eng/Version.props does not contain VersionPrefix.'
}

$expectedTag = "v$version"
if ($Tag -cne $expectedTag) {
    throw "Release tag '$Tag' does not match eng/Version.props ('$expectedTag')."
}

if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$') {
    throw "Version '$version' is not valid SemVer."
}

$readme = Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw
$security = Get-Content -LiteralPath (Join-Path $root 'SECURITY.md') -Raw
$changelog = Get-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Raw
if ($readme.Contains('はまだ公開されていません。')) {
    throw 'README.md still describes the release as unpublished.'
}

if ($security.Contains('| 公開済みバージョン | なし |')) {
    throw 'SECURITY.md still reports that no version is published.'
}

$versionParts = $version.Split('.')[0..1]
$supportedSeries = "$($versionParts[0]).$($versionParts[1]).x"
if (-not $security.Contains("| $version |") -and
    -not $security.Contains("| $supportedSeries |")) {
    throw "SECURITY.md must list the released version or supported series: $version / $supportedSeries."
}

$escapedVersion = [regex]::Escape($version)
if ($changelog -notmatch "(?m)^## \[$escapedVersion\] - [0-9]{4}-[0-9]{2}-[0-9]{2}$") {
    throw "CHANGELOG.md must contain a dated release heading for $version."
}

Write-Output "Release tag $Tag matches canonical version $version."
