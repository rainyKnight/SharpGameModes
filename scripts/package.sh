#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PACKAGE_ROOT="$ROOT/.artifacts/package"
OUT="$PACKAGE_ROOT/game"
INSTALLED_NOTICES="$OUT/sharp/SharpGameModes"

rm -rf "$PACKAGE_ROOT"
mkdir -p \
    "$PACKAGE_ROOT/LICENSES" \
    "$INSTALLED_NOTICES/LICENSES" \
    "$OUT/sharp/modules" \
    "$OUT/sharp/shared/SharpGameModes.Contracts" \
    "$OUT/sharp/shared/SharpGameModes.Domain" \
    "$OUT/sharp/shared/Sharp.Modules.ClientPreferences.Shared" \
    "$OUT/sharp/configs" \
    "$OUT/sharp/data/sharp-gamemodes" \
    "$OUT/csgo/cfg"

dotnet restore "$ROOT/SharpGameModes.slnx"
dotnet build "$ROOT/SharpGameModes.slnx" \
    --configuration Release \
    --no-restore \
    --disable-build-servers \
    --maxcpucount:1
dotnet test "$ROOT/SharpGameModes.slnx" \
    --configuration Release \
    --no-build \
    --no-restore \
    --disable-build-servers \
    --maxcpucount:1

for module in SharpGameModes.Core SharpGameModes.MapSystem SharpGameModes.AutoTeam SharpGameModes.Rules SharpGameModes.TeamDeathmatch SharpGameModes.ZombieInfection SharpGameModes.BotMatch SharpGameModes.PlayerModels SharpGameModes.RoleSound SharpGameModes.WorkshopMount; do
    dotnet publish "$ROOT/src/$module/$module.csproj" \
        --configuration Release \
        --no-restore \
        --disable-build-servers \
        --output "$OUT/sharp/modules/$module"
done

for module in SharpGameModes.PlayerData SharpGameModes.Cosmetics; do
    dotnet restore "$ROOT/src/$module/$module.csproj" --runtime linux-x64
    dotnet publish "$ROOT/src/$module/$module.csproj" \
        --configuration Release \
        --runtime linux-x64 \
        --self-contained false \
        --no-restore \
        --disable-build-servers \
        --output "$OUT/sharp/modules/$module"
done

cp "$ROOT/src/SharpGameModes.Contracts/bin/Release/net10.0/SharpGameModes.Contracts.dll" "$OUT/sharp/shared/SharpGameModes.Contracts/"
cp "$ROOT/src/SharpGameModes.Contracts/bin/Release/net10.0/SharpGameModes.Contracts.deps.json" "$OUT/sharp/shared/SharpGameModes.Contracts/"
cp "$ROOT/src/SharpGameModes.Contracts/bin/Release/net10.0/SharpGameModes.Contracts.pdb" "$OUT/sharp/shared/SharpGameModes.Contracts/"
cp "$ROOT/src/SharpGameModes.Domain/bin/Release/net10.0/SharpGameModes.Domain.dll" "$OUT/sharp/shared/SharpGameModes.Domain/"
cp "$ROOT/src/SharpGameModes.Domain/bin/Release/net10.0/SharpGameModes.Domain.deps.json" "$OUT/sharp/shared/SharpGameModes.Domain/"
cp "$ROOT/src/SharpGameModes.Domain/bin/Release/net10.0/SharpGameModes.Domain.pdb" "$OUT/sharp/shared/SharpGameModes.Domain/"
cp -R "$ROOT/vendor/ModSharp-2.1.136/ClientPreferences" "$OUT/sharp/modules/ClientPrefsOfficial"
cp -R "$ROOT/vendor/ModSharp-2.1.136/Sharp.Modules.ClientPreferences.Shared/." \
    "$OUT/sharp/shared/Sharp.Modules.ClientPreferences.Shared/"
cp -R "$ROOT/config/sharp/configs/sharp-gamemodes" "$OUT/sharp/configs/"
cp "$ROOT/config/sharp/configs/admins.jsonc" "$OUT/sharp/configs/"
cp "$ROOT/config/sharp/configs/core.json" "$OUT/sharp/configs/"
cp -R "$ROOT/config/sharp/data/sharp-gamemodes/cosmetics" "$OUT/sharp/data/sharp-gamemodes/"
cp -R "$ROOT/config/csgo/cfg/sharp-gamemodes" "$OUT/csgo/cfg/"
cp -R "$ROOT/config/csgo/overrides" "$OUT/csgo/"
cp "$ROOT/LICENSE" "$PACKAGE_ROOT/LICENSE"
cp "$ROOT/THIRD_PARTY_NOTICES.md" "$PACKAGE_ROOT/THIRD_PARTY_NOTICES.md"
cp -R "$ROOT/LICENSES/." "$PACKAGE_ROOT/LICENSES/"
cp "$ROOT/LICENSE" "$INSTALLED_NOTICES/LICENSE"
cp "$ROOT/THIRD_PARTY_NOTICES.md" "$INSTALLED_NOTICES/THIRD_PARTY_NOTICES.md"
cp -R "$ROOT/LICENSES/." "$INSTALLED_NOTICES/LICENSES/"

