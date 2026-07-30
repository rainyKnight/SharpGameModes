using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;

namespace SharpGameModes.ZombieInfection;

public sealed partial class ZombieInfectionModule
{
    private void InstallHooks()
    {
        if (_hooksInstalled)
        {
            return;
        }

        _hooks.PlayerGetMaxSpeed.InstallHookPre(OnGetMaxSpeed);
        _hooks.PlayerWeaponCanEquip.InstallHookPre(OnCanEquipWeapon);
        _hooks.PlayerWeaponCanUse.InstallHookPre(OnCanUseWeapon);
        _hooks.PlayerCanAcquire.InstallHookPre(OnCanAcquire);
        _hooks.PlayerDispatchTraceAttack.InstallHookPre(OnTraceAttackPre);
        _hooks.PlayerDispatchTraceAttack.InstallHookPost(OnTraceAttackPost);
        _hooks.PlayerRunCommand.InstallHookPre(OnPlayerRunCommand);
        _hooks.EmitSound.InstallHookPre(OnEmitSound);
        _hooks.SoundEvent.InstallHookPre(OnSoundEvent);
        _hooksInstalled = true;
    }

    private void RemoveHooks()
    {
        if (!_hooksInstalled)
        {
            return;
        }

        _hooks.PlayerGetMaxSpeed.RemoveHookPre(OnGetMaxSpeed);
        _hooks.PlayerWeaponCanEquip.RemoveHookPre(OnCanEquipWeapon);
        _hooks.PlayerWeaponCanUse.RemoveHookPre(OnCanUseWeapon);
        _hooks.PlayerCanAcquire.RemoveHookPre(OnCanAcquire);
        _hooks.PlayerDispatchTraceAttack.RemoveHookPre(OnTraceAttackPre);
        _hooks.PlayerDispatchTraceAttack.RemoveHookPost(OnTraceAttackPost);
        _hooks.PlayerRunCommand.RemoveHookPre(OnPlayerRunCommand);
        _hooks.EmitSound.RemoveHookPre(OnEmitSound);
        _hooks.SoundEvent.RemoveHookPre(OnSoundEvent);
        _hooksInstalled = false;
    }

    private HookReturnValue<float> OnGetMaxSpeed(
        IPlayerGetMaxSpeedHookParams param,
        HookReturnValue<float> result)
    {
        if (!IsActive() || !IsEligible(param.Controller))
        {
            return result;
        }

        var multiplier = param.Controller.Team == CStrikeTeam.TE ? _config.ZombieSpeed : _config.HumanSpeed;
        return new HookReturnValue<float>(EHookAction.SkipCallReturnOverride, param.OriginalSpeed * multiplier);
    }

    private HookReturnValue<bool> OnCanEquipWeapon(
        IPlayerWeaponCanEquipHookParams param,
        HookReturnValue<bool> result)
    {
        if (IsZombieWithForbiddenWeapon(param.Controller, param.Weapon))
        {
            return new HookReturnValue<bool>(EHookAction.SkipCallReturnOverride, false);
        }

        return result;
    }

    private HookReturnValue<bool> OnCanUseWeapon(
        IPlayerWeaponCanUseHookParams param,
        HookReturnValue<bool> result)
    {
        if (IsZombieWithForbiddenWeapon(param.Controller, param.Weapon))
        {
            return new HookReturnValue<bool>(EHookAction.SkipCallReturnOverride, false);
        }

        return result;
    }

    private HookReturnValue<EAcquireResult> OnCanAcquire(
        IPlayerCanAcquireHookParams param,
        HookReturnValue<EAcquireResult> result)
    {
        if (IsActive() && IsEligible(param.Controller) && param.Controller.Team == CStrikeTeam.TE
            && param.Method == EAcquireMethod.PickUp)
        {
            return new HookReturnValue<EAcquireResult>(
                EHookAction.SkipCallReturnOverride,
                EAcquireResult.NotAllowedByProhibition);
        }

        return result;
    }

