using System.Text.Json.Serialization;

namespace CodexMulti.Configuration;

internal sealed class AppConfig
{
    public const string DefaultResumePrompt = "Continue from the last point. The previous attempt was interrupted by a rate limit.";

    [JsonPropertyName("activeProfile")]
    public string? ActiveProfile { get; set; }

    [JsonPropertyName("profileOrder")]
    public List<string> ProfileOrder { get; set; } = new();

    [JsonPropertyName("resumePrompt")]
    public string ResumePrompt { get; set; } = DefaultResumePrompt;

    [JsonPropertyName("codexExecutable")]
    public string CodexExecutable { get; set; } = "codex";

    public static AppConfig CreateDefault()
    {
        return new AppConfig();
    }

    public void EnsureProfileRegistered(string name)
    {
        if (!ProfileOrder.Contains(name, StringComparer.Ordinal))
        {
            ProfileOrder.Add(name);
        }
    }

    public void RemoveProfile(string name)
    {
        ProfileOrder.RemoveAll(item => string.Equals(item, name, StringComparison.Ordinal));
        if (string.Equals(ActiveProfile, name, StringComparison.Ordinal))
        {
            ActiveProfile = ProfileOrder.FirstOrDefault();
        }
    }

    public void Normalize()
    {
        ResumePrompt = string.IsNullOrWhiteSpace(ResumePrompt) ? DefaultResumePrompt : ResumePrompt;
        CodexExecutable = string.IsNullOrWhiteSpace(CodexExecutable) ? "codex" : CodexExecutable;

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in ProfileOrder.Where(profile => !string.IsNullOrWhiteSpace(profile)))
        {
            if (seen.Add(profile))
            {
                normalized.Add(profile);
            }
        }

        ProfileOrder = normalized;
        if (string.IsNullOrWhiteSpace(ActiveProfile))
        {
            ActiveProfile = null;
        }
    }
}
