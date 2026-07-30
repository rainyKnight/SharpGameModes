using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;

namespace SharpGameModes.ZombieInfection;

public sealed partial class ZombieInfectionModule
{
    private void InfectPlayer(IPlayerController player, IPlayerController? infectedBy, bool isMother)
    {
        if (!IsActive() || _phase is not (ZombiePhase.Countdown or ZombiePhase.Active)
            || !IsEligible(player) || !IsAlive(player))
        {
            return;
        }

        var key = PlayerKey(player);
        if (player.Team == CStrikeTeam.TE && !isMother)
        {
            return;
        }

        if (isMother)
        {
            _motherZombies.Add(key);
        }

        if (_config.ZombieLives > 0)
        {
            _zombieLives[key] = _config.ZombieLives;
        }

        player.SwitchTeam(CStrikeTeam.TE);
        Schedule(() => ApplyZombieSpawn(player), 0.05);
        Broadcast(infectedBy is null
            ? $"{_config.Prefix} {player.PlayerName} 成为了母体僵尸！"
            : $"{_config.Prefix} {player.PlayerName} 被 {infectedBy.PlayerName} 感染。");
        Schedule(CheckRoundEnd, 0.1);
    }

    private void ScheduleCorpseInfection(
        IPlayerController player,
        IPlayerController infectedBy,
        Vector? origin,
        Vector? angles)
    {
        var key = PlayerKey(player);
        if (!_pendingCorpseInfections.Add(key))
        {
            return;
        }

        if (origin is { } position)
        {
            var transform = new CorpseTransform(position, angles ?? default);
            _pendingCorpseTransforms[key] = transform;
            CreateCorpseMarker(key, transform);
        }

        Broadcast(
            $"{_config.Prefix} {player.PlayerName} 被 {infectedBy.PlayerName} 击倒，" +
            $"{_config.CorpseInfectionDelaySeconds:0.#} 秒后尸变。");
        var generation = _lifecycleGeneration;
        Schedule(
            () => FinishCorpseInfection(player, infectedBy, key, generation),
            _config.CorpseInfectionDelaySeconds);
    }

    private void FinishCorpseInfection(
        IPlayerController player,
        IPlayerController infectedBy,
        int key,
        int generation)
    {
        _pendingCorpseInfections.Remove(key);
        var hasTransform = _pendingCorpseTransforms.TryGetValue(key, out var transform);
        _pendingCorpseTransforms.Remove(key);
        RemoveCorpseMarker(key);
        if (!IsCurrent(generation) || _phase != ZombiePhase.Active || !IsEligible(player))
        {
            Schedule(CheckRoundEnd, 0.1);
            return;
        }

        _motherZombies.Remove(key);
        if (_config.ZombieLives > 0)
        {
            _zombieLives[key] = _config.ZombieLives;
        }

        player.SwitchTeam(CStrikeTeam.TE);
        player.Respawn();
        Schedule(
            () =>
            {
                if (!IsCurrent(generation) || _phase != ZombiePhase.Active
                    || !IsEligible(player) || player.GetPlayerPawn() is not { IsAlive: true } pawn)
                {
                    return;
                }

                if (hasTransform)
                {
                    TeleportToCorpse(pawn, transform);
                }

                ApplyZombieSpawn(player);
                Schedule(
                    () =>
                    {
                        if (IsCurrent(generation) && player.GetPlayerPawn() is { IsAlive: true } currentPawn
                            && hasTransform)
                        {
                            TeleportToCorpse(currentPawn, transform);
                        }
                    },
                    0.05);
                HealZombie(infectedBy, _config.ZombieHealOnInfect);
                Broadcast($"{_config.Prefix} {player.PlayerName} 被 {infectedBy.PlayerName} 感染。");
                Schedule(CheckRoundEnd, 0.1);
            },
            0.05);
    }

    private void TeleportToCorpse(IPlayerPawn pawn, CorpseTransform transform)
    {
        var position = transform.Origin;
        position.Z += _config.CorpseRespawnZOffset;
        var angles = transform.Angles;
        angles.X = 0;
        angles.Z = 0;
        pawn.Teleport(position, angles, new Vector());
    }

