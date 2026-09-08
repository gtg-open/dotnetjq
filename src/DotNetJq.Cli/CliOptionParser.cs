using System.Globalization;
using DotNetJq.Port;

namespace DotNetJq.Cli;

internal static class CliOptionParser
{
    internal static CliParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var options = new CliOptions();
        var furtherArguments = CliArgumentKind.String;
        var collectArguments = false;
        var argumentsDone = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argumentsDone || !IsOptionLike(argument))
            {
                if (options.Program is null)
                {
                    options.Program = argument;
                }
                else if (collectArguments)
                {
                    if (furtherArguments == CliArgumentKind.Json && !IsValidJson(argument))
                    {
                        return CliParseResult.Failure("jq: invalid JSON text passed to --jsonargs");
                    }

                    options.PositionalArguments.Add(new CliPositionalArgument(argument, furtherArguments));
                }
                else
                {
                    options.Files.Add(argument);
                }

                continue;
            }

            if (argument == "--")
            {
                argumentsDone = true;
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                var longOption = argument[2..];
                var error = ParseLong(
                    longOption,
                    arguments,
                    ref index,
                    options,
                    ref collectArguments,
                    ref furtherArguments);
                if (error is not null)
                {
                    return CliParseResult.Failure(error);
                }

                if (options.ImmediateAction != CliImmediateAction.None)
                {
                    return CliParseResult.Success(options);
                }

                continue;
            }

            var shortOptions = argument.AsSpan(1);
            for (var shortIndex = 0; shortIndex < shortOptions.Length; shortIndex++)
            {
                var option = shortOptions[shortIndex];
                switch (option)
                {
                    case 's': options.Slurp = true; break;
                    case 'r': options.RawOutput = true; break;
                    case 'j': options.RawOutput = true; options.JoinOutput = true; break;
                    case 'c': options.CompactOutput = true; break;
                    case 'C': options.ForceColor = true; break;
                    case 'M': options.ForceMonochrome = true; break;
                    case 'a': options.AsciiOutput = true; break;
                    case 'S': options.SortKeys = true; break;
                    case 'R': options.RawInput = true; break;
                    case 'n': options.NullInput = true; break;
                    case 'f': options.FromFile = true; break;
                    case 'e': options.ExitStatus = true; break;
                    case 'b': options.Binary = true; break;
                    case 'h': options.ImmediateAction = CliImmediateAction.Help; return CliParseResult.Success(options);
                    case 'V': options.ImmediateAction = CliImmediateAction.Version; return CliParseResult.Success(options);
                    case 'L':
                    {
                        string path;
                        if (shortIndex + 1 < shortOptions.Length)
                        {
                            path = shortOptions[(shortIndex + 1)..].ToString();
                        }
                        else if (!TryTake(arguments, ref index, out path))
                        {
                            return CliParseResult.Failure(
                                "-L takes a parameter: (e.g. -L /search/path or -L/search/path)");
                        }

                        options.LibraryPaths.Add(path);
                        shortIndex = shortOptions.Length;
                        break;
                    }
                    default:
                        return CliParseResult.Failure($"jq: Unknown option -{option}");
                }
            }
        }

        return CliParseResult.Success(options);
    }

    private static string? ParseLong(
        string option,
        IReadOnlyList<string> arguments,
        ref int index,
        CliOptions options,
        ref bool collectArguments,
        ref CliArgumentKind furtherArguments)
    {
        switch (option)
        {
            case "slurp": options.Slurp = true; return null;
            case "raw-output": options.RawOutput = true; return null;
            case "raw-output0":
                options.RawOutput = true;
                options.RawOutputZero = true;
                options.JoinOutput = true;
                return null;
            case "join-output": options.RawOutput = true; options.JoinOutput = true; return null;
            case "compact-output": options.CompactOutput = true; return null;
            case "color-output": options.ForceColor = true; return null;
            case "monochrome-output": options.ForceMonochrome = true; return null;
            case "ascii-output": options.AsciiOutput = true; return null;
            case "unbuffered": options.Unbuffered = true; return null;
            case "sort-keys": options.SortKeys = true; return null;
            case "raw-input": options.RawInput = true; return null;
            case "null-input": options.NullInput = true; return null;
            case "from-file": options.FromFile = true; return null;
            case "binary": options.Binary = true; return null;
            case "tab":
                options.TabIndent = true;
                options.CompactOutput = false;
                return null;
            case "seq": options.Sequence = true; return null;
            case "stream": options.Stream = true; return null;
            case "stream-errors":
                options.Stream = true;
                options.StreamErrors = true;
                return null;
            case "exit-status": options.ExitStatus = true; return null;
            case "args":
                collectArguments = true;
                furtherArguments = CliArgumentKind.String;
                return null;
            case "jsonargs":
                collectArguments = true;
                furtherArguments = CliArgumentKind.Json;
                return null;
            case "debug-dump-disasm": options.DebugDumpDisassembly = true; return null;
            case "debug-trace": options.JqFlags |= libjq.JQ_DEBUG_TRACE; return null;
            case "debug-trace=all": options.JqFlags |= libjq.JQ_DEBUG_TRACE_ALL; return null;
            case "help": options.ImmediateAction = CliImmediateAction.Help; return null;
            case "version": options.ImmediateAction = CliImmediateAction.Version; return null;
            case "build-configuration":
                options.ImmediateAction = CliImmediateAction.BuildConfiguration;
                return null;
            case "indent":
            {
                if (!TryTake(arguments, ref index, out var text))
                {
                    return "jq: --indent takes one parameter";
                }

                if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var indent) ||
                    indent is < -1 or > 7)
                {
                    return "jq: --indent takes a number between -1 and 7";
                }

                options.TabIndent = indent == -1;
                options.Indent = indent == -1 ? 0 : indent;
                options.CompactOutput = false;
                return null;
            }
            case "library-path":
                if (!TryTake(arguments, ref index, out var path))
                {
                    return "-L takes a parameter: (e.g. -L /search/path or -L/search/path)";
                }

                options.LibraryPaths.Add(path);
                return null;
            case "arg":
                return TakeNamed(arguments, ref index, options, CliArgumentKind.String);
            case "argjson":
                return TakeNamed(arguments, ref index, options, CliArgumentKind.Json);
            case "rawfile":
                return TakeNamed(arguments, ref index, options, CliArgumentKind.RawFile);
            case "slurpfile":
                return TakeNamed(arguments, ref index, options, CliArgumentKind.SlurpFile);
            case "run-tests":
                options.ImmediateAction = CliImmediateAction.RunTests;
                while (++index < arguments.Count)
                {
                    options.TestArguments.Add(arguments[index]);
                }

                return null;
            default:
                return $"jq: Unknown option --{option}";
        }
    }

    private static string? TakeNamed(
        IReadOnlyList<string> arguments,
        ref int index,
        CliOptions options,
        CliArgumentKind kind)
    {
        if (index + 2 >= arguments.Count)
        {
            var name = kind switch
            {
                CliArgumentKind.String => "arg",
                CliArgumentKind.Json => "argjson",
                CliArgumentKind.RawFile => "rawfile",
                CliArgumentKind.SlurpFile => "slurpfile",
                _ => throw new InvalidOperationException(),
            };
            var example = kind switch
            {
                CliArgumentKind.String => "varname value",
                CliArgumentKind.Json => "varname text",
                CliArgumentKind.RawFile => "varname filename",
                CliArgumentKind.SlurpFile => "varname filename",
                _ => throw new InvalidOperationException(),
            };
            return $"jq: --{name} takes two parameters (e.g. --{name} {example})";
        }

        var argumentName = arguments[++index];
        var value = arguments[++index];
        var duplicate = options.NamedArguments.Any(argument =>
            string.Equals(argument.Name, argumentName, StringComparison.Ordinal));
        if (!duplicate && kind == CliArgumentKind.Json && !IsValidJson(value))
        {
            return "jq: invalid JSON text passed to --argjson";
        }

        if (!duplicate)
        {
            options.NamedArguments.Add(new CliNamedArgument(argumentName, value, kind));
        }

        return null;
    }

    private static bool IsOptionLike(string text) =>
        text.Length >= 2 && text[0] == '-' &&
        (text[1] == '-' || IsAsciiLetter(text[1]));

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsValidJson(string text)
    {
        var value = libjq.jv_parse(text);
        try
        {
            return value.IsValid;
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    private static bool TryTake(
        IReadOnlyList<string> arguments,
        ref int index,
        out string value)
    {
        if (index + 1 >= arguments.Count)
        {
            value = string.Empty;
            return false;
        }

        value = arguments[++index];
        return true;
    }
}
