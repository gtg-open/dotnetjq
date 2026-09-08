// DOTNETJQ COMPATIBILITY PORT
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Semantic source: vendor/oniguruma/src/regparse.c (prs_callout_of_contents)
// UPSTREAM COMPONENT: `(?{...})`/`(?{{...}})` content-callout grammar.
// STRATEGY: PORT the pinned delimiter, tag, direction, and diagnostic rules.
// BEHAVIORAL CONTRACT: jq installs no progress or retraction content callback, so a
// successfully parsed content callout is a zero-width success; its optional tag still
// participates in callout tag identity and resolves to the initial numeric value zero.

namespace DotNetJq.Compatibility.Regex;

internal static class OnigurumaContentCallout
{
    internal readonly record struct ParseResult(
        int End,
        string? Tag,
        char Direction);

    internal static bool TryParse(
        string pattern,
        int start,
        out ParseResult result)
    {
        result = default;
        if (!pattern.AsSpan(start).StartsWith("(?{", StringComparison.Ordinal))
        {
            return false;
        }

        var cursor = start + 3;
        if (cursor >= pattern.Length)
        {
            InvalidPattern();
        }

        var braceNesting = 0;
        while (cursor < pattern.Length && pattern[cursor] == '{')
        {
            braceNesting++;
            cursor++;
            if (cursor >= pattern.Length)
            {
                InvalidPattern();
            }
        }

        while (true)
        {
            if (cursor >= pattern.Length)
            {
                InvalidPattern();
            }

            var current = pattern[cursor++];
            if (current != '}')
            {
                continue;
            }

            var remaining = braceNesting;
            while (remaining > 0)
            {
                if (cursor >= pattern.Length)
                {
                    InvalidPattern();
                }

                current = pattern[cursor++];
                if (current == '}')
                {
                    remaining--;
                }
                else
                {
                    break;
                }
            }

            if (remaining == 0)
            {
                break;
            }
        }

        if (cursor >= pattern.Length)
        {
            EndPatternInGroup();
        }

        var terminator = pattern[cursor++];
        string? tag = null;
        if (terminator == '[')
        {
            var tagStart = cursor;
            var tagEnd = pattern.IndexOf(']', cursor);
            if (tagEnd < 0)
            {
                // Oniguruma distinguishes a wholly empty/truncated tag opener from a
                // non-empty unterminated tag. The latter has already entered tag-name
                // validation and reports INVALID_CALLOUT_TAG_NAME.
                if (tagStart == pattern.Length)
                {
                    EndPatternInGroup();
                }

                throw new JqRuntimeException("Regex failure: invalid callout tag name");
            }

            tag = pattern[tagStart..tagEnd];
            if (!OnigurumaCalloutArgumentParser.IsAllowedTag(tag))
            {
                throw new JqRuntimeException("Regex failure: invalid callout tag name");
            }

            cursor = tagEnd + 1;
            if (cursor >= pattern.Length)
            {
                EndPatternInGroup();
            }

            terminator = pattern[cursor++];
        }

        var direction = '>';
        if (terminator is 'X' or '<' or '>')
        {
            direction = terminator;
            if (cursor >= pattern.Length)
            {
                EndPatternInGroup();
            }

            terminator = pattern[cursor++];
        }

        if (terminator != ')')
        {
            InvalidPattern();
        }

        result = new ParseResult(cursor - 1, tag, direction);
        return true;
    }

    private static void InvalidPattern() =>
        throw new JqRuntimeException("Regex failure: invalid callout pattern");

    private static void EndPatternInGroup() =>
        throw new JqRuntimeException("Regex failure: end pattern in group");
}
