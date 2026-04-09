namespace CodexMulti.Run;

internal sealed class ProcessRunResult
{
    public int ExitCode { get; init; }

    public string? SessionId { get; init; }

    public string? RolloutPath { get; init; }

    public bool RateLimitDetected { get; init; }

    public string? RateLimitMessage { get; init; }
}
