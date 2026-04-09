using CodexMulti.Auth;
using CodexMulti.Cli;
using CodexMulti.Configuration;
using CodexMulti.Infrastructure;
using CodexMulti.Profiles;
using CodexMulti.Run;

namespace CodexMulti;

internal sealed class Application
{
    public async Task<int> RunAsync(string[] args)
    {
        var paths = AppPaths.Create();
        paths.EnsureCreated();

        var logger = new AppLogger(paths.LogsDirectory);
        var configStore = new AppConfigStore(paths);
        var authInspector = new AuthInspector();
        var profileStore = new ProfileStore(paths, authInspector);
        var authSwitcher = new AuthJsonSwitcher(paths, authInspector);

        try
        {
            var command = CliCommandParser.Parse(args);
            return command switch
            {
                RunCommand runCommand => await HandleRunAsync(runCommand, configStore, profileStore, authSwitcher, logger),
                AuthCommand authCommand => await HandleAuthAsync(authCommand, configStore, profileStore, authSwitcher),
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
        ProfileStore profileStore,
        AuthJsonSwitcher authSwitcher,
        AppLogger logger)
    {
        using var instanceLock = InstanceLock.Acquire(configStore.Paths.InstanceLockFilePath);

        var runner = new CodexMultiRunner(configStore, profileStore, authSwitcher, logger);
        return await runner.RunAsync(command.Args);
    }

    private static async Task<int> HandleAuthAsync(
        AuthCommand command,
        AppConfigStore configStore,
        ProfileStore profileStore,
        AuthJsonSwitcher authSwitcher)
    {
        var config = await configStore.LoadAsync();

        switch (command.Subcommand)
        {
            case AuthListCommand:
                await PrintProfilesAsync(config, profileStore);
                return 0;

            case AuthCurrentCommand:
                await PrintCurrentProfileAsync(config, profileStore);
                return 0;

            case AuthShowCommand showCommand:
                await PrintProfileAsync(showCommand.Name, profileStore);
                return 0;

            case AuthSaveCommand saveCommand:
            {
                using var instanceLock = InstanceLock.Acquire(configStore.Paths.InstanceLockFilePath);
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
                using var instanceLock = InstanceLock.Acquire(configStore.Paths.InstanceLockFilePath);
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
                using var instanceLock = InstanceLock.Acquire(configStore.Paths.InstanceLockFilePath);
                var updatedConfig = await configStore.LoadAsync();
                var metadata = await profileStore.GetRequiredAsync(useCommand.Name);
                await authSwitcher.SwitchToProfileAsync(metadata.Name);
                updatedConfig.EnsureProfileRegistered(metadata.Name);
                updatedConfig.ActiveProfile = metadata.Name;
                await configStore.SaveAsync(updatedConfig);
                Console.WriteLine($"Active profile: {metadata.Name}");
                return 0;
            }

            case AuthRemoveCommand removeCommand:
            {
                using var instanceLock = InstanceLock.Acquire(configStore.Paths.InstanceLockFilePath);
                var updatedConfig = await configStore.LoadAsync();
                await profileStore.RemoveAsync(removeCommand.Name);
                updatedConfig.RemoveProfile(removeCommand.Name);
                await configStore.SaveAsync(updatedConfig);
                Console.WriteLine($"Removed profile '{removeCommand.Name}'.");
                return 0;
            }

            default:
                throw new UserFacingException("Unsupported auth command.");
        }
    }

    private static async Task PrintProfilesAsync(AppConfig config, ProfileStore profileStore)
    {
        var profiles = await profileStore.ListAsync();
        if (profiles.Count == 0)
        {
            Console.WriteLine("No profiles saved.");
            return;
        }

        foreach (var profile in OrderProfiles(config, profiles))
        {
            var activeMarker = string.Equals(profile.Name, config.ActiveProfile, StringComparison.Ordinal) ? "*" : " ";
            Console.WriteLine($"{activeMarker} {FormatProfileSummary(profile)}");
        }
    }

    private static async Task PrintCurrentProfileAsync(AppConfig config, ProfileStore profileStore)
    {
        var activeProfileName = config.ActiveProfile;
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

    private static string FormatProfileSummary(ProfileMetadata profile)
    {
        return $"{profile.Name} authMode={profile.AuthMode ?? "-"} accountId={profile.AccountId ?? "-"} emailHint={profile.EmailHint ?? "-"} savedAtUtc={profile.SavedAtUtc:O}";
    }
}
