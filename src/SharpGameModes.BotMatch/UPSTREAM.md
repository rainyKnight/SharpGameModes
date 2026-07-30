# BotMatch upstream mapping

SharpGameModes ports the following bot functionality to the ModSharp public
SDK. The SharpGameModes implementations were modified during July 2026 and
are licensed with the rest of the project under `AGPL-3.0-only`.

## CS2-Bot-Improver

Upstream:
[ed0ard/CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver), commit
`af1639598b1d7ba64d4850a36f4c819500f3b8ea`, GNU AGPL version 3.

| Upstream component | SharpGameModes implementation |
| --- | --- |
| BotAI 1.8.7 | `BotAiRuntime.cs` |
| BotAimImprover | `BotAimRuntime.cs` |
| BotBuy 1.0.12 | `BotBuyRuntime.cs`, `BotBuyPolicy.cs` |
| BotRandomizer 1.3.0 | `BotCosmeticRuntime.cs` |
| BotState / smarter-bot behavior | `BotStateRuntime.cs`, `BotStateFlashRuntime.cs` |
| NadeSystem 1.1.7 | `NadeSystemRuntime.cs`, `NadeProjectileFactory.cs` |
| RoundDamageRecap | `RoundDamageRecapRuntime.cs` |
| BotProfile tiers | `config/csgo/overrides/` |
| Grenade and cosmetic data | `config/sharp/configs/sharp-gamemodes/botmatch-*` |

The copied data directories preserve the upstream license and snapshot details
in their local `README.md` and `UPSTREAM-LICENSE` files.

## CS2-Bot-Controller

Upstream:
[XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller),
version `v0.5.5`, commit `57077bd88a934ee093138589292972a0d4fa97d0`,
`AGPL-3.0-only`.

`BotControllerRuntime.cs` reimplements the ABI 16 behavior using ModSharp hooks
and APIs. The implementation has been adapted and modified for this project.

## CS2-Bot-Hider

Upstream:
[XBribo/CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider), commit
`53961880f2ac9b2722296dcd47363bf32c460822`, `AGPL-3.0-only`.

`BotIdentityRuntime.cs` and the identity catalog reimplement identity and
presentation behavior. The catalog keeps its upstream snapshot and license in
`config/sharp/configs/sharp-gamemodes/botmatch-identities/`.

See the repository-level `THIRD_PARTY_NOTICES.md` for all distributed
dependencies and license locations.
