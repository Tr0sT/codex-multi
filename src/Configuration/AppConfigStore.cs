using System.Text.Json;
using CodexMulti.Infrastructure;

namespace CodexMulti.Configuration;

internal sealed class AppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public AppConfigStore(AppPaths paths)
    {
        Paths = paths;
    }

    public AppPaths Paths { get; }

    public async Task<AppConfig> LoadAsync()
    {
        if (!File.Exists(Paths.ConfigFilePath))
        {
            var defaultConfig = AppConfig.CreateDefault();
            await SaveAsync(defaultConfig);
            return defaultConfig;
        }

        await using var stream = File.OpenRead(Paths.ConfigFilePath);
        var config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonOptions);
        if (config is null)
        {
            throw new UserFacingException($"Failed to parse config file '{Paths.ConfigFilePath}'.");
        }

        config.Normalize();
        return config;
    }

    public async Task SaveAsync(AppConfig config)
    {
        config.Normalize();

        Directory.CreateDirectory(Path.GetDirectoryName(Paths.ConfigFilePath)!);
        await using var stream = new FileStream(Paths.ConfigFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, config, JsonOptions);
        await stream.FlushAsync();
        FilePermissions.TrySetOwnerOnly(Paths.ConfigFilePath);
    }
}
