using System.Globalization;

namespace Aeris.Cli;

public class UsageException(string message) : Exception(message);

/// <summary>Parsed arguments: a command, positional values and --options. Unknown options are an error.</summary>
public class CommandLine
{
    public string Command { get; private init; } = "";
    public List<string> Positional { get; } = [];
    private readonly Dictionary<string, string?> options = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, (string[] Flags, string[] Values)> Commands = new()
    {
        ["scan"] = (["--json", "--quiet"], ["--top", "--min-size", "--type", "--csv"]),
        ["clean"] = (["--dry-run", "--yes", "--json", "--quiet", "--scheduled"], ["--categories", "--profile", "--older-than", "--mode"]),
        ["duplicates"] = (["--json", "--quiet", "--delete-to-recycle", "--yes", "--similar-images"], ["--min-size", "--threshold"]),
        ["empty-folders"] = (["--json", "--quiet", "--delete-to-recycle", "--yes", "--keep-system-files"], []),
        ["history"] = (["--json"], ["--last"]),
        ["restore"] = (["--yes", "--json"], []),
        ["categories"] = (["--json"], []),
        ["help"] = ([], []),
        ["version"] = ([], []),
    };

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] is "-h" or "--help" or "/?") return new() { Command = "help" };
        if (args[0] is "--version" or "-v") return new() { Command = "version" };
        string command = args[0].ToLowerInvariant();
        if (!Commands.TryGetValue(command, out var known)) throw new UsageException(Loc.Format("Cli.UnknownCommand", args[0]));
        var line = new CommandLine { Command = command };
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) { line.Positional.Add(arg); continue; }
            string name = arg, value = "";
            int equals = arg.IndexOf('=');
            if (equals > 0) { name = arg[..equals]; value = arg[(equals + 1)..]; }
            if (name is "--help") { return new() { Command = "help" }; }
            if (known.Flags.Contains(name))
            {
                if (equals > 0) throw new UsageException(Loc.Format("Cli.NoValueExpected", name));
                line.options[name] = null;
            }
            else if (known.Values.Contains(name))
            {
                if (equals < 0)
                {
                    if (i + 1 >= args.Count) throw new UsageException(Loc.Format("Cli.ValueExpected", name));
                    value = args[++i];
                }
                line.options[name] = value;
            }
            else throw new UsageException(Loc.Format("Cli.UnknownOption", name, command));
        }
        return line;
    }

    public bool Has(string name) => options.ContainsKey(name);

    public string? Value(string name) => options.TryGetValue(name, out var value) ? value : null;

    public int Int(string name, int fallback, int minimum, int maximum)
    {
        if (Value(name) is not { } text) return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < minimum || value > maximum)
            throw new UsageException(Loc.Format("Cli.BadNumber", name, minimum, maximum));
        return value;
    }

    /// <summary>Sizes like 500, 100KB, 100MB, 2GB or 1.5TB (binary units).</summary>
    public static long ParseSize(string text)
    {
        string trimmed = text.Trim().ToUpperInvariant();
        long multiplier = 1;
        foreach (var (suffix, factor) in new[] { ("TB", 1L << 40), ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("B", 1L) })
            if (trimmed.EndsWith(suffix, StringComparison.Ordinal)) { multiplier = factor; trimmed = trimmed[..^suffix.Length].Trim(); break; }
        if (!double.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) || value < 0 || value * multiplier > long.MaxValue)
            throw new UsageException(Loc.Format("Cli.BadSize", text));
        return (long)(value * multiplier);
    }

    /// <summary>Ages like 2d, 48h or 1w.</summary>
    public static TimeSpan ParseAge(string text)
    {
        string trimmed = text.Trim().ToLowerInvariant();
        if (trimmed.Length >= 2 && int.TryParse(trimmed[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int amount))
        {
            switch (trimmed[^1])
            {
                case 'h': return TimeSpan.FromHours(amount);
                case 'd': return TimeSpan.FromDays(amount);
                case 'w': return TimeSpan.FromDays(7 * amount);
            }
        }
        throw new UsageException(Loc.Format("Cli.BadAge", text));
    }
}
