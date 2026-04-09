using System.Text.Json.Serialization;

namespace CodexMulti.Profiles;

internal sealed class ProfileMetadata
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("savedAtUtc")]
    public DateTimeOffset SavedAtUtc { get; set; }

    [JsonPropertyName("authMode")]
    public string? AuthMode { get; set; }

    [JsonPropertyName("accountId")]
    public string? AccountId { get; set; }

    [JsonPropertyName("emailHint")]
    public string? EmailHint { get; set; }
}
