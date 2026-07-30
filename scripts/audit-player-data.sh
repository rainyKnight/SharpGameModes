#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    printf 'Usage: %s <autoteamlock_player_data.db>\n' "$0" >&2
    exit 2
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet run \
    --project "$ROOT/tools/SharpGameModes.PlayerData.Audit/SharpGameModes.PlayerData.Audit.csproj" \
    --configuration Release \
    -- "$1"
