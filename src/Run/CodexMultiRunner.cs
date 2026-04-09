using CodexMulti.Auth;
using CodexMulti.Configuration;
using CodexMulti.Infrastructure;
using CodexMulti.Profiles;
using CodexMulti.Sessions;

namespace CodexMulti.Run;

internal sealed class CodexMultiRunner
{
    private readonly AppConfigStore _configStore;
    private readonly ProfileStore _profileStore;
    private readonly AuthJsonSwitcher _authSwitcher;
    private readonly AppLogger _logger;

    public CodexMultiRunner(
        AppConfigStore configStore,
        ProfileStore profileStore,
        AuthJsonSwitcher authSwitcher,
        AppLogger logger)
    {
        _configStore = configStore;
        _profileStore = profileStore;
        _authSwitcher = authSwitcher;
        _logger = logger;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var config = await _configStore.LoadAsync();
        var profiles = (await _profileStore.ListAsync()).ToDictionary(profile => profile.Name, StringComparer.Ordinal);
        if (profiles.Count == 0)
        {
            throw new UserFacingException("No profiles saved. Use `codex-multi auth save <name>` first.");
        }

        var orderedProfiles = OrderProfiles(config, profiles.Keys);
        if (orderedProfiles.Count == 0)
        {
            throw new UserFacingException("No profiles saved. Use `codex-multi auth save <name>` first.");
        }

        var currentProfile = ResolveInitialProfile(config, orderedProfiles);
        var exhaustedProfiles = new HashSet<string>(StringComparer.Ordinal);
        SessionReference? sessionReference = null;
        var isRetry = false;

        while (true)
        {
            await ActivateProfileAsync(config, currentProfile);

            if (isRetry)
            {
                Console.Error.WriteLine($"Rate limit detected. Switching to profile '{currentProfile}' and resuming.");
            }

            _logger.Info($"Using profile '{currentProfile}'.");

            var watcher = new SessionLogWatcher(_configStore.Paths.CodexSessionsDirectory, _logger);
            var processRunner = new CodexProcessRunner(_logger, watcher);

            var launchPlan = isRetry
                ? LaunchPlan.Resume(config.CodexExecutable, sessionReference!.SessionId, config.ResumePrompt)
                : LaunchPlan.Raw(config.CodexExecutable, args);

            var result = await processRunner.RunAsync(launchPlan, sessionReference);
            if (!string.IsNullOrWhiteSpace(result.SessionId) && !string.IsNullOrWhiteSpace(result.RolloutPath))
            {
                sessionReference = new SessionReference(result.SessionId!, result.RolloutPath!);
            }

            if (!result.RateLimitDetected)
            {
                return result.ExitCode;
            }

            exhaustedProfiles.Add(currentProfile);
            _logger.Info($"Rate limit detected for profile '{currentProfile}'.");

            if (sessionReference is null)
            {
                throw new UserFacingException("Rate limit was detected, but the current session id could not be determined.");
            }

            currentProfile = SelectNextProfile(orderedProfiles, currentProfile, exhaustedProfiles)
                ?? throw new UserFacingException("All available profiles have been exhausted or failed with rate limits.");

            isRetry = true;
        }
    }

    private async Task ActivateProfileAsync(AppConfig config, string profileName)
    {
        await _authSwitcher.SwitchToProfileAsync(profileName);
        config.EnsureProfileRegistered(profileName);
        config.ActiveProfile = profileName;
        await _configStore.SaveAsync(config);
    }

    private static List<string> OrderProfiles(AppConfig config, IEnumerable<string> availableProfiles)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var available = new HashSet<string>(availableProfiles, StringComparer.Ordinal);

        foreach (var profile in config.ProfileOrder)
        {
            if (available.Contains(profile) && seen.Add(profile))
            {
                ordered.Add(profile);
            }
        }

        foreach (var profile in available.OrderBy(profile => profile, StringComparer.Ordinal))
        {
            if (seen.Add(profile))
            {
                ordered.Add(profile);
            }
        }

        config.ProfileOrder = ordered.ToList();
        return ordered;
    }

    private static string ResolveInitialProfile(AppConfig config, IReadOnlyList<string> orderedProfiles)
    {
        if (!string.IsNullOrWhiteSpace(config.ActiveProfile) && orderedProfiles.Contains(config.ActiveProfile, StringComparer.Ordinal))
        {
            return config.ActiveProfile;
        }

        return orderedProfiles[0];
    }

    private static string? SelectNextProfile(
        IReadOnlyList<string> orderedProfiles,
        string currentProfile,
        HashSet<string> exhaustedProfiles)
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
}

internal sealed record SessionReference(string SessionId, string RolloutPath);
