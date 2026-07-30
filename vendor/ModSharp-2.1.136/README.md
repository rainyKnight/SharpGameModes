# ModSharp first-party module vendor record

`ClientPreferences` and `Sharp.Modules.ClientPreferences.Shared` are built from
`Kxnrl/modsharp-public` git revision 136, matching the ModSharp `2.1.136`
runtime used by this project.

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
