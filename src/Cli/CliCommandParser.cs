namespace CodexMulti.Cli;

internal static class CliCommandParser
{
    public static ICommand Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new RunCommand(Array.Empty<string>());
        }

        if (!string.Equals(args[0], "auth", StringComparison.Ordinal))
        {
            return new RunCommand(args);
        }

        if (args.Length < 2)
        {
            throw new UserFacingException("Usage: codex-multi auth <list|save|import|use|current|remove|show> ...");
        }

        return args[1] switch
        {
            "list" when args.Length == 2 => new AuthCommand(new AuthListCommand()),
            "current" when args.Length == 2 => new AuthCommand(new AuthCurrentCommand()),
            "save" when args.Length == 3 => new AuthCommand(new AuthSaveCommand(args[2])),
            "use" when args.Length == 3 => new AuthCommand(new AuthUseCommand(args[2])),
            "remove" when args.Length == 3 => new AuthCommand(new AuthRemoveCommand(args[2])),
            "show" when args.Length == 3 => new AuthCommand(new AuthShowCommand(args[2])),
            "import" => ParseImport(args),
            _ => throw new UserFacingException("Usage: codex-multi auth <list|save|import|use|current|remove|show> ..."),
        };
    }

    private static ICommand ParseImport(string[] args)
    {
        if (args.Length != 5 || !string.Equals(args[3], "--from", StringComparison.Ordinal))
        {
            throw new UserFacingException("Usage: codex-multi auth import <name> --from <path>");
        }

        return new AuthCommand(new AuthImportCommand(args[2], args[4]));
    }
}

internal interface ICommand;

internal sealed record RunCommand(string[] Args) : ICommand;

internal sealed record AuthCommand(IAuthSubcommand Subcommand) : ICommand;

internal interface IAuthSubcommand;

internal sealed record AuthListCommand : IAuthSubcommand;

internal sealed record AuthCurrentCommand : IAuthSubcommand;

internal sealed record AuthSaveCommand(string Name) : IAuthSubcommand;

internal sealed record AuthImportCommand(string Name, string SourcePath) : IAuthSubcommand;

internal sealed record AuthUseCommand(string Name) : IAuthSubcommand;

internal sealed record AuthShowCommand(string Name) : IAuthSubcommand;

internal sealed record AuthRemoveCommand(string Name) : IAuthSubcommand;