    private HookReturnValue<long> OnTraceAttackPre(
        IPlayerDispatchTraceAttackHookParams param,
        HookReturnValue<long> result)
    {
        if (!IsActive() || !IsEligible(param.Controller))
        {
            return result;
        }

        if (_config.DisableFallDamage && (param.DamageType & DamageFlagBits.Fall) != 0)
        {
            ArmFallSoundSuppression(param.Controller);
            param.Damage = 0;
            param.OriginalDamage = 0;
            ClearDamageFeedback(param.Pawn);
            return new HookReturnValue<long>(EHookAction.SkipCallReturnOverride, 0);
        }

        if (_phase != ZombiePhase.Active || param.Controller.Team != CStrikeTeam.CT)
        {
            return result;
        }

        var attackerPawn = _entities.FindEntityByHandle(param.AttackerPawnHandle);
        var attacker = attackerPawn?.GetControllerAuto();
        if (!IsEligible(attacker) || attacker.Team != CStrikeTeam.TE || ReferenceEquals(attacker, param.Controller)
            || attackerPawn?.GetActiveWeapon() is not { IsKnife: true } weapon)
        {
            return result;
        }

        var heavy = weapon.WeaponMode == CStrikeWeaponMode.Secondary
            || param.Damage >= _config.ZombieKnifeHeavyDamageThreshold;
        param.Damage = heavy ? _config.ZombieKnifeHeavyDamage : _config.ZombieKnifeLightDamage;
        return result;
    }

    private void OnTraceAttackPost(
        IPlayerDispatchTraceAttackHookParams param,
        HookReturnValue<long> result)
    {
        if (!IsActive() || !IsEligible(param.Controller))
        {
            return;
        }

        if (_config.DisableDamageShake)
        {
            ClearDamageFeedback(param.Pawn);
        }

        if (!_config.KnockbackEnabled || _phase != ZombiePhase.Active || param.Controller.Team != CStrikeTeam.TE
            || param.DamageDealt <= 0 || (param.DamageType & DamageFlagBits.Bullet) == 0)
        {
            return;
        }

        var attackerPawn = _entities.FindEntityByHandle(param.AttackerPawnHandle);
        var attacker = attackerPawn?.GetControllerAuto();
        if (!IsEligible(attacker) || attacker.Team != CStrikeTeam.CT || attackerPawn is null)
        {
            return;
        }

        var force = Math.Clamp(
            _config.KnockbackBaseForce + param.DamageDealt * _config.KnockbackDamageScale,
            0,
            3000);
        var eyeAngles = attackerPawn.GetEyeAngles();
        const float toRadians = MathF.PI / 180;
        var pitch = eyeAngles.X * toRadians;
        var yaw = eyeAngles.Y * toRadians;
        var cosPitch = MathF.Cos(pitch);
        var direction = new Vector(
            cosPitch * MathF.Cos(yaw),
            cosPitch * MathF.Sin(yaw),
            -MathF.Sin(pitch));
        var velocity = param.Pawn.GetAbsVelocity();
        velocity.X += direction.X * force;
        velocity.Y += direction.Y * force;
        velocity.Z += direction.Z * force + _config.KnockbackVerticalBoost;

        if (_config.KnockbackMaxHorizontalSpeed > 0)
        {
            var horizontalSpeed = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
            if (horizontalSpeed > _config.KnockbackMaxHorizontalSpeed)
            {
                var scale = _config.KnockbackMaxHorizontalSpeed / horizontalSpeed;
                velocity.X *= scale;
                velocity.Y *= scale;
            }
        }

        param.Pawn.SetAbsVelocity(velocity);
        if (_config.DisableHitSlowdown)
        {
            param.Pawn.TransientChangeVelocityModifier(1);
        }
    }

    private HookReturnValue<EmptyHookReturn> OnPlayerRunCommand(
        IPlayerRunCommandHookParams param,
        HookReturnValue<EmptyHookReturn> result)
    {
        if (IsActive()
            && _config.DisableFallDamage
            && IsEligible(param.Controller)
            && MathF.Abs(param.Pawn.GetAbsVelocity().Z) >= _config.FallSoundVelocityThreshold)
        {
            ArmFallSoundSuppression(param.Controller);
        }

        return result;
    }

