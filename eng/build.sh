#!/usr/bin/env sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
exec dotnet run --project "$root/eng/Yaap.Build/Yaap.Build.csproj" -- "$@"
