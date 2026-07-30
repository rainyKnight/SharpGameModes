# Configuration examples

The files in this directory are public examples, not a production server backup.

## Layout

- `sharp/configs/sharp-gamemodes/`: module settings and map pools.
- `sharp/data/sharp-gamemodes/cosmetics/`: public weapon-paint catalog data.
- `sharp/gamedata/`: ModSharp gamedata required by BotMatch.
- `csgo/cfg/sharp-gamemodes/`: mode-specific CS2 ConVar examples.
- `csgo/overrides/`: four BotProfile difficulty tiers.

The BotMatch map pool, identity catalog, grenade lineups and cosmetic catalog are
functional upstream data and are intentionally complete. The classic, TDM,
zombie, administrator, player-model and role-sound files are minimal examples.

The player-model example includes a T-only model, a CT-only model and a model
without `side`; omitting `side` makes the model available to both teams. The
RoleSound example lists every event key found in the reference configuration,
but all sound resources and model mappings are placeholders.

Keep live SteamIDs, administrators, player databases, custom Workshop assets,
passwords and tokens outside the repository. When updating from a release,
compare the examples with your private deployment instead of overwriting your
private files blindly.

Chat labels are configurable through each module's `prefix` field. PlayerModels
uses `Prefix`, and RoleSound uses `ChatPrefix`. TDM and Zombie help-message
templates accept `{prefix}`. The map-system source notice has its own
`source_offer.prefix`; an empty value hides only the label, not the source
message.

`map-system.jsonc` also contains the public corresponding-source URL shown by
`!source`. Its join notice is disabled by default. Forks and modified
deployments should update that URL to the exact source they make available to
players.
