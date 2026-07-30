using Microsoft.Extensions.Logging;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace SharpGameModes.BotMatch;

internal sealed class ConVarLease(
    IConVarManager conVars,
    ILogger logger)
{
    private readonly Dictionary<string, string> _originalValues = new(StringComparer.Ordinal);
    private bool _active;

    public void Acquire(IReadOnlyDictionary<string, string> desiredValues)
    {
        if (_active)
        {
            return;
        }

        _active = true;
        foreach (var (name, value) in desiredValues)
        {
            var conVar = Find(name);
            if (conVar is null)
            {
                logger.LogWarning("Bot-match ConVar {ConVar} was not found; this enhancement is unavailable.", name);
                continue;
            }

            try
            {
                _originalValues.Add(name, conVar.GetString());
                conVar.SetString(value);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to acquire bot-match ConVar {ConVar}.", name);
            }
        }
    }

    public bool SetOwned(string name, string value)
    {
        if (!_active || !_originalValues.ContainsKey(name))
        {
            return false;
        }

        var conVar = Find(name);
        if (conVar is null)
        {
            return false;
        }

        try
        {
            conVar.SetString(value);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update leased bot-match ConVar {ConVar}.", name);
            return false;
        }
    }

    public void Reapply(IReadOnlyDictionary<string, string> desiredValues)
    {
        if (!_active)
        {
            return;
        }

        foreach (var (name, value) in desiredValues)
        {
            SetOwned(name, value);
        }
    }

    public void Release()
    {
        if (!_active)
        {
            return;
        }

        foreach (var (name, value) in _originalValues.Reverse())
        {
            var conVar = Find(name);
            if (conVar is null)
            {
                continue;
            }

            try
            {
                conVar.SetString(value);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to restore bot-match ConVar {ConVar}.", name);
            }
        }

        _originalValues.Clear();
        _active = false;
    }

    private IConVar? Find(string name)
        => conVars.FindConVar(name) ?? conVars.FindConVar(name, useIterator: true);
}
