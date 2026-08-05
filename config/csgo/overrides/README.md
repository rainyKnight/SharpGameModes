# BotProfile difficulty data

The package exposes four tiers:

- `Low`: simple.
- `Medium`: normal, sourced from upstream's archived
  `overrides/archived/medium_difficulty/botprofile/botprofile.db`.
- `HLTVTop10`: hard, curated to the 50 members of the first ten teams in
  upstream `Commands.txt` at commit
  `7649abe4b1f0b67c6826aea0c3c488348799ca60`. Every selected profile uses a
  Skill 100 `Pro*` template; upstream's `enkay J` stand-in is promoted from
  `RankOthers` to `ProSteady` for this tier.
- `High`: nightmare.

`Low` and `High` preserve the byte-for-byte databases from
[`ed0ard/CS2-Bot-Improver`](https://github.com/ed0ard/CS2-Bot-Improver)
release `v1.4.3` (`d1d83982db88fbdb686b2bf13aa8c6f9d65a4604`). Their semantic
contents match repository snapshot
`7649abe4b1f0b67c6826aea0c3c488348799ca60`; the `Medium` archive and
`HLTVTop10` source profiles are copied from that snapshot. The `.gitattributes`
entry treats these databases as binary so builds reproduce the exact tested
assets on every platform.

Each raw database is packed into a single-file `botprofile.vpk` by
`tools/SharpGameModes.BotProfile.Pack`. Run
`scripts/configure-botprofile-searchpath.sh <game-root> <tier>` after the
package is installed, then restart the CS2 process. The script places the
selected VPK before `Game sharp` in `gameinfo.gi`; mounting the directory
itself is insufficient because the stock CS2 VPK still wins that lookup.

`SharpGameModes.BotMatch` verifies during ModSharp module initialization and every
server initialization that the engine filesystem resolves both the exact byte
size and SHA-256 fingerprint of the selected source `botprofile.db`. BotMatch
activation is blocked if that verification fails instead of silently falling
back to the stock CS2 profiles.

The runtime loads the VPK but uses the auditable raw database beside it as the
expected byte length. A raw-only directory remains a compatibility fallback
for environments whose filesystem search order supports it.

These upstream files are licensed under AGPL-3.0. See `UPSTREAM-LICENSE`.
