using CodexMulti.Auth;
using CodexMulti.Configuration;
using CodexMulti.Infrastructure;
using CodexMulti.Profiles;

namespace CodexMulti.Run;

internal sealed class CodexMultiRunner
{
    private static readonly TimeSpan StateLockTimeout = TimeSpan.FromSeconds(30);

    private readonly AppConfigStore _configStore;
    private readonly RuntimeStateStore _runtimeStateStore;
    private readonly ProfileStore _profileStore;
    private readonly AuthJsonSwitcher _authSwitcher;
    private readonly AppLogger _logger;

    public CodexMultiRunner(
        AppConfigStore configStore,
        RuntimeStateStore runtimeStateStore,
        ProfileStore profileStore,
        AuthJsonSwitcher authSwitcher,
        AppLogger logger)
    {
        _configStore = configStore;
        _runtimeStateStore = runtimeStateStore;
        _profileStore = profileStore;
        _authSwitcher = authSwitcher;
        _logger = logger;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var runId = CreateRunId();
        var exhaustedProfiles = new HashSet<string>(StringComparer.Ordinal);
        var lease = await PrepareLaunchAsync(runId);
        SessionReference? sessionReference = null;

        while (true)
        {
            _logger.Info($"Run '{runId}' launching with profile '{lease.ProfileName}' generation {lease.Generation}.");

            var watcher = new Sessions.SessionLogWatcher(_configStore.Paths.CodexSessionsDirectory, _logger);
            var processRunner = new CodexProcessRunner(_logger, watcher);
            var launchPlan = sessionReference is null
                ? LaunchPlan.Raw(lease.CodexExecutable, args)
                : LaunchPlan.Resume(lease.CodexExecutable, sessionReference.SessionId, lease.ResumePrompt);

            var result = await processRunner.RunAsync(launchPlan, sessionReference);
            if (!string.IsNullOrWhiteSpace(result.SessionId) && !string.IsNullOrWhiteSpace(result.RolloutPath))
            {
                sessionReference = new SessionReference(result.SessionId!, result.RolloutPath!);
            }

            if (!result.RateLimitDetected)
            {
                await FinalizeSuccessfulRunAsync(lease);
                return result.ExitCode;
            }

            exhaustedProfiles.Add(lease.ProfileName);
            _logger.Info($"Run '{runId}' detected rate limit for profile '{lease.ProfileName}'.");

            if (sessionReference is null)
            {
                throw new UserFacingException("Rate limit was detected, but the current session id could not be determined.");
            }

            lease = await ResolveAfterRateLimitAsync(runId, lease, exhaustedProfiles);
            Console.Error.WriteLine($"Rate limit detected. Using profile '{lease.ProfileName}' and resuming.");
        }
    }

    private async Task<RunProfileLease> PrepareLaunchAsync(string runId)
    {
        using var stateLock = AcquireStateLock();

        var config = await _configStore.LoadAsync();
        var runtimeState = await _runtimeStateStore.LoadAsync();
        var orderedProfiles = await LoadOrderedProfilesAsync(config);
        var activeProfile = ResolveActiveProfile(config, runtimeState, orderedProfiles);

        await EnsureSharedAuthForProfileAsync(activeProfile);

        var configChanged = ApplyOrderedProfiles(config, orderedProfiles);
        if (!string.Equals(config.ActiveProfile, activeProfile, StringComparison.Ordinal))
        {
            config.ActiveProfile = activeProfile;
            configChanged = true;
        }

        var runtimeChanged = false;
        if (!string.Equals(runtimeState.ActiveProfile, activeProfile, StringComparison.Ordinal))
        {
            runtimeState.ActiveProfile = activeProfile;
            runtimeState.UpdatedAtUtc = DateTimeOffset.UtcNow;
            runtimeState.UpdatedByRunId = runId;
            runtimeChanged = true;
        }

        if (configChanged)
        {
            await _configStore.SaveAsync(config);
        }

        if (runtimeChanged || !File.Exists(_runtimeStateStore.Paths.RuntimeStateFilePath))
        {
            await _runtimeStateStore.SaveAsync(runtimeState);
        }

        return CreateLease(config, runtimeState, activeProfile);
    }

    private async Task<RunProfileLease> ResolveAfterRateLimitAsync(
        string runId,
        RunProfileLease currentLease,
        HashSet<string> exhaustedProfiles)
    {
        using var stateLock = AcquireStateLock();

        var config = await _configStore.LoadAsync();
        var runtimeState = await _runtimeStateStore.LoadAsync();
        var orderedProfiles = await LoadOrderedProfilesAsync(config);
        var activeProfile = ResolveActiveProfile(config, runtimeState, orderedProfiles);

        var generationMatches = runtimeState.Generation == currentLease.Generation;
        var profileMatches = string.Equals(runtimeState.ActiveProfile, currentLease.ProfileName, StringComparison.Ordinal);

        if (generationMatches && profileMatches)
        {
            await _profileStore.SyncSharedAuthToProfileIfMatchesAsync(currentLease.ProfileName);

            var nextProfile = SelectNextProfile(orderedProfiles, currentLease.ProfileName, exhaustedProfiles)
                ?? throw new UserFacingException("All available profiles have been exhausted or failed with rate limits.");

            await EnsureSharedAuthForProfileAsync(nextProfile);

            runtimeState.ActiveProfile = nextProfile;
            runtimeState.Generation++;
            runtimeState.UpdatedAtUtc = DateTimeOffset.UtcNow;
            runtimeState.UpdatedByRunId = runId;

            ApplyOrderedProfiles(config, orderedProfiles);
            config.ActiveProfile = nextProfile;

            await _configStore.SaveAsync(config);
            await _runtimeStateStore.SaveAsync(runtimeState);

            _logger.Info($"Run '{runId}' switched shared active profile from '{currentLease.ProfileName}' to '{nextProfile}'.");
            return CreateLease(config, runtimeState, nextProfile);
        }

        await EnsureSharedAuthForProfileAsync(activeProfile);

        var configChanged = ApplyOrderedProfiles(config, orderedProfiles);
        if (!string.Equals(config.ActiveProfile, activeProfile, StringComparison.Ordinal))
        {
            config.ActiveProfile = activeProfile;
            configChanged = true;
        }

        if (configChanged)
        {
            await _configStore.SaveAsync(config);
        }

        _logger.Info(
            $"Run '{runId}' detected that another instance already switched shared profile to '{activeProfile}' generation {runtimeState.Generation}.");

        return CreateLease(config, runtimeState, activeProfile);
    }

