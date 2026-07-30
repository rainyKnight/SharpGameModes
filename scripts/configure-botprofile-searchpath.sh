#!/usr/bin/env bash
set -euo pipefail

GAME_ROOT="${1:?usage: configure-botprofile-searchpath.sh <game-root> <Low|Medium|HLTVTop10|High>}"
TIER="${2:?usage: configure-botprofile-searchpath.sh <game-root> <Low|Medium|HLTVTop10|High>}"

case "${TIER,,}" in
    low) TIER="Low" ;;
    medium) TIER="Medium" ;;
    hltvtop10|hltvtop37) TIER="HLTVTop10" ;;
    high) TIER="High" ;;
    *)
        printf 'Unsupported BotProfile tier: %s\n' "$TIER" >&2
        exit 2
        ;;
esac

GAME_ROOT="$(cd "$GAME_ROOT" && pwd)"
GAMEINFO="$GAME_ROOT/csgo/gameinfo.gi"
DATABASE="$GAME_ROOT/csgo/overrides/$TIER/botprofile.db"
VPK="$GAME_ROOT/csgo/overrides/$TIER/botprofile.vpk"
BACKUP="$GAMEINFO.sharp-gamemodes-botprofile.bak"
TEMP="$GAMEINFO.sharp-gamemodes-botprofile.tmp"

test -f "$GAMEINFO"
test -s "$DATABASE"
test -s "$VPK"

if [[ ! -f "$BACKUP" ]]; then
    cp -- "$GAMEINFO" "$BACKUP"
fi

awk -v tier="$TIER" '
    /^[[:space:]]*Game[[:space:]]+csgo\/overrides\/(Low|Medium|HLTVTop10|HLTVTop37|High)(\/botprofile\.vpk)?([[:space:]]|$)/ {
        next
    }
    !inserted && /^[[:space:]]*Game[[:space:]]+sharp([[:space:]]|$)/ {
        match($0, /^[[:space:]]*/)
        indent = substr($0, RSTART, RLENGTH)
        print indent "Game\tcsgo/overrides/" tier "/botprofile.vpk"
        inserted = 1
    }
    { print }
    END {
        if (!inserted) {
            exit 3
        }
    }
' "$GAMEINFO" >"$TEMP"

mv -- "$TEMP" "$GAMEINFO"
printf 'Configured %s before Game sharp in %s\n' \
    "csgo/overrides/$TIER/botprofile.vpk" \
    "$GAMEINFO"
