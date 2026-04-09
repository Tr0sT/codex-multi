namespace CodexMulti.Infrastructure;

internal sealed class AppPaths
{
    private AppPaths(
        string configDirectory,
        string codexHomeDirectory)
    {
        ConfigDirectory = configDirectory;
        ProfilesDirectory = Path.Combine(ConfigDirectory, "profiles");
        LogsDirectory = Path.Combine(ConfigDirectory, "logs");
        LocksDirectory = Path.Combine(ConfigDirectory, "locks");

        ConfigFilePath = Path.Combine(ConfigDirectory, "config.json");
        InstanceLockFilePath = Path.Combine(LocksDirectory, "instance.lock");

        CodexHomeDirectory = codexHomeDirectory;
        CodexAuthFilePath = Path.Combine(CodexHomeDirectory, "auth.json");
        CodexSessionsDirectory = Path.Combine(CodexHomeDirectory, "sessions");
    }

    public string ConfigDirectory { get; }

    public string ProfilesDirectory { get; }

    public string LogsDirectory { get; }

    public string LocksDirectory { get; }

    public string ConfigFilePath { get; }

    public string InstanceLockFilePath { get; }

    public string CodexHomeDirectory { get; }

    public string CodexAuthFilePath { get; }

    public string CodexSessionsDirectory { get; }

    public string GetProfileDirectory(string name) => Path.Combine(ProfilesDirectory, name);

    public string GetProfileAuthFilePath(string name) => Path.Combine(GetProfileDirectory(name), "auth.json");

    public string GetProfileMetaFilePath(string name) => Path.Combine(GetProfileDirectory(name), "meta.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(LocksDirectory);

        FilePermissions.TrySetOwnerOnlyDirectory(ConfigDirectory);
        FilePermissions.TrySetOwnerOnlyDirectory(ProfilesDirectory);
        FilePermissions.TrySetOwnerOnlyDirectory(LogsDirectory);
        FilePermissions.TrySetOwnerOnlyDirectory(LocksDirectory);
    }

    public static AppPaths Create()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(homeDirectory, ".config");
        }

        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(homeDirectory, ".codex");
        }

        return new AppPaths(
            Path.Combine(configRoot, "codex-multi"),
            codexHome);
    }
}