    private async Task FinalizeSuccessfulRunAsync(RunProfileLease lease)
    {
        using var stateLock = AcquireStateLock();

        var runtimeState = await _runtimeStateStore.LoadAsync();
        if (runtimeState.Generation != lease.Generation ||
            !string.Equals(runtimeState.ActiveProfile, lease.ProfileName, StringComparison.Ordinal))
        {
            return;
        }

        await _profileStore.SyncSharedAuthToProfileIfMatchesAsync(lease.ProfileName);
    }

    private async Task EnsureSharedAuthForProfileAsync(string profileName)
    {
        if (await _profileStore.SharedAuthMatchesProfileAsync(profileName))
        {
            await _profileStore.SyncSharedAuthToProfileIfMatchesAsync(profileName);
            return;
        }

        await _authSwitcher.SwitchToProfileAsync(profileName);
    }

    private async Task<IReadOnlyList<string>> LoadOrderedProfilesAsync(AppConfig config)
    {
        var profiles = (await _profileStore.ListAsync()).Select(profile => profile.Name).ToArray();
        if (profiles.Length == 0)
        {
            throw new UserFacingException("No profiles saved. Use `codex-multi auth save <name>` first.");
        }

        return OrderProfiles(config, profiles);
    }

    private static string ResolveActiveProfile(AppConfig config, RuntimeState runtimeState, IReadOnlyList<string> orderedProfiles)
    {
        if (!string.IsNullOrWhiteSpace(runtimeState.ActiveProfile) &&
            orderedProfiles.Contains(runtimeState.ActiveProfile, StringComparer.Ordinal))
        {
            return runtimeState.ActiveProfile;
        }

        if (!string.IsNullOrWhiteSpace(config.ActiveProfile) &&
            orderedProfiles.Contains(config.ActiveProfile, StringComparer.Ordinal))
        {
            return config.ActiveProfile;
        }

        return orderedProfiles[0];
    }

    private static IReadOnlyList<string> OrderProfiles(AppConfig config, IReadOnlyList<string> availableProfiles)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var profile in config.ProfileOrder)
        {
            if (availableProfiles.Contains(profile, StringComparer.Ordinal) && seen.Add(profile))
            {
                ordered.Add(profile);
            }
        }

        foreach (var profile in availableProfiles.OrderBy(profile => profile, StringComparer.Ordinal))
        {
            if (seen.Add(profile))
            {
                ordered.Add(profile);
            }
        }

        return ordered;
    }

    private static string? SelectNextProfile(
        IReadOnlyList<string> orderedProfiles,
        string currentProfile,
        IReadOnlySet<string> exhaustedProfiles)
    {
        var currentIndex = -1;
        for (var index = 0; index < orderedProfiles.Count; index++)
        {
            if (string.Equals(orderedProfiles[index], currentProfile, StringComparison.Ordinal))
            {
                currentIndex = index;
                break;
            }
        }

        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        for (var offset = 1; offset <= orderedProfiles.Count; offset++)
        {
            var candidate = orderedProfiles[(currentIndex + offset) % orderedProfiles.Count];
            if (!exhaustedProfiles.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool ApplyOrderedProfiles(AppConfig config, IReadOnlyList<string> orderedProfiles)
    {
        if (config.ProfileOrder.SequenceEqual(orderedProfiles, StringComparer.Ordinal))
        {
            return false;
        }

        config.ProfileOrder = orderedProfiles.ToList();
        return true;
    }

    private static RunProfileLease CreateLease(AppConfig config, RuntimeState runtimeState, string profileName)
    {
        return new RunProfileLease(
            profileName,
            runtimeState.Generation,
            config.CodexExecutable,
            config.ResumePrompt);
    }

    private InstanceLock AcquireStateLock()
    {
        return InstanceLock.Acquire(
            _configStore.Paths.StateLockFilePath,
            "Another codex-multi instance is updating shared auth state. Please retry.",
            StateLockTimeout);
    }

    private static string CreateRunId()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Environment.ProcessId}-{suffix}";
    }
}

internal sealed record SessionReference(string SessionId, string RolloutPath);

internal sealed record RunProfileLease(
    string ProfileName,
    long Generation,
    string CodexExecutable,
    string ResumePrompt);
