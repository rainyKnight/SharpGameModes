# SharpGameModes

SharpGameModes is a collection of ModSharp plugins for CS2 community servers. It provides Classic Competitive, Team Deathmatch, Zombie Infection, enhanced bot matches, and shared map, player-data, and team-balancing services.

## Features

| Module | Purpose |
| --- | --- |
| `SharpGameModes.Core` | Publishes the read-only context for the current map and game mode |
| `SharpGameModes.MapSystem` | Map pools, RTV, nominations, voting, Workshop changes, and next-map persistence |
| `SharpGameModes.AutoTeam` | Team selection locks, automatic assignment, rating-based balance, and low-rating health compensation |
| `SharpGameModes.PlayerData` | Round statistics, match history, rating calculation, and SQLite storage |
| `SharpGameModes.Rules` | Shared friendly-fire rules |
| `SharpGameModes.Cosmetics` | Weapon finishes, knife selection, and player preferences |
| `SharpGameModes.PlayerModels` | Player-model menus, precaching, and ClientPreferences integration |
| `SharpGameModes.RoleSound` | Model-based voice profiles and radio replacement |
| `SharpGameModes.TeamDeathmatch` | Team scoring, respawning, equipment, and weapon menus |
| `SharpGameModes.ZombieInfection` | Infection rounds, conversion, models, weapons, knife damage, and knockback |
| `SharpGameModes.WorkshopMount` | Mounts single-file or multipart Workshop VPKs supplied through `-dual_addon` |
| `SharpGameModes.BotMatch` | Bot AI, aiming, state handling, purchases, grenades, identities, cosmetics, and damage recaps |

The available modes are `classic`, `tdm`, `zombie`, and `botmatch`. A mode module runs only while its mode is active. When the server leaves that mode, the module restores its ConVars, hooks, memory patches, timers, and per-player state.

## BotMatch

BotMatch integrates and ports functionality from several open-source CS2 bot projects:

- BotController ABI 16: locks, user-command injection, movement recording and replay, and purchase plans.
- BotAI 1.8.7: 43 reversible Linux behavior patches and 42 reversible Windows behavior patches.
- BotAimImprover: `head`, `body`, and `mixed` targeting with physics-based visibility checks.
- BotState: counter-strafing, crouching, stuck recovery, navigation, defusing, flash avoidance, reloading, and weapon-state handling.
- BotBuy 1.0.12: economy decisions, refunds, armor, defuse kits, weapon donations, and special-round purchases.
- NadeSystem 1.1.7: `off`, `less`, `normal`, `more`, and `max` grenade strategies.
- BotRandomizer: deterministic agents, music kits, knives, gloves, finishes, stickers, and charms.
- BotHider presentation: professional-player identities, synthetic SteamIDs, names, crosshairs, medals, avatars, and simulated latency.
- RoundDamageRecap: classic or Perfect World-style end-of-round damage summaries selected from the client language.

The repository includes the complete BotMatch map pool supported by the current implementation:

`de_mirage`, `de_inferno`, `de_anubis`, `de_ancient`, `de_dust2`, `de_overpass`, `de_vertigo`, `de_train`, `de_cache`, `cs_office`, and `cs_italy`.

`de_nuke` is excluded from the default pool because its official bot navigation has known problems. The upstream grenade data for the map remains available for future evaluation.

BotProfile provides four difficulty tiers:

- `Low`: Easy
- `Medium`: Normal
- `HLTVTop10`: Hard, using players from the first ten teams in the upstream professional-team list
- `High`: Nightmare

After installing the package, run:

```bash
./scripts/configure-botprofile-searchpath.sh <game-root> <Low|Medium|HLTVTop10|High>
```

The script places the selected `botprofile.vpk` before `Game sharp` in `gameinfo.gi` and preserves the first backup it creates. At startup, BotMatch verifies the size of the database actually parsed by the engine and refuses activation if validation fails.

Writing `userinfo` is unsafe on some CS2 builds, so the native `BOT` scoreboard label is not forcibly hidden by default. Names, latency, crosshairs, medals, and avatars continue to work. BotController voice frames and automatic bot voting are not currently implemented.

## Requirements

