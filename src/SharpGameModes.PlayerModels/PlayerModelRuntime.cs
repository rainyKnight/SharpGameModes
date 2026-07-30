using Microsoft.Extensions.Logging;
using SharpGameModes.Domain;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace SharpGameModes.PlayerModels;

public sealed partial class PlayerModelsModule
{
    private void InstallHooks()
    {
        if (_hooksInstalled)
        {
            return;
        }

        _hooks.PlayerSpawnPost.InstallForward(OnPlayerSpawned, ListenerPriority);
        _hooksInstalled = true;
    }

    private void RemoveHooks()
    {
        if (!_hooksInstalled)
        {
            return;
        }

        _hooks.PlayerSpawnPost.RemoveForward(OnPlayerSpawned);
        _hooksInstalled = false;
    }

    private void OnPlayerSpawned(IPlayerSpawnForwardParams parameters)
    {
        if (!_config.Enabled
            || !IsAvailableForTeam(parameters.Controller.Team)
            || !IsHuman(parameters.Client)
            || !IsPlayingTeam(parameters.Controller.Team)
            || !parameters.Pawn.IsValid())
        {
            return;
        }

        CaptureOriginalModel(parameters.Client, parameters.Controller.Team, parameters.Pawn);
        ApplyCurrentModel(parameters.Client);
    }

    private void CaptureOriginalModel(IGameClient client, CStrikeTeam team, IPlayerPawn pawn)
    {
        var modelName = pawn.GetBodyComponent()
            .GetSceneNode()?
            .AsSkeletonInstance?
            .GetModelState()
            .ModelName;
        if (string.IsNullOrWhiteSpace(modelName)
            || _config.Models.Values.Any(
                model => model.Path.Equals(modelName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _originalModels[(client.SteamId.AsPrimitive(), team)] = modelName;
    }

    private void ApplyCurrentModel(IGameClient client)
    {
        if (!_config.Enabled
            || !IsHuman(client)
            || _preferences?.Instance is not { } preferences
            || !preferences.IsLoaded(client.SteamId)
            || client.GetPlayerController() is not { } controller
            || !IsAvailableForTeam(controller.Team)
            || controller.GetPlayerPawn() is not { IsAlive: true } pawn)
        {
            return;
        }

        ValidateSelections(client);
        CaptureOriginalModel(client, controller.Team, pawn);
        var side = ToModelSide(controller.Team);
        var selection = GetDefaultRule(client, side)?.Force == true
            ? "@default"
            : GetSelection(client, side);
        var model = ResolveSelection(client, side, selection);
        if (model is null)
        {
            RestoreOriginalModel(client, controller.Team, pawn);
            return;
        }

        try
        {
            pawn.SetModel(model.Path);
            SetLegVisibility(pawn, model.DisableLeg);
            ApplySkin(client, pawn, model);
            ApplyMeshGroups(client, pawn, model);
            _appliedModels[(client.SteamId.AsPrimitive(), controller.Team)] = model.Index;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not apply player model {Model} to {SteamId}.",
                model.Index,
                client.SteamId.AsPrimitive());
        }
    }

    private void RestoreOriginalModel(IGameClient client, CStrikeTeam team, IPlayerPawn pawn)
    {
        var key = (client.SteamId.AsPrimitive(), team);
        if (_originalModels.TryGetValue(key, out var originalModel)
            && !string.IsNullOrWhiteSpace(originalModel))
        {
            pawn.SetModel(originalModel);
        }

        SetLegVisibility(pawn, _config.DisableDefaultModelLeg);
        _appliedModels.Remove(key);
    }

    private static void SetLegVisibility(IPlayerPawn pawn, bool hideLegs)
    {
        var color = pawn.RenderColor;
        pawn.RenderColor = new Color32(color.R, color.G, color.B, hideLegs ? (byte)254 : (byte)255);
    }

    private void ApplySkin(IGameClient client, IPlayerPawn pawn, PlayerModelDefinition model)
    {
        var skin = model.FixedSkin >= 0 ? model.FixedSkin : GetSkin(client, model.Index);
        pawn.AcceptInput("Skin", pawn, pawn, skin);
    }

    private void ApplyMeshGroups(IGameClient client, IPlayerPawn pawn, PlayerModelDefinition model)
    {
        if (model.MeshGroups.Count == 0 && model.FixedMeshGroups.Count == 0)
        {
            return;
        }

        if (!HasMeshGroupCookie(client, model.Index))
        {
            var currentMask = pawn.GetBodyComponent()
                .GetSceneNode()?
                .AsSkeletonInstance?
                .GetModelState()
                .MeshGroupMask ?? 0;
            SetMeshGroups(client, model.Index, PlayerModelMeshGroups.EnabledGroups(currentMask));
        }

        var mask = PlayerModelMeshGroups.CalculateMask(
            GetMeshGroups(client, model.Index),
            model.FixedMeshGroups);
        if (mask != 0)
        {
            pawn.SetMaterialGroupMask(mask);
        }
    }

    private bool TryGetAppliedModel(
        IGameClient client,
        out IPlayerPawn pawn,
        out PlayerModelDefinition model)
    {
        pawn = null!;
        model = null!;
        if (!IsHuman(client)
            || client.GetPlayerController() is not { } controller
            || !IsAvailableForTeam(controller.Team)
            || controller.GetPlayerPawn() is not { IsAlive: true } currentPawn
            || !_appliedModels.TryGetValue(
                (client.SteamId.AsPrimitive(), controller.Team),
                out var modelIndex)
            || !_config.Models.TryGetValue(modelIndex, out var currentModel))
        {
            return false;
        }

        pawn = currentPawn;
        model = currentModel;
        return true;
    }

    private void ReapplyCurrentModel(IGameClient client)
        => ApplyCurrentModel(client);
}
