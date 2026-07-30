using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;

namespace SharpGameModes.Cosmetics;

public sealed partial class CosmeticsModule
{
    private void InstallHooks()
    {
        if (_hooksInstalled)
        {
            return;
        }

        _hooks.PlayerSpawnPost.InstallForward(OnPlayerSpawned, ListenerPriority);
        _hooks.GiveNamedItem.InstallHookPre(OnGiveNamedItemPre, ListenerPriority);
        _hooks.GiveNamedItem.InstallHookPost(OnGiveNamedItemPost, ListenerPriority);
        _hooks.PlayerEquipWeapon.InstallForward(OnPlayerEquipWeapon, ListenerPriority);
        _hooksInstalled = true;
    }

    private void RemoveHooks()
    {
        if (!_hooksInstalled)
        {
            return;
        }

        _hooks.PlayerSpawnPost.RemoveForward(OnPlayerSpawned);
        _hooks.GiveNamedItem.RemoveHookPre(OnGiveNamedItemPre);
        _hooks.GiveNamedItem.RemoveHookPost(OnGiveNamedItemPost);
        _hooks.PlayerEquipWeapon.RemoveForward(OnPlayerEquipWeapon);
        _hooksInstalled = false;
    }

    private void OnPlayerSpawned(IPlayerSpawnForwardParams parameters)
    {
        if (!_config.Enabled || !parameters.Pawn.IsValid())
        {
            return;
        }

        var client = parameters.Client;
        var controller = parameters.Controller;
        if (IsHuman(client))
        {
            Schedule(
                () =>
                {
                    if (controller.GetPlayerPawn() is { IsAlive: true } pawn)
                    {
                        RefreshWeaponSkins(client, pawn);
                    }
                },
                _config.SpawnApplyDelaySeconds);
        }
    }
}
