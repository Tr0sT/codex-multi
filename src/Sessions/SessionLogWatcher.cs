using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexMulti.Infrastructure;
using CodexMulti.Run;

namespace CodexMulti.Sessions;

internal sealed class SessionLogWatcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdleAfterRateLimit = TimeSpan.FromSeconds(3);

    private readonly string _sessionsDirectory;
    private readonly AppLogger _logger;

    public SessionLogWatcher(string sessionsDirectory, AppLogger logger)
    {
        _sessionsDirectory = sessionsDirectory;
        _logger = logger;
    }

    public async Task<SessionObservation> ObserveAsync(
        Process process,
        DateTimeOffset startedAtUtc,
        string currentWorkingDirectory,
        SessionReference? sessionReference)
    {
        var observation = new SessionObservation
        {
            LastProgressAtUtc = startedAtUtc,
        };

        JsonlSessionTail? tail = null;
        if (sessionReference is not null && File.Exists(sessionReference.RolloutPath))
        {
            tail = new JsonlSessionTail(sessionReference.RolloutPath, sessionReference.SessionId, skipExistingContent: true);
            observation.RolloutPath = sessionReference.RolloutPath;
            observation.SessionId = sessionReference.SessionId;
        }

        var terminationRequested = false;
        while (!process.HasExited)
        {
            tail ??= await TryFindTailAsync(startedAtUtc, currentWorkingDirectory, sessionReference?.RolloutPath);
            if (tail is not null)
            {
                ApplyDelta(observation, await tail.ReadNewEventsAsync());
            }

            if (observation.RateLimitDetected &&
                !terminationRequested &&
                DateTimeOffset.UtcNow - observation.LastProgressAtUtc >= IdleAfterRateLimit)
            {
                terminationRequested = true;
                _logger.Info("Rate limit detected and the child process stalled. Terminating child process.");
                TryTerminate(process);
            }

            await Task.Delay(PollInterval);
        }

        tail ??= await TryFindTailAsync(startedAtUtc, currentWorkingDirectory, sessionReference?.RolloutPath);
        if (tail is not null)
        {
            for (var i = 0; i < 3; i++)
            {
                ApplyDelta(observation, await tail.ReadNewEventsAsync());
                await Task.Delay(TimeSpan.FromMilliseconds(150));
            }
        }

        return observation;
    }

    private void ApplyDelta(SessionObservation observation, SessionLogDelta delta)
    {
        if (!string.IsNullOrWhiteSpace(delta.RolloutPath))
        {
            observation.RolloutPath ??= delta.RolloutPath;
        }

        if (!string.IsNullOrWhiteSpace(delta.SessionId))
        {
            observation.SessionId ??= delta.SessionId;
        }

        if (delta.HadNewLines)
        {
            observation.LastProgressAtUtc = delta.LastEventTimestampUtc ?? DateTimeOffset.UtcNow;
        }

        if (delta.RateLimitDetected)
        {
            observation.RateLimitDetected = true;
            observation.RateLimitMessage ??= delta.RateLimitMessage;
        }
    }

    private async Task<JsonlSessionTail?> TryFindTailAsync(
        DateTimeOffset startedAtUtc,
        string currentWorkingDirectory,
        string? preferredPath)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
        {
            return new JsonlSessionTail(preferredPath, null, skipExistingContent: true);
        }

        if (!Directory.Exists(_sessionsDirectory))
        {
            return null;
        }

        JsonlSessionCandidate? bestCandidate = null;
        string? bestPath = null;
        TimeSpan? bestDelta = null;

        foreach (var filePath in EnumerateCandidateFiles(startedAtUtc))
        {
            var candidate = await JsonlSessionCandidate.TryLoadAsync(filePath);
            if (candidate is null)
            {
                continue;
            }

            if (!string.Equals(candidate.Cwd, currentWorkingDirectory, StringComparison.Ordinal))
            {
                continue;
            }

            if (candidate.StartedAtUtc < startedAtUtc.AddSeconds(-5))
            {
                continue;
            }

            var delta = candidate.StartedAtUtc - startedAtUtc;
            var absoluteDelta = delta.Duration();

            if (bestDelta is not null && absoluteDelta >= bestDelta.Value)
            {
                continue;
            }

            bestCandidate = candidate;
            bestPath = filePath;
            bestDelta = absoluteDelta;
        }

        if (bestCandidate is not null && bestPath is not null)
        {
            _logger.Info($"Matched rollout log '{bestPath}'.");
            return new JsonlSessionTail(bestPath, bestCandidate.SessionId, skipExistingContent: false);
        }

        return null;
    }

    private IEnumerable<string> EnumerateCandidateFiles(DateTimeOffset startedAtUtc)
    {
        var localDates = new HashSet<DateOnly>
        {
            DateOnly.FromDateTime(startedAtUtc.LocalDateTime),
            DateOnly.FromDateTime(DateTime.Now),
        };

        foreach (var date in localDates.OrderByDescending(date => date))
        {
            var dayDirectory = Path.Combine(
                _sessionsDirectory,
                date.Year.ToString("0000"),
                date.Month.ToString("00"),
                date.Day.ToString("00"));

            if (!Directory.Exists(dayDirectory))
            {
                continue;
            }

            foreach (var filePath in Directory.EnumerateFiles(dayDirectory, "rollout-*.jsonl", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(path => path, StringComparer.Ordinal))
            {
                yield return filePath;
            }
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}

internal sealed class SessionObservation
{
    public string? SessionId { get; set; }

    public string? RolloutPath { get; set; }

    public bool RateLimitDetected { get; set; }

    public string? RateLimitMessage { get; set; }

    public DateTimeOffset LastProgressAtUtc { get; set; }
}

internal sealed class SessionLogDelta
{
    public string? SessionId { get; set; }

    public string? RolloutPath { get; set; }

    public bool HadNewLines { get; set; }

    public bool RateLimitDetected { get; set; }

    public string? RateLimitMessage { get; set; }

    public DateTimeOffset? LastEventTimestampUtc { get; set; }
}

internal sealed class JsonlSessionTail
{
    private readonly string _path;
    private readonly string? _initialSessionId;
    private bool _skipExistingContent;
    private long _position;

    public JsonlSessionTail(string path, string? initialSessionId, bool skipExistingContent)
    {
        _path = path;
        _initialSessionId = initialSessionId;
        _skipExistingContent = skipExistingContent;
    }

    public async Task<SessionLogDelta> ReadNewEventsAsync()
    {
        var delta = new SessionLogDelta
        {
            SessionId = _initialSessionId,
            RolloutPath = _path,
        };

        if (!File.Exists(_path))
        {
            return delta;
        }

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (_position > stream.Length)
        {
            _position = 0;
        }

        if (_skipExistingContent)
        {
            _position = stream.Length;
            _skipExistingContent = false;
        }

        stream.Seek(_position, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync() is { } line)
        {
            delta.HadNewLines = true;
            _position = reader.BaseStream.Position;
            TryApplyLine(delta, line);
        }

        _position = reader.BaseStream.Position;
        return delta;
    }

    private static void TryApplyLine(SessionLogDelta delta, string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var eventTimestamp = TryReadTimestamp(root);
            if (eventTimestamp is not null)
            {
                delta.LastEventTimestampUtc = eventTimestamp;
            }

            var type = TryReadString(root, "type");
            if (string.Equals(type, "session_meta", StringComparison.Ordinal) &&
                root.TryGetProperty("payload", out var metaPayload))
            {
                delta.SessionId ??= TryReadString(metaPayload, "id");
                return;
            }

            if (!string.Equals(type, "event_msg", StringComparison.Ordinal) ||
                !root.TryGetProperty("payload", out var payload))
            {
                return;
            }

            if (!string.Equals(TryReadString(payload, "type"), "error", StringComparison.Ordinal))
            {
                return;
            }

            var message = TryReadString(payload, "message");
            if (!string.IsNullOrWhiteSpace(message) &&
                message.Contains("Rate limit reached", StringComparison.Ordinal))
            {
                delta.RateLimitDetected = true;
                delta.RateLimitMessage ??= message;
            }
        }
        catch
        {
        }
    }

    private static DateTimeOffset? TryReadTimestamp(JsonElement root)
    {
        var value = TryReadString(root, "timestamp");
        return DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return null;
    }
}

internal sealed class JsonlSessionCandidate
{
    public required string SessionId { get; init; }

    public required string Cwd { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }

    public static async Task<JsonlSessionCandidate?> TryLoadAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        for (var i = 0; i < 8; i++)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!string.Equals(TryReadString(root, "type"), "session_meta", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!root.TryGetProperty("payload", out var payload))
                {
                    continue;
                }

                var sessionId = TryReadString(payload, "id");
                var cwd = TryReadString(payload, "cwd");
                var timestampValue = TryReadString(payload, "timestamp");
                if (string.IsNullOrWhiteSpace(sessionId) ||
                    string.IsNullOrWhiteSpace(cwd) ||
                    !DateTimeOffset.TryParse(timestampValue, out var startedAtUtc))
                {
                    return null;
                }

                return new JsonlSessionCandidate
                {
                    SessionId = sessionId,
                    Cwd = cwd,
                    StartedAtUtc = startedAtUtc,
                };
            }
            catch
            {
            }
        }

        return null;
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return null;
    }
}
