using CodexMulti.Infrastructure;

namespace CodexMulti.Auth;

internal sealed class AuthJsonSwitcher
{
    private readonly AppPaths _paths;
    private readonly AuthInspector _authInspector;

    public AuthJsonSwitcher(AppPaths paths, AuthInspector authInspector)
    {
        _paths = paths;
        _authInspector = authInspector;
    }

    public async Task SwitchToProfileAsync(string profileName)
    {
        var sourcePath = _paths.GetProfileAuthFilePath(profileName);
        if (!File.Exists(sourcePath))
        {
            throw new UserFacingException($"Profile '{profileName}' does not exist.");
        }

        var authJson = await File.ReadAllTextAsync(sourcePath, TextEncodings.Utf8NoBom);
        _authInspector.Inspect(authJson);

        Directory.CreateDirectory(_paths.CodexHomeDirectory);
        FilePermissions.TrySetOwnerOnlyDirectory(_paths.CodexHomeDirectory);

        var tempFilePath = Path.Combine(
            _paths.CodexHomeDirectory,
            $"auth.json.tmp-{Environment.ProcessId}-{Guid.NewGuid():N}");

        await File.WriteAllTextAsync(tempFilePath, authJson, TextEncodings.Utf8NoBom);
        FilePermissions.TrySetOwnerOnly(tempFilePath);

        try
        {
            File.Move(tempFilePath, _paths.CodexAuthFilePath, overwrite: true);
            var finalJson = await File.ReadAllTextAsync(_paths.CodexAuthFilePath, TextEncodings.Utf8NoBom);
            _authInspector.Inspect(finalJson);
            FilePermissions.TrySetOwnerOnly(_paths.CodexAuthFilePath);
        }
        catch
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }

            throw;
        }
    }
}
