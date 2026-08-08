param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $BuildArguments
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $PSScriptRoot 'Yaap.Build/Yaap.Build.csproj') -- @BuildArguments
exit $LASTEXITCODE
