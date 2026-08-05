using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Managers;

namespace SharpGameModes.BotMatch;

/// <summary>
/// Mounts the selected upstream BotProfile database before Source 2 creates a
/// game server. Bot profiles are process-global engine data, so the search path
/// remains mounted while the module is loaded; non-BotMatch modes freeze or
/// remove their native Bots through the existing mode configuration.
/// </summary>
internal sealed class BotProfileMountRuntime : IDisposable
{
    private const int PathAddToHead = 0;
    private const int SearchPathPriorityDirectory = 1;
    private const int SearchPathPriorityVpk = 2;

    private readonly IFileManager _files;
    private readonly ILogger _logger;
    private readonly string _overridesRoot;
    private readonly string _difficultyTier;
    private string? _mountedPath;
    private BotProfileSource? _source;
    private byte[]? _expectedDatabaseSha256;
    private int _resolvedDatabaseBytes;
    private long _mountAttempts;
    private long _mounts;
    private long _errors;

    public BotProfileMountRuntime(
        IFileManager files,
        ILogger logger,
        string overridesRoot,
        string difficultyTier)
    {
        _files = files;
        _logger = logger;
        _overridesRoot = overridesRoot;
        _difficultyTier = difficultyTier;
    }

    public bool IsReady => _source is not null && _resolvedDatabaseBytes > 0;

    public bool Mount(string lifecycle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lifecycle);
        Interlocked.Increment(ref _mountAttempts);

        var source = BotProfileSourceResolver.Resolve(
            _overridesRoot,
            _difficultyTier);
        if (source is null)
        {
            Interlocked.Increment(ref _errors);
            _logger.LogError(
                "Cannot mount BotProfile tier {Tier} during {Lifecycle}: expected {Root}/<Low|Medium|HLTVTop10|High>/botprofile.vpk or botprofile.db.",
                _difficultyTier,
                lifecycle,
                _overridesRoot);
            return false;
        }

        _expectedDatabaseSha256 = source.Value.ExpectedDatabaseBytes > 0
            ? SHA256.HashData(File.ReadAllBytes(source.Value.DatabasePath))
            : null;
        var existing = ResolveDatabaseFingerprint();
        if (MatchesSelectedSource(source.Value, existing))
        {
            _mountedPath = null;
            _source = source;
            _resolvedDatabaseBytes = existing.Bytes;
            Interlocked.Increment(ref _mounts);
            _logger.LogInformation(
                "Verified BotProfile tier {Tier} from {Source} during {Lifecycle}; the existing GAME search path resolves the exact selected botprofile.db at {Bytes} bytes and the expected SHA-256 fingerprint.",
                source.Value.DifficultyTier,
                source.Value.DatabasePath,
                lifecycle,
                _resolvedDatabaseBytes);
            return true;
        }

        try
        {
            RemoveMountedPath();
            _files.AddSearchPath(
                source.Value.SearchPath,
                "GAME",
                PathAddToHead,
                source.Value.Format == BotProfileSourceFormat.Vpk
                    ? SearchPathPriorityVpk
                    : SearchPathPriorityDirectory);
            _mountedPath = source.Value.SearchPath;
            _source = source;

            var resolved = ResolveDatabaseFingerprint();
            _resolvedDatabaseBytes = resolved.Bytes;
            if (!BotProfileValidationPolicy.TryValidate(
                    _resolvedDatabaseBytes,
                    source.Value.ExpectedDatabaseBytes,
                    resolved.Sha256,
                    _expectedDatabaseSha256 ?? [],
                    out var validationError))
            {
                throw new InvalidDataException(
                    $"The mounted BotProfile is invalid: {validationError}.");
            }

            Interlocked.Increment(ref _mounts);
            _logger.LogInformation(
                "Mounted BotProfile tier {Tier} from {Source} as a {Format} GAME search path during {Lifecycle}; resolved botprofile.db is {Bytes} bytes with the expected SHA-256 fingerprint.",
                source.Value.DifficultyTier,
                source.Value.DatabasePath,
                source.Value.Format,
                lifecycle,
                _resolvedDatabaseBytes);
            return true;
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _errors);
            _logger.LogError(
                exception,
                "Failed to mount BotProfile tier {Tier} from {Source} during {Lifecycle}.",
                source.Value.DifficultyTier,
                source.Value.DatabasePath,
                lifecycle);
            RemoveMountedPath();
            return false;
        }
    }

    public string GetStatus()
    {
        var source = _source;
        return IsReady && source is not null
            ? $"BotProfile ready: tier {source.Value.DifficultyTier}, " +
              $"format {source.Value.Format}, database {source.Value.DatabasePath}, " +
              $"resolved bytes {_resolvedDatabaseBytes}, fingerprint verified, " +
              $"mounts {Interlocked.Read(ref _mounts)}/" +
              $"{Interlocked.Read(ref _mountAttempts)}, errors {Interlocked.Read(ref _errors)}."
            : $"BotProfile unavailable: requested tier {_difficultyTier}, root {_overridesRoot}, " +
              $"mounts {Interlocked.Read(ref _mounts)}/{Interlocked.Read(ref _mountAttempts)}, " +
              $"errors {Interlocked.Read(ref _errors)}.";
    }

    public void Dispose()
        => RemoveMountedPath();

    private DatabaseFingerprint ResolveDatabaseFingerprint()
    {
        using var database = _files.OpenFile("botprofile.db", "GAME");
        var size = database?.Size() ?? 0;
        if (database is null || size <= 0)
        {
            return new DatabaseFingerprint(size, null);
        }

        var contents = new byte[size];
        database.Read(contents);
        return new DatabaseFingerprint(size, SHA256.HashData(contents));
    }

    private bool MatchesSelectedSource(
        BotProfileSource source,
        DatabaseFingerprint resolved)
        => BotProfileValidationPolicy.TryValidate(
            resolved.Bytes,
            source.ExpectedDatabaseBytes,
            resolved.Sha256,
            _expectedDatabaseSha256 ?? [],
            out _);

    private void RemoveMountedPath()
    {
        if (_mountedPath is not { } path)
        {
            _source = null;
            _expectedDatabaseSha256 = null;
            _resolvedDatabaseBytes = 0;
            return;
        }

        try
        {
            _files.RemoveSearchPath(path, "GAME");
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _errors);
            _logger.LogWarning(
                exception,
                "Failed to remove BotProfile GAME search path {Path}.",
                path);
        }
        finally
        {
            _mountedPath = null;
            _source = null;
            _expectedDatabaseSha256 = null;
            _resolvedDatabaseBytes = 0;
        }
    }

    private readonly record struct DatabaseFingerprint(
        int Bytes,
        byte[]? Sha256);
}
