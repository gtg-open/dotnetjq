// DOTNETJQ COMPATIBILITY PORT
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Oniguruma revision: 4ef89209a239c1aea328cf13c05a2807e5c146d1
// Source: vendor/oniguruma/src/regparse.c (prs_long, prs_callout_args,
// is_allowed_callout_name, and is_allowed_callout_tag_name).
// Strategy: PORT -- jq's pinned callout argument lexer and LONG parser.

using System.Text;

namespace DotNetJq.Compatibility.Regex;

internal static class OnigurumaCalloutArgumentParser
{
    private const int MaximumArgumentByteLength = 128;
    private const int MaximumArgumentCount = 4;

    internal static ParsedCalloutArguments Parse(string pattern, int openBrace)
    {
        var arguments = new List<ParsedCalloutArgument>();
        var value = new StringBuilder();
        var hadEscape = false;
        for (var index = openBrace + 1; index < pattern.Length;)
        {
            var current = pattern[index++];
            if (current == '\\')
            {
                hadEscape = true;
                if (index >= pattern.Length)
                {
                    throw new ArgumentException("invalid callout pattern");
                }

                var escaped = pattern[index++];
                if (escaped is not ('\\' or '}' or ','))
                {
                    value.Append('\\');
                }

                value.Append(escaped);
                ValidateByteLength(value);
                continue;
            }

            if (current is ',' or '}')
            {
                // prs_callout_args deliberately ignores empty comma fields.
                if (value.Length != 0)
                {
                    if (arguments.Count == MaximumArgumentCount)
                    {
                        throw new ArgumentException("invalid callout pattern");
                    }

                    arguments.Add(new ParsedCalloutArgument(value.ToString(), hadEscape));
                }

                value.Clear();
                hadEscape = false;
                if (current == '}')
                {
                    return new ParsedCalloutArguments(arguments.ToArray(), index - 1);
                }

                continue;
            }

            value.Append(current);
            ValidateByteLength(value);
        }

        throw new ArgumentException("invalid callout pattern");
    }

    internal static bool TryParseLong(string value, out long result)
    {
        result = 0;
        if (value.Length == 0)
        {
            return false;
        }

        var negative = false;
        var index = 0;
        if (value[0] is '+' or '-')
        {
            negative = value[0] == '-';
            index = 1;
        }

        long magnitude = 0;
        for (; index < value.Length; index++)
        {
            var current = value[index];
            if (!char.IsAsciiDigit(current))
            {
                return false;
            }

            var digit = current - '0';
            if (magnitude > (long.MaxValue - digit) / 10)
            {
                result = 0;
                return false;
            }

            magnitude = magnitude * 10 + digit;
        }

        result = negative ? -magnitude : magnitude;
        return true;
    }

    internal static bool IsAllowedName(string value) => IsAllowedIdentifier(value);

    internal static bool IsAllowedTag(string value) => IsAllowedIdentifier(value);

    internal static bool IsSingleCharacter(string value) =>
        value.EnumerateRunes().Take(2).Count() == 1;

    private static bool IsAllowedIdentifier(string value)
    {
        if (value.Length == 0 || char.IsAsciiDigit(value[0]))
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    private static void ValidateByteLength(StringBuilder value)
    {
        if (Encoding.UTF8.GetByteCount(value.ToString()) > MaximumArgumentByteLength)
        {
            throw new ArgumentException("invalid callout arg");
        }
    }

    internal readonly record struct ParsedCalloutArgument(string Value, bool HadEscape);

    internal readonly record struct ParsedCalloutArguments(
        ParsedCalloutArgument[] Arguments,
        int CloseBrace);
}
