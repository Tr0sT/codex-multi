using CodexMulti.Auth;
using CodexMulti.Cli;
using CodexMulti.Configuration;
using CodexMulti.Infrastructure;
using CodexMulti.Profiles;
using CodexMulti.Run;

namespace CodexMulti;

internal sealed class Application
{
    private static readonly TimeSpan StateLockTimeout = TimeSpan.FromSeconds(30);

    public async Task<int> RunAsync(string[] args)
    {
        var paths = AppPaths.Create();
        paths.EnsureCreated();

        var logger = new AppLogger(paths.LogsDirectory);
        var configStore = new AppConfigStore(paths);
        var runtimeStateStore = new RuntimeStateStore(paths);
        var authInspector = new AuthInspector();
        var profileStore = new ProfileStore(paths, authInspector);
        var authSwitcher = new AuthJsonSwitcher(paths, authInspector);

        try
        {
            var command = CliCommandParser.Parse(args);
            return command switch
            {
                RunCommand runCommand => await HandleRunAsync(runCommand, configStore, runtimeStateStore, profileStore, authSwitcher, logger),
                AuthCommand authCommand => await HandleAuthAsync(authCommand, configStore, runtimeStateStore, profileStore, authSwitcher),
                _ => throw new UserFacingException("Unsupported command."),
            };
        }
        catch (UserFacingException ex)
        {
            logger.Error(ex.Message);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            logger.Error(ex.ToString());
            Console.Error.WriteLine("Unexpected error. See the codex-multi log for details.");
            return 1;
        }
    }

    private static async Task<int> HandleRunAsync(
        RunCommand command,
        AppConfigStore configStore,
        RuntimeStateStore runtimeStateStore,
        ProfileStore profileStore,
        AuthJsonSwitcher authSwitcher,
        AppLogger logger)
    {
        var runner = new CodexMultiRunner(configStore, runtimeStateStore, profileStore, authSwitcher, logger);
        return await runner.RunAsync(command.Args);
    }

    private static async Task<int> HandleAuthAsync(
        AuthCommand command,
        AppConfigStore configStore,
        RuntimeStateStore runtimeStateStore,
        ProfileStore profileStore,
        AuthJsonSwitcher authSwitcher)
    {
        var config = await configStore.LoadAsync();
        var runtimeState = await runtimeStateStore.LoadAsync();

        switch (command.Subcommand)
        {
            case AuthListCommand:
                await PrintProfilesAsync(config, runtimeState, profileStore);
                return 0;

            case AuthCurrentCommand:
                await PrintCurrentProfileAsync(config, runtimeState, profileStore);
                return 0;

            case AuthShowCommand showCommand:
                await PrintProfileAsync(showCommand.Name, profileStore);
                return 0;

            case AuthSaveCommand saveCommand:
            {
                using var stateLock = AcquireStateLock(configStore.Paths);
                var updatedConfig = await configStore.LoadAsync();
                var metadata = await profileStore.SaveCurrentAuthAsync(saveCommand.Name);
                updatedConfig.EnsureProfileRegistered(metadata.Name);
                updatedConfig.ActiveProfile ??= metadata.Name;
                await configStore.SaveAsync(updatedConfig);
                Console.WriteLine($"Saved profile '{metadata.Name}'.");
                return 0;
            }

            case AuthImportCommand importCommand:
            {
                using var stateLock = AcquireStateLock(configStore.Paths);
                var updatedConfig = await configStore.LoadAsync();
                var metadata = await profileStore.ImportAsync(importCommand.Name, importCommand.SourcePath);
                updatedConfig.EnsureProfileRegistered(metadata.Name);
                updatedConfig.ActiveProfile ??= metadata.Name;
                await configStore.SaveAsync(updatedConfig);
                Console.WriteLine($"Imported profile '{metadata.Name}'.");
                return 0;
            }

            case AuthUseCommand useCommand:
            {
                using var stateLock = AcquireStateLock(configStore.Paths);
                var updatedConfig = await configStore.LoadAsync();
                var updatedRuntimeState = await runtimeStateStore.LoadAsync();
                var metadata = await profileStore.GetRequiredAsync(useCommand.Name);
                var matchesProfile = await profileStore.SharedAuthMatchesProfileAsync(metadata.Name);
                if (matchesProfile)
                {
                    await profileStore.SyncSharedAuthToProfileIfMatchesAsync(metadata.Name);
                }
                else
                {
                    await authSwitcher.SwitchToProfileAsync(metadata.Name);
                }

                if (!string.Equals(updatedRuntimeState.ActiveProfile, metadata.Name, StringComparison.Ordinal))
                {
                    updatedRuntimeState.Generation++;
                    updatedRuntimeState.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    updatedRuntimeState.UpdatedByRunId = "auth-use";
                }

                updatedRuntimeState.ActiveProfile = metadata.Name;
                updatedConfig.EnsureProfileRegistered(metadata.Name);
                updatedConfig.ActiveProfile = metadata.Name;
                await configStore.SaveAsync(updatedConfig);
                await runtimeStateStore.SaveAsync(updatedRuntimeState);
                Console.WriteLine($"Active profile: {metadata.Name}");
                return 0;
            }

            case AuthRemoveCommand removeCommand:
            {
                using var stateLock = AcquireStateLock(configStore.Paths);
                var updatedConfig = await configStore.LoadAsync();
                var updatedRuntimeState = await runtimeStateStore.LoadAsync();
                var removedWasActive = string.Equals(updatedRuntimeState.ActiveProfile, removeCommand.Name, StringComparison.Ordinal);
                await profileStore.RemoveAsync(removeCommand.Name);
                updatedConfig.RemoveProfile(removeCommand.Name);

                if (removedWasActive)
                {
                    var remainingProfiles = (await profileStore.ListAsync())
                        .Select(profile => profile.Name)
                        .ToArray();

                    var orderedProfiles = OrderProfileNames(updatedConfig, remainingProfiles);
                    var nextProfile = orderedProfiles.FirstOrDefault();

                    updatedRuntimeState.Generation++;
                    updatedRuntimeState.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    updatedRuntimeState.UpdatedByRunId = "auth-remove";
                    updatedRuntimeState.ActiveProfile = nextProfile;

                    if (!string.IsNullOrWhiteSpace(nextProfile))
                    {
                        if (await profileStore.SharedAuthMatchesProfileAsync(nextProfile))
                        {
                            await profileStore.SyncSharedAuthToProfileIfMatchesAsync(nextProfile);
                        }
                        else
                        {
                            await authSwitcher.SwitchToProfileAsync(nextProfile);
                        }

                        updatedConfig.ActiveProfile = nextProfile;
                    }
                }

                await configStore.SaveAsync(updatedConfig);
                await runtimeStateStore.SaveAsync(updatedRuntimeState);
                Console.WriteLine($"Removed profile '{removeCommand.Name}'.");
                return 0;
            }

            default:
                throw new UserFacingException("Unsupported auth command.");
        }
    }