    private void HandleZombieDeath(IPlayerController player)
    {
        var key = PlayerKey(player);
        if (_config.ZombieLives <= 0)
        {
            var generation = _lifecycleGeneration;
            Schedule(
                () =>
                {
                    if (IsCurrent(generation) && _phase == ZombiePhase.Active)
                    {
                        RespawnAndApplyZombie(player);
                    }
                },
                _config.ZombieRespawnDelaySeconds);
            return;
        }

        var livesLeft = Math.Max(0, _zombieLives.GetValueOrDefault(key, _config.ZombieLives) - 1);
        _zombieLives[key] = livesLeft;
        if (livesLeft > 0)
        {
            player.Print(HudPrintChannel.Chat, $"{_config.Prefix} 僵尸剩余生命：{livesLeft}");
            Schedule(() => RespawnAndApplyZombie(player), _config.ZombieRespawnDelaySeconds);
            return;
        }

        player.Print(HudPrintChannel.Chat, $"{_config.Prefix} 僵尸生命耗尽，等待下一回合。");
        player.SwitchTeam(CStrikeTeam.Spectator);
    }

    private void RespawnAndApplyHuman(IPlayerController player)
    {
        if (!IsEligible(player) || !IsActive())
        {
            return;
        }

        var key = PlayerKey(player);
        _motherZombies.Remove(key);
        if (player.Team != CStrikeTeam.CT)
        {
            player.SwitchTeam(CStrikeTeam.CT);
        }

        if (!IsAlive(player))
        {
            player.Respawn();
        }

        Schedule(() => ApplyHumanSpawn(player), 0.15);
    }

    private void RespawnAndApplyZombie(IPlayerController player)
    {
        if (!IsEligible(player) || !IsActive() || _phase != ZombiePhase.Active)
        {
            return;
        }

        if (player.Team != CStrikeTeam.TE)
        {
            player.SwitchTeam(CStrikeTeam.TE);
        }

        if (!IsAlive(player))
        {
            player.Respawn();
        }

        Schedule(() => ApplyZombieSpawn(player), 0.15);
    }

    private void JoinActiveRoundAsZombie(IPlayerController player, bool announce)
    {
        if (!IsEligible(player) || !IsActive() || _phase != ZombiePhase.Active)
        {
            return;
        }

        var key = PlayerKey(player);
        _motherZombies.Remove(key);
        if (_config.ZombieLives > 0)
        {
            _zombieLives[key] = _config.ZombieLives;
        }

        if (player.Team != CStrikeTeam.TE)
        {
            player.SwitchTeam(CStrikeTeam.TE);
        }

        if (!IsAlive(player))
        {
            player.Respawn();
        }

        Schedule(
            () =>
            {
                ApplyZombieSpawn(player);
                if (announce && IsEligible(player))
                {
                    Broadcast($"{_config.Prefix} {player.PlayerName} 中途加入，成为僵尸。");
                }

                Schedule(CheckRoundEnd, 0.2);
            },
            0.15);
    }

