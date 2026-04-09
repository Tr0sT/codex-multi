using System.Text.Json;
using System.Text.RegularExpressions;
using CodexMulti.Auth;
using CodexMulti.Infrastructure;

namespace CodexMulti.Profiles;

internal sealed class ProfileStore
{
    private static readonly Regex ValidProfileName = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly AppPaths _paths;
    private readonly AuthInspector _authInspector;

    public ProfileStore(AppPaths paths, AuthInspector authInspector)
    {
        _paths = paths;
        _authInspector = authInspector;
    }

    public async Task<ProfileMetadata> SaveCurrentAuthAsync(string name)
    {
        var sourcePath = _paths.CodexAuthFilePath;
        if (!File.Exists(sourcePath))
        {
            throw new UserFacingException($"Current auth file '{sourcePath}' was not found.");
        }

        return await SaveFromSourceAsync(name, sourcePath);
    }

    public async Task<ProfileMetadata> ImportAsync(string name, string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new UserFacingException($"Auth file '{fullSourcePath}' was not found.");
        }

        return await SaveFromSourceAsync(name, fullSourcePath);
    }

    public async Task<IReadOnlyList<ProfileMetadata>> ListAsync()
    {
        if (!Directory.Exists(_paths.ProfilesDirectory))
        {
            return Array.Empty<ProfileMetadata>();
        }

        var profiles = new List<ProfileMetadata>();
        foreach (var directory in Directory.EnumerateDirectories(_paths.ProfilesDirectory))
        {
            var name = Path.GetFileName(directory);
            var metadata = await TryReadMetadataAsync(name);
            if (metadata is not null)
            {
                profiles.Add(metadata);
            }
        }

        return profiles;
    }

    public async Task<ProfileMetadata?> TryGetAsync(string name)
    {
        ValidateName(name);
        return await TryReadMetadataAsync(name);
    }

    public async Task<ProfileMetadata> GetRequiredAsync(string name)
    {
        var profile = await TryGetAsync(name);
        return profile ?? throw new UserFacingException($"Profile '{name}' does not exist.");
    }

    public Task RemoveAsync(string name)
    {
        ValidateName(name);

        var profileDirectory = _paths.GetProfileDirectory(name);
        if (!Directory.Exists(profileDirectory))
        {
            throw new UserFacingException($"Profile '{name}' does not exist.");
        }

        Directory.Delete(profileDirectory, recursive: true);
        return Task.CompletedTask;
    }

    private async Task<ProfileMetadata> SaveFromSourceAsync(string name, string sourcePath)
    {
        ValidateName(name);

        var authJson = await File.ReadAllTextAsync(sourcePath, TextEncodings.Utf8NoBom);
        var inspection = _authInspector.Inspect(authJson);

        var profileDirectory = _paths.GetProfileDirectory(name);
        Directory.CreateDirectory(profileDirectory);
        FilePermissions.TrySetOwnerOnlyDirectory(profileDirectory);

        var authFilePath = _paths.GetProfileAuthFilePath(name);
        await File.WriteAllTextAsync(authFilePath, authJson, TextEncodings.Utf8NoBom);
        FilePermissions.TrySetOwnerOnly(authFilePath);

        var metadata = new ProfileMetadata
        {
            Name = name,
            SavedAtUtc = DateTimeOffset.UtcNow,
            AuthMode = inspection.AuthMode,
            AccountId = inspection.AccountId,
            EmailHint = inspection.EmailHint,
        };

        var metaFilePath = _paths.GetProfileMetaFilePath(name);
        await using (var stream = new FileStream(metaFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions);
            await stream.FlushAsync();
        }

        FilePermissions.TrySetOwnerOnly(metaFilePath);
        return metadata;
    }

    private async Task<ProfileMetadata?> TryReadMetadataAsync(string name)
    {
        var metaFilePath = _paths.GetProfileMetaFilePath(name);
        if (File.Exists(metaFilePath))
        {
            await using var stream = File.OpenRead(metaFilePath);
            var metadata = await JsonSerializer.DeserializeAsync<ProfileMetadata>(stream, JsonOptions);
            if (metadata is not null)
            {
                metadata.Name = name;
                return metadata;
            }
        }

        var authFilePath = _paths.GetProfileAuthFilePath(name);
        if (!File.Exists(authFilePath))
        {
            return null;
        }

        var authJson = await File.ReadAllTextAsync(authFilePath, TextEncodings.Utf8NoBom);
        var inspection = _authInspector.Inspect(authJson);
        return new ProfileMetadata
        {
            Name = name,
            SavedAtUtc = File.GetLastWriteTimeUtc(authFilePath),
            AuthMode = inspection.AuthMode,
            AccountId = inspection.AccountId,
            EmailHint = inspection.EmailHint,
        };
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !ValidProfileName.IsMatch(name))
        {
            throw new UserFacingException("Profile name may contain only letters, digits, '.', '_' and '-'.");
        }
    }
}
