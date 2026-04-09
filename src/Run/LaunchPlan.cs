namespace CodexMulti.Run;

internal sealed class LaunchPlan
{
    private LaunchPlan(string executable, IReadOnlyList<string> arguments)
    {
        Executable = executable;
        Arguments = arguments;
    }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public static LaunchPlan Raw(string executable, IReadOnlyList<string> arguments)
    {
        return new LaunchPlan(executable, arguments.ToArray());
    }

    public static LaunchPlan Resume(string executable, string sessionId, string prompt)
    {
        return new LaunchPlan(executable, new[] { "resume", sessionId, prompt });
    }

    public string ToDisplayString()
    {
        return string.Join(" ", new[] { Executable }.Concat(Arguments.Select(QuoteIfNeeded)));
    }

    private static string QuoteIfNeeded(string value)
    {
        return value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
    }
}
