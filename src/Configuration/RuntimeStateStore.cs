using System.Text.Json;
using CodexMulti.Infrastructure;

namespace CodexMulti.Configuration;

internal sealed class RuntimeStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public RuntimeStateStore(AppPaths paths)
    {
        Paths = paths;
    }

    public AppPaths Paths { get; }

    public async Task<RuntimeState> LoadAsync()
    {
        if (!File.Exists(Paths.RuntimeStateFilePath))
        {
            return RuntimeState.CreateDefault();
        }

        await using var stream = File.OpenRead(Paths.RuntimeStateFilePath);
        var state = await JsonSerializer.DeserializeAsync<RuntimeState>(stream, JsonOptions);
        return state ?? RuntimeState.CreateDefault();
    }

    public async Task SaveAsync(RuntimeState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.RuntimeStateFilePath)!);
        await using var stream = new FileStream(Paths.RuntimeStateFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions);
        await stream.FlushAsync();
        FilePermissions.TrySetOwnerOnly(Paths.RuntimeStateFilePath);
    }
}
