using System.Text.Json.Serialization;

namespace CodexMulti.Configuration;

internal sealed class RuntimeState
{
    [JsonPropertyName("activeProfile")]
    public string? ActiveProfile { get; set; }

    [JsonPropertyName("generation")]
    public long Generation { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset? UpdatedAtUtc { get; set; }

    [JsonPropertyName("updatedByRunId")]
    public string? UpdatedByRunId { get; set; }

    public static RuntimeState CreateDefault()
    {
        return new RuntimeState();
    }
}