- Linux x64 CS2 Dedicated Server
- ModSharp `2.1.136`
- .NET 10 SDK for building
- The official ModSharp `AdminManager`, `CommandCenter`, `TargetingManager`, and `MenuManager` modules

The release package contains the project modules, example configuration, BotProfile tiers, BotMatch data, the matching official ClientPreferences modules, and all project and third-party license notices. Install it into an existing CS2 server with ModSharp.

## Build

```bash
./scripts/build.sh
./scripts/test.sh
./scripts/package.sh
```

The release tree is generated at `.artifacts/package/`. Its `game/` directory mirrors the CS2 server's `game/` directory, while the archive root and installed `game/sharp/SharpGameModes/` directory both preserve the license notices.

## Configuration

The repository provides sanitized, runnable examples in:

- `config/sharp/configs/sharp-gamemodes/`
- `config/csgo/cfg/sharp-gamemodes/`

`admins.jsonc` contains no administrators by default. Player-model and role-sound examples are disabled, while the Classic, TDM, and Zombie map pools contain a small set of stock examples. The BotMatch map pool and its upstream feature data remain complete.

Keep live SteamIDs, administrators, models, voice assets, Workshop selections, databases, passwords, and tokens outside the repository. Do not commit a live `sharp/data/` directory or private deployment configuration.

### Chat prefixes

Module labels are read from configuration:

| Module | Setting |
| --- | --- |
| AutoTeam | `auto-team.jsonc` → `prefix` |
| BotMatch | `botmatch.jsonc` → `prefix` |
| Cosmetics | `cosmetics.jsonc` → `prefix` |
| TeamDeathmatch | `tdm.jsonc` → `prefix` |
| ZombieInfection | `zombie.jsonc` → `prefix` |
| Map-system source offer | `map-system.jsonc` → `source_offer.prefix` |
| PlayerModels | `player-models.jsonc` → `Prefix` |
| RoleSound | `rolesound.jsonc` → `ChatPrefix` |

Removing a setting uses the module's default label. Set it to an empty string to hide the label. The TDM `buy_help_message` and Zombie `weapon_help_message` templates support the `{prefix}` placeholder.

See [`config/README.md`](config/README.md) for configuration details and [`docs/architecture.md`](docs/architecture.md) for module boundaries.

## Common commands

- Maps: `!rtv`, `!yd`, `!ydc`, `!revote`, `!nextmap`, `!maps`
- Corresponding source: `!source` or `!源码`
- Player models: `!model`, `!md`, `!mg`, `!skin`
- Weapon cosmetics: `!s`, `!k`
- TDM and Zombie weapons: `!guns`
- Bot aim mode: `!bot_aim [head|body|mixed]`
- Bot grenade mode: `!bot_nades [off|less|normal|more|max]`
- Reroll bot cosmetics: `!br_reroll [all|slot]`
- Damage recap style: `!damage_style [auto|classic|pw]`
- Administrative team changes: `!forcect`, `!forcet`, `!forcespec`, `!forceteam`

Administrative commands use ModSharp's AdminManager and TargetingManager. Team-management commands require the `admin:team` permission.

## License and attribution

SharpGameModes is released under the
[GNU Affero General Public License version 3 only](LICENSE)
(`AGPL-3.0-only`).

The default map-system configuration presents the corresponding-source URL to
players after they join and through `!source`. Anyone distributing or running
a modified version should publish its complete corresponding source and change
`source_offer.url` to that location.

BotMatch ports and modifies code or data from
[`ed0ard/CS2-Bot-Improver`](https://github.com/ed0ard/CS2-Bot-Improver),
[`XBribo/CS2-Bot-Controller`](https://github.com/XBribo/CS2-Bot-Controller),
and [`XBribo/CS2-Bot-Hider`](https://github.com/XBribo/CS2-Bot-Hider).
The weapon-paint catalog comes from
[`Nereziel/cs2-WeaponPaints`](https://github.com/Nereziel/cs2-WeaponPaints).

Exact commits, component mappings, modification notices, dependency versions,
and retained license texts are listed in
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md). ModSharp and its official
modules remain the property of their respective authors; the `vendor/`
directory records only the fixed first-party files required by this build.