find "$OUT/sharp/modules" -name 'Sharp.Shared.dll' -delete
find "$OUT" -name '.DS_Store' -delete

for module in SharpGameModes.Core SharpGameModes.MapSystem SharpGameModes.PlayerData SharpGameModes.Cosmetics SharpGameModes.AutoTeam SharpGameModes.Rules SharpGameModes.TeamDeathmatch SharpGameModes.ZombieInfection SharpGameModes.BotMatch SharpGameModes.PlayerModels SharpGameModes.RoleSound SharpGameModes.WorkshopMount; do
    test -f "$OUT/sharp/modules/$module/$module.dll"
    test -f "$OUT/sharp/modules/$module/$module.deps.json"
done
test -f "$OUT/sharp/shared/SharpGameModes.Contracts/SharpGameModes.Contracts.dll"
test -f "$OUT/sharp/shared/SharpGameModes.Contracts/SharpGameModes.Contracts.deps.json"
test -f "$OUT/sharp/shared/SharpGameModes.Domain/SharpGameModes.Domain.dll"
test -f "$OUT/sharp/shared/SharpGameModes.Domain/SharpGameModes.Domain.deps.json"
test -f "$OUT/sharp/shared/Sharp.Modules.ClientPreferences.Shared/Sharp.Modules.ClientPreferences.Shared.dll"
test -f "$OUT/sharp/modules/ClientPrefsOfficial/ClientPreferences.dll"
test -f "$OUT/sharp/configs/admins.jsonc"
test -f "$OUT/sharp/configs/core.json"
test -f "$OUT/sharp/configs/sharp-gamemodes/cosmetics.jsonc"
test -f "$OUT/sharp/configs/sharp-gamemodes/player-models.jsonc"
test -f "$OUT/sharp/configs/sharp-gamemodes/player-model-defaults.jsonc"
test -f "$OUT/sharp/configs/sharp-gamemodes/rolesound.jsonc"
test -f "$OUT/sharp/configs/sharp-gamemodes/botmatch.jsonc"
test -f "$OUT/sharp/configs/sharp-gamemodes/botmatch-identities/bot_info.json"
test -f "$OUT/sharp/configs/sharp-gamemodes/botmatch-identities/UPSTREAM-LICENSE"
test -f "$OUT/sharp/configs/sharp-gamemodes/map-pools/botmatch.jsonc"
test -f "$OUT/sharp/data/sharp-gamemodes/cosmetics/skins_en.json"
test -f "$OUT/sharp/data/sharp-gamemodes/cosmetics/README.md"
test -f "$OUT/sharp/data/sharp-gamemodes/cosmetics/UPSTREAM-LICENSE"
test -f "$OUT/csgo/cfg/sharp-gamemodes/botmatch.cfg"
test -f "$OUT/csgo/overrides/Low/botprofile.db"
test -f "$OUT/csgo/overrides/Low/botprofile.vpk"
test -f "$OUT/csgo/overrides/Medium/botprofile.db"
test -f "$OUT/csgo/overrides/Medium/botprofile.vpk"
test -f "$OUT/csgo/overrides/HLTVTop10/botprofile.db"
test -f "$OUT/csgo/overrides/HLTVTop10/botprofile.vpk"
test -f "$OUT/csgo/overrides/High/botprofile.db"
test -f "$OUT/csgo/overrides/High/botprofile.vpk"
test -f "$OUT/csgo/overrides/UPSTREAM-LICENSE"
test -f "$PACKAGE_ROOT/LICENSE"
test -f "$PACKAGE_ROOT/THIRD_PARTY_NOTICES.md"
test -f "$PACKAGE_ROOT/LICENSES/ModSharp-EXCEPTION.txt"
test -f "$INSTALLED_NOTICES/LICENSE"
test -f "$INSTALLED_NOTICES/THIRD_PARTY_NOTICES.md"
test -f "$INSTALLED_NOTICES/LICENSES/Apache-2.0-SQLitePCLRaw.txt"
printf 'Package ready: %s\n' "$PACKAGE_ROOT"