    private void ArmFallSoundSuppression(IPlayerController? player)
    {
        if (IsActive() && _config.DisableFallDamage && IsEligible(player))
        {
            _fallSoundSuppressUntil[PlayerKey(player)]
                = DateTimeOffset.UtcNow.AddSeconds(_config.FallSoundSuppressSeconds);
        }
    }

    private HookReturnValue<SoundOpEventGuid> OnEmitSound(
        IEmitSoundHookParams param,
        HookReturnValue<SoundOpEventGuid> result)
    {
        if (!IsActive() || !_config.DisableFallDamage)
        {
            return result;
        }

        var suppressedPlayers = RemoveSuppressedFallSoundReceivers(
            param.HasReceiver,
            param.RemoveReceiver);
        if (suppressedPlayers.Count == 0)
        {
            return result;
        }

        if (_config.DebugFallSoundMessages)
        {
            _logger.LogInformation(
                "Removed zombie fall-sound receivers [{Players}] from emitted sound {SoundName} at entity {EntityIndex}.",
                string.Join(", ", suppressedPlayers),
                param.SoundName,
                param.EntityIndex);
        }

        return new HookReturnValue<SoundOpEventGuid>(EHookAction.ChangeParamReturnDefault);
    }

    private HookReturnValue<SoundOpEventGuid> OnSoundEvent(
        ISoundEventHookParams param,
        HookReturnValue<SoundOpEventGuid> result)
    {
        if (!IsActive() || !_config.DisableFallDamage)
        {
            return result;
        }

        var suppressedPlayers = RemoveSuppressedFallSoundReceivers(
            param.HasReceiver,
            param.RemoveReceiver);
        if (suppressedPlayers.Count == 0)
        {
            return result;
        }

        if (_config.DebugFallSoundMessages)
        {
            _logger.LogInformation(
                "Removed zombie fall-sound receivers [{Players}] from sound event {SoundName}.",
                string.Join(", ", suppressedPlayers),
                param.SoundName);
        }

        return new HookReturnValue<SoundOpEventGuid>(EHookAction.ChangeParamReturnDefault);
    }

    private List<string> RemoveSuppressedFallSoundReceivers(
        Func<Sharp.Shared.Units.PlayerSlot, bool> hasReceiver,
        Action<Sharp.Shared.Units.PlayerSlot> removeReceiver)
    {
        var now = DateTimeOffset.UtcNow;
        var suppressedPlayers = new List<string>();
        foreach (var client in GetEligibleClients())
        {
            var slot = client.Slot;
            if (!_fallSoundSuppressUntil.TryGetValue(slot.AsPrimitive(), out var until))
            {
                continue;
            }

            if (now > until)
            {
                _fallSoundSuppressUntil.Remove(slot.AsPrimitive());
                continue;
            }

            if (!hasReceiver(slot))
            {
                continue;
            }

            removeReceiver(slot);
            suppressedPlayers.Add(client.Name);
        }

        return suppressedPlayers;
    }

    private bool IsZombieWithForbiddenWeapon(IPlayerController controller, IBaseWeapon weapon)
        => IsActive() && IsEligible(controller) && controller.Team == CStrikeTeam.TE && !weapon.IsKnife;

    private static bool IsKnifeName(string weapon)
        => weapon.Contains("knife", StringComparison.OrdinalIgnoreCase);

    private static void ClearDamageFeedback(IPlayerPawn pawn)
    {
        pawn.ApplyStressDamage = false;
        pawn.FlinchStack = 0;
        if (pawn.GetAimPunchService() is { } aimPunch)
        {
            aimPunch.PredictableBaseAngle = new Vector();
            aimPunch.PredictableBaseAngleVel = new Vector();
            aimPunch.UnpredictableBaseAngle = new Vector();
        }
    }
}
