[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ExpectedPackage,

    [Parameter(Mandatory = $true)]
    [string] $PublishedPackage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedPath = (Resolve-Path -LiteralPath $ExpectedPackage).Path
$publishedPath = (Resolve-Path -LiteralPath $PublishedPackage).Path

& dotnet nuget verify $publishedPath --all
if ($LASTEXITCODE -ne 0) {
    throw "The published NuGet package signature is invalid: $publishedPath"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-PackageContent {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackagePath
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    $entries = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::Ordinal)
    $hasSignature = $false

    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -ceq '.signature.p7s') {
                if ($hasSignature) {
                    throw "NuGet package contains duplicate signature entries: $PackagePath"
                }

                $hasSignature = $true
                continue
            }

            $stream = $entry.Open()
            $sha256 = [System.Security.Cryptography.SHA256]::Create()
            try {
                $digest = [Convert]::ToHexString($sha256.ComputeHash($stream)).ToLowerInvariant()
            } finally {
                $sha256.Dispose()
                $stream.Dispose()
            }

            $value = [pscustomobject]@{
                Length = $entry.Length
                Digest = $digest
            }
            if (-not $entries.TryAdd($entry.FullName, $value)) {
                throw "NuGet package contains a duplicate entry '$($entry.FullName)': $PackagePath"
            }
        }
    } finally {
        $archive.Dispose()
    }

    [pscustomobject]@{
        Entries = $entries
        HasSignature = $hasSignature
    }
}

$expected = Read-PackageContent -PackagePath $expectedPath
$published = Read-PackageContent -PackagePath $publishedPath

if (-not $published.HasSignature) {
    throw "The published NuGet package has no repository signature: $publishedPath"
}

$expectedNames = @($expected.Entries.Keys | Sort-Object)
$publishedNames = @($published.Entries.Keys | Sort-Object)
$difference = Compare-Object -ReferenceObject $expectedNames -DifferenceObject $publishedNames
if ($null -ne $difference) {
    throw "Published NuGet package entries differ from the validated package: $($difference | Out-String)"
}

foreach ($name in $expectedNames) {
    $expectedEntry = $expected.Entries[$name]
    $publishedEntry = $published.Entries[$name]
    if ($expectedEntry.Length -ne $publishedEntry.Length -or
        $expectedEntry.Digest -cne $publishedEntry.Digest) {
        throw "Published NuGet package entry differs from the validated package: $name"
    }
}

Write-Host "Verified $($expectedNames.Count) NuGet package entries and the repository signature."
