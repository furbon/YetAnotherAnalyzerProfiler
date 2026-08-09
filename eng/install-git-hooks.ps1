$ErrorActionPreference = 'Stop'

$repositoryRoot = (& git rev-parse --show-toplevel).Trim()
if (-not $repositoryRoot) {
    throw 'Repository root was not found.'
}

& git -C $repositoryRoot config --local core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) {
    throw 'Could not configure core.hooksPath.'
}

$configuredPath = (& git -C $repositoryRoot config --local --get core.hooksPath).Trim()
if ($configuredPath -ne '.githooks') {
    throw "Unexpected core.hooksPath: $configuredPath"
}

Write-Output 'YAAP Git commit guard enabled.'
