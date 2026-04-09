using System.ComponentModel;
using System.Diagnostics;
using CodexMulti.Infrastructure;
using CodexMulti.Sessions;

namespace CodexMulti.Run;

internal sealed class CodexProcessRunner
{
    private readonly AppLogger _logger;
    private readonly SessionLogWatcher _sessionLogWatcher;

    public CodexProcessRunner(AppLogger logger, SessionLogWatcher sessionLogWatcher)
    {
        _logger = logger;
        _sessionLogWatcher = sessionLogWatcher;
    }

    public async Task<ProcessRunResult> RunAsync(LaunchPlan launchPlan, SessionReference? sessionReference)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launchPlan.Executable,
            UseShellExecute = false,
            RedirectStandardError = false,
            RedirectStandardOutput = false,
            RedirectStandardInput = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        foreach (var argument in launchPlan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        try
        {
            _logger.Info($"Launching: {launchPlan.ToDisplayString()}");
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new UserFacingException($"Failed to start '{launchPlan.Executable}': {ex.Message}");
        }

        var observation = await _sessionLogWatcher.ObserveAsync(
            process,
            DateTimeOffset.UtcNow,
            Environment.CurrentDirectory,
            sessionReference);

        await process.WaitForExitAsync();

        return new ProcessRunResult
        {
            ExitCode = process.ExitCode,
            SessionId = observation.SessionId,
            RolloutPath = observation.RolloutPath,
            RateLimitDetected = observation.RateLimitDetected,
            RateLimitMessage = observation.RateLimitMessage,
        };
    }
}
