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

Write-Output "Release tag $Tag matches canonical version $version."