    private static async Task PrintProfilesAsync(AppConfig config, RuntimeState runtimeState, ProfileStore profileStore)
    {
        var profiles = await profileStore.ListAsync();
        if (profiles.Count == 0)
        {
            Console.WriteLine("No profiles saved.");
            return;
        }

        var displayedActiveProfile = runtimeState.ActiveProfile ?? config.ActiveProfile;
        foreach (var profile in OrderProfiles(config, profiles))
        {
            var activeMarker = string.Equals(profile.Name, displayedActiveProfile, StringComparison.Ordinal) ? "*" : " ";
            Console.WriteLine($"{activeMarker} {FormatProfileSummary(profile)}");
        }
    }

    private static async Task PrintCurrentProfileAsync(AppConfig config, RuntimeState runtimeState, ProfileStore profileStore)
    {
        var activeProfileName = runtimeState.ActiveProfile ?? config.ActiveProfile;
        if (string.IsNullOrWhiteSpace(activeProfileName))
        {
            Console.WriteLine("No active profile configured.");
            return;
        }

        var profile = await profileStore.TryGetAsync(activeProfileName);
        if (profile is null)
        {
            Console.WriteLine($"Configured active profile '{activeProfileName}' is missing.");
            return;
        }

        Console.WriteLine(FormatProfileSummary(profile));
    }

    private static async Task PrintProfileAsync(string name, ProfileStore profileStore)
    {
        var profile = await profileStore.GetRequiredAsync(name);
        Console.WriteLine($"name: {profile.Name}");
        Console.WriteLine($"savedAtUtc: {profile.SavedAtUtc:O}");
        Console.WriteLine($"authMode: {profile.AuthMode ?? "-"}");
        Console.WriteLine($"accountId: {profile.AccountId ?? "-"}");
        Console.WriteLine($"emailHint: {profile.EmailHint ?? "-"}");
    }

    private static IReadOnlyList<ProfileMetadata> OrderProfiles(AppConfig config, IReadOnlyList<ProfileMetadata> profiles)
    {
        var byName = profiles.ToDictionary(profile => profile.Name, StringComparer.Ordinal);
        var ordered = new List<ProfileMetadata>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in config.ProfileOrder)
        {
            if (byName.TryGetValue(name, out var profile) && seen.Add(name))
            {
                ordered.Add(profile);
            }
        }

        foreach (var profile in profiles.OrderBy(profile => profile.Name, StringComparer.Ordinal))
        {
            if (seen.Add(profile.Name))
            {
                ordered.Add(profile);
            }
        }

        return ordered;
    }

    private static IReadOnlyList<string> OrderProfileNames(AppConfig config, IReadOnlyList<string> profiles)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in config.ProfileOrder)
        {
            if (profiles.Contains(name, StringComparer.Ordinal) && seen.Add(name))
            {
                ordered.Add(name);
            }
        }

        foreach (var name in profiles.OrderBy(profile => profile, StringComparer.Ordinal))
        {
            if (seen.Add(name))
            {
                ordered.Add(name);
            }
        }

        return ordered;
    }

    private static string FormatProfileSummary(ProfileMetadata profile)
    {
        return $"{profile.Name} authMode={profile.AuthMode ?? "-"} accountId={profile.AccountId ?? "-"} emailHint={profile.EmailHint ?? "-"} savedAtUtc={profile.SavedAtUtc:O}";
    }

    private static InstanceLock AcquireStateLock(AppPaths paths)
    {
        return InstanceLock.Acquire(
            paths.StateLockFilePath,
            "Another codex-multi instance is updating shared auth state. Please retry.",
            StateLockTimeout);
    }
}
