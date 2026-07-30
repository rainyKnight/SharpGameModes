# ModSharp first-party module vendor record

`ClientPreferences` and `Sharp.Modules.ClientPreferences.Shared` are built from
`Kxnrl/modsharp-public` commit
`16944ed7f530d26e1aef8987409acd3dc0f69815`, released as ModSharp `2.1.136`
and matching the runtime used by this project.

Upstream: https://github.com/Kxnrl/modsharp-public

ClientPreferences stores model selections in
`sharp/data/client-preferences.db`.

The package installs the module assembly in
`sharp/modules/ClientPrefsOfficial`. This directory name keeps ClientPreferences
ahead of `SharpGameModes.PlayerModels` in filesystem enumeration order. The
official assembly, display name, interface identity and IL are unchanged.

Portable PDB files are omitted. The CodeView source path embedded in the two
DLLs is normalized to a generic `opensource` build user so published binaries
do not expose a contributor's local account name.

ModSharp is distributed under AGPL-3.0-or-later with special exceptions. The
upstream exception notice is preserved in
`LICENSES/ModSharp-EXCEPTION.txt`, and the full AGPL version 3 text is in the
repository `LICENSE`.
