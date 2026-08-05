# Third-party notices

SharpGameModes is licensed under GNU Affero General Public License version 3
only (`AGPL-3.0-only`). The complete project license is in [`LICENSE`](LICENSE).

This file records the upstream material included in, adapted by, or used to
build the project. SharpGameModes changes to upstream bot components were made
during July 2026. The default configuration offers the corresponding source
for an installed build through the configurable `!source` command; an optional
join notice is available but disabled by default.

## BotMatch source and data

| Upstream | Snapshot used | License | Use in SharpGameModes |
| --- | --- | --- | --- |
| [ed0ard/CS2-Bot-Improver](https://github.com/ed0ard/CS2-Bot-Improver) | `af1639598b1d7ba64d4850a36f4c819500f3b8ea` | AGPL-3.0 | BotAI, BotAimImprover, BotBuy, BotRandomizer, BotState, NadeSystem, RoundDamageRecap, BotProfile data, and related behavior ported to ModSharp and modified |
| [XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller) | `v0.5.5` (`57077bd88a934ee093138589292972a0d4fa97d0`) | AGPL-3.0-only | BotController ABI 16 behavior ported to ModSharp and modified |
| [XBribo/CS2-Bot-Hider](https://github.com/XBribo/CS2-Bot-Hider) | `53961880f2ac9b2722296dcd47363bf32c460822` | AGPL-3.0-only | Bot identity and presentation data ported to ModSharp and modified |
| [Nereziel/cs2-WeaponPaints](https://github.com/Nereziel/cs2-WeaponPaints) | `d7086fae892c250e97f24dfc5640e501d4bdcf75` | GPL-3.0-only | `website/data/skins_en.json` imported unchanged as the public weapon-paint catalog |

Detailed BotMatch component mapping and modification notices are in
[`src/SharpGameModes.BotMatch/UPSTREAM.md`](https://github.com/rainyKnight/SharpGameModes/blob/main/src/SharpGameModes.BotMatch/UPSTREAM.md).
Copied data directories contain their upstream license text and provenance
record beside the data.

The implementation also consulted
[samyycX/CS2-PlayerModelChanger](https://github.com/samyycX/CS2-PlayerModelChanger)
as a behavior reference. No source code or binary from that project is
distributed.

## Workshop client advertisement

The connection-reply behavior in `SharpGameModes.WorkshopMount` is adapted
from [Source2ZE/MultiAddonManager](https://github.com/Source2ZE/MultiAddonManager),
snapshot `464cd8ab5d71622ebde3280a1849c444c0935489`. MultiAddonManager is
copyright (C) 2024-2025 xen and licensed under GPL-3.0-only. SharpGameModes
reimplements the relevant `ReplyConnection` detour through ModSharp, limits
it to the configured `-dual_addon` on Valve-map replies, and leaves Workshop
map and multi-addon sequencing to ModSharp's existing state machine. No
MultiAddonManager binary, MetaMod plugin, or funchook code is distributed.
The retained GPL-3.0 text is in
[`LICENSES/GPL-3.0-MultiAddonManager.txt`](LICENSES/GPL-3.0-MultiAddonManager.txt).

## ModSharp

The packaged official `ClientPreferences` module and
`Sharp.Modules.ClientPreferences.Shared` assembly come from
[Kxnrl/modsharp-public](https://github.com/Kxnrl/modsharp-public), commit
`16944ed7f530d26e1aef8987409acd3dc0f69815`, released as ModSharp `2.1.136`.
ModSharp is licensed under AGPL-3.0-or-later with special exceptions. The
complete standard AGPL text is in the project [`LICENSE`](LICENSE), and the
upstream copyright and exception notice is preserved verbatim in
[`LICENSES/ModSharp-EXCEPTION.txt`](LICENSES/ModSharp-EXCEPTION.txt).

SharpGameModes is an independently authored set of modules using ModSharp's
public SDK. It does not distribute a modified ModSharp core.

## Packaged managed and native dependencies

The following dependencies may be included in module publish output:

| Component | Version | License file |
| --- | --- | --- |
| ClientPreferences | 2.1.1 | ModSharp terms described above |
| LiteDB | 5.0.21 | [`LICENSES/MIT-LiteDB.txt`](LICENSES/MIT-LiteDB.txt) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.9 | [`LICENSES/MIT-dotnet.txt`](LICENSES/MIT-dotnet.txt) |
| Microsoft.Extensions.Logging.Abstractions | 10.0.9 | [`LICENSES/MIT-dotnet.txt`](LICENSES/MIT-dotnet.txt) |
| MySqlConnector | 2.6.0 | [`LICENSES/MIT-MySqlConnector.txt`](LICENSES/MIT-MySqlConnector.txt) |
| Refit | 10.2.0 | [`LICENSES/MIT-Refit.txt`](LICENSES/MIT-Refit.txt) |
| RESPite | 3.0.0 | [`LICENSES/MIT-StackExchange.txt`](LICENSES/MIT-StackExchange.txt) |
| StackExchange.Redis | 3.0.0 | [`LICENSES/MIT-StackExchange.txt`](LICENSES/MIT-StackExchange.txt) |
| System.IO.Hashing | 10.0.5 | [`LICENSES/MIT-dotnet.txt`](LICENSES/MIT-dotnet.txt) |
| Microsoft.Data.Sqlite | 10.0.10 | [`LICENSES/MIT-dotnet.txt`](LICENSES/MIT-dotnet.txt) |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.4 | [`LICENSES/Apache-2.0-SQLitePCLRaw.txt`](LICENSES/Apache-2.0-SQLitePCLRaw.txt) |
| SourceGear.sqlite3 / SQLite | 3.53.3 | [`LICENSES/SQLite-Public-Domain.txt`](LICENSES/SQLite-Public-Domain.txt) |

Build and test tooling that is not copied into the release package retains the
license supplied by its own NuGet package or upstream repository.