    private void ApplyHumanSpawn(IPlayerController player)
    {
        if (!IsEligible(player) || !IsActive() || player.Team != CStrikeTeam.CT
            || player.GetPlayerPawn() is not { IsAlive: true } pawn)
        {
            return;
        }

        RestoreSavedModel(player, pawn);
        pawn.MaxHealth = _config.HumanHealth;
        pawn.Health = _config.HumanHealth;
        pawn.TransientChangeVelocityModifier(1);
        if (_config.SpawnFullArmor)
        {
            pawn.ArmorValue = _config.HumanArmor;
            if (_config.SpawnHelmet && pawn.GetItemService() is { } items)
            {
                items.HasHelmet = true;
            }
        }

        Schedule(() => RefillHumanReserveAmmo(player), 0.05);
        if (_config.ShowWeaponHelpOnHumanSpawn && _weaponHelpPrompted.Add(PlayerKey(player)))
        {
            player.Print(
                HudPrintChannel.Chat,
                _config.WeaponHelpMessage.Replace(
                    "{prefix}",
                    _config.Prefix,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    private void ApplyZombieSpawn(IPlayerController player)
    {
        if (!IsEligible(player) || !IsActive() || player.Team != CStrikeTeam.TE
            || player.GetPlayerPawn() is not { IsAlive: true } pawn)
        {
            return;
        }

        var key = PlayerKey(player);
        var isMother = _motherZombies.Contains(key);
        var health = isMother ? _config.MotherZombieHealth : _config.ZombieHealth;
        pawn.MaxHealth = health;
        pawn.Health = health;
        pawn.TransientChangeVelocityModifier(1);
        StripZombieToKnife(player);

        var generation = _lifecycleGeneration;
        Schedule(
            () =>
            {
                if (IsCurrent(generation))
                {
                    ApplyZombieModel(player, isMother);
                }
            },
            _config.ApplyZombieModelDelaySeconds);
    }

    private void StripZombieToKnife(IPlayerController player)
    {
        if (!IsEligible(player) || player.Team != CStrikeTeam.TE
            || player.GetPlayerPawn() is not { IsAlive: true } pawn)
        {
            return;
        }

        pawn.RemoveAllItems(removeSuit: false);
        pawn.GiveNamedItem("weapon_knife");
    }

    private void HealZombie(IPlayerController player, int amount)
    {
        if (amount <= 0 || !IsEligible(player) || player.Team != CStrikeTeam.TE
            || player.GetPlayerPawn() is not { IsAlive: true } pawn)
        {
            return;
        }

        var maximum = _motherZombies.Contains(PlayerKey(player))
            ? _config.MotherZombieHealth
            : _config.ZombieHealth;
        pawn.Health = Math.Min(maximum, pawn.Health + amount);
    }

    private void ApplyZombieModel(IPlayerController player, bool isMother)
    {
        if (!IsEligible(player) || player.Team != CStrikeTeam.TE
            || player.GetPlayerPawn() is not { IsAlive: true } pawn)
        {
            return;
        }

        var models = isMother && _config.MotherZombieModels.Length > 0
            ? _config.MotherZombieModels
            : _config.ZombieModels;
        if (models.Length == 0)
        {
            return;
        }

        var key = PlayerKey(player);
        if (!_savedModels.ContainsKey(key))
        {
            var currentModel = pawn.GetBodyComponent()
                .GetSceneNode()?
                .AsSkeletonInstance?
                .GetModelState()
                .ModelName;
            if (!string.IsNullOrWhiteSpace(currentModel))
            {
                _savedModels[key] = currentModel;
            }
        }

        pawn.SetModel(models[Random.Shared.Next(models.Length)]);
    }

    private void RestoreSavedModel(IPlayerController player, IPlayerPawn pawn)
    {
        var key = PlayerKey(player);
        if (_savedModels.Remove(key, out var model) && !string.IsNullOrWhiteSpace(model))
        {
            pawn.SetModel(model);
        }
    }

    private void RestoreSavedModels()
    {
        foreach (var player in GetEligibleControllers())
        {
            if (player.GetPlayerPawn() is { } pawn)
            {
                RestoreSavedModel(player, pawn);
            }
        }
    }

    private void CreateCorpseMarker(int key, CorpseTransform transform)
    {
        if (!_config.CorpseMarkerEnabled)
        {
            return;
        }

        var model = string.IsNullOrWhiteSpace(_config.CorpseMarkerModel)
            ? _config.ZombieModels.FirstOrDefault()
            : _config.CorpseMarkerModel;
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        RemoveCorpseMarker(key);
        var kv = new Dictionary<string, KeyValuesVariantValueItem>
        {
            ["model"] = model,
            ["spawnflags"] = 256,
            ["defaultanim"] = _config.CorpseMarkerAnimation,
            ["disablereceiveshadows"] = true,
            ["disableshadows"] = true,
            ["solid"] = 0,
        };
        var marker = _entities.SpawnEntitySync<IBaseModelEntity>("prop_dynamic", kv);
        if (marker is null)
        {
            return;
        }

        marker.RenderMode = _config.CorpseMarkerAlpha < 255 ? RenderMode.TransAlpha : RenderMode.Normal;
        marker.RenderColor = new Color32(
            _config.CorpseMarkerRed,
            _config.CorpseMarkerGreen,
            _config.CorpseMarkerBlue,
            _config.CorpseMarkerAlpha);
        marker.SetModelScale(_config.CorpseMarkerScale);
        var glow = marker.GetGlowProperty();
        glow.Glowing = true;
        glow.GlowType = _config.CorpseMarkerGlowType;
        glow.GlowTeam = -1;
        glow.GlowRangeMin = 0;
        glow.GlowRangeMax = _config.CorpseMarkerGlowRange;
        glow.GlowColorOverride = new Color32(
            _config.CorpseMarkerRed,
            _config.CorpseMarkerGreen,
            _config.CorpseMarkerBlue,
            255);
        var position = transform.Origin;
        position.Z += _config.CorpseMarkerZOffset;
        var angles = transform.Angles;
        angles.X = 0;
        angles.Z = 0;
        marker.Teleport(position, angles, new Vector());
        marker.AcceptInput("DisableCollision");
        _corpseMarkers[key] = marker;
    }

    private void RemoveCorpseMarker(int key)
    {
        if (_corpseMarkers.Remove(key, out var marker) && marker.IsValidEntity)
        {
            marker.AcceptInput("Kill");
        }
    }

    private void ClearCorpseMarkers()
    {
        foreach (var marker in _corpseMarkers.Values)
        {
            if (marker.IsValidEntity)
            {
                marker.AcceptInput("Kill");
            }
        }

        _corpseMarkers.Clear();
    }
}
