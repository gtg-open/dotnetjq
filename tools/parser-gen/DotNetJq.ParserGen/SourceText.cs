using System.Text;
using System.Text.RegularExpressions;

namespace DotNetJq.ParserGen;

internal static class SourceText
{
    public static IReadOnlyList<string> SplitSections(string source)
    {
        var sections = new List<string>();
        var start = 0;
        foreach (Match match in Regex.Matches(source, @"(?m)^\s*%%\s*$"))
        {
            sections.Add(source[start..match.Index]);
            start = match.Index + match.Length;
        }
        sections.Add(source[start..]);
        return sections;
    }

    public static string MaskCommentsAndCodeBlocks(string source)
    {
        var result = source.ToCharArray();
        var index = 0;
        while (index < source.Length)
        {
            if (source[index] is '\'' or '"')
            {
                index = CopyQuoted(source, result, null, index);
            }
            else if (StartsWith(source, index, "/*"))
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                    throw new GuardrailException("unterminated block comment");
                Blank(result, source, index, end + 2);
                index = end + 2;
            }
            else if (StartsWith(source, index, "//"))
            {
                var end = source.IndexOf('\n', index + 2);
                if (end < 0)
                    end = source.Length;
                Blank(result, source, index, end);
                index = end;
            }
            else if (StartsWith(source, index, "%{"))
            {
                var end = source.IndexOf("%}", index + 2, StringComparison.Ordinal);
                if (end < 0)
                    throw new GuardrailException("unterminated %{ code block");
                Blank(result, source, index, end + 2);
                index = end + 2;
            }
            else if (StartsWith(source, index, "%code"))
            {
                var brace = source.IndexOf('{', index + 5);
                if (brace < 0)
                    throw new GuardrailException("%code directive has no body");
                var end = SkipBalancedBrace(source, brace);
                Blank(result, source, index, end);
                index = end;
            }
            else
            {
                index++;
            }
        }

        return new string(result);
    }

    public static string MaskCommentsAndActions(
        string source,
        string? preservedComment = null,
        string? commentReplacement = null,
        string? actionReplacement = null)
    {
        var result = new StringBuilder(source.Length);
        var index = 0;
        while (index < source.Length)
        {
            if (source[index] is '\'' or '"')
            {
                index = CopyQuoted(source, null, result, index);
            }
            else if (StartsWith(source, index, "/*"))
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                    throw new GuardrailException("unterminated block comment in grammar rules");
                var body = source[(index + 2)..end].Trim();
                if (preservedComment is not null && body == preservedComment)
                    result.Append(' ').Append(commentReplacement).Append(' ');
                else
                    PreserveNewlines(result, source, index, end + 2);
                index = end + 2;
            }
            else if (StartsWith(source, index, "//"))
            {
                var end = source.IndexOf('\n', index + 2);
                if (end < 0)
                    end = source.Length;
                PreserveNewlines(result, source, index, end);
                index = end;
            }
            else if (source[index] == '{')
            {
                var end = SkipBalancedBrace(source, index);
                result.Append(' ').Append(actionReplacement ?? string.Empty).Append(' ');
                PreserveNewlines(result, source, index, end);
                index = end;
            }
            else
            {
                result.Append(source[index++]);
            }
        }

        return result.ToString();
    }

    public static IReadOnlyList<string> TokenizeGrammar(string source)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < source.Length)
        {
            if (char.IsWhiteSpace(source[index]))
            {
                index++;
                continue;
            }

            if (source[index] is ':' or ';' or '|')
            {
                tokens.Add(source[index++].ToString());
                continue;
            }

            if (source[index] is '\'' or '"')
            {
                var start = index;
                var quote = source[index++];
                while (index < source.Length)
                {
                    if (source[index] == '\\')
                    {
                        index += Math.Min(2, source.Length - index);
                    }
                    else if (source[index++] == quote)
                    {
                        break;
                    }
                }
                tokens.Add(source[start..index]);
                continue;
            }

            if (source[index] == '<')
            {
                var end = source.IndexOf('>', index + 1);
                if (end < 0)
                    throw new GuardrailException("unterminated grammar type tag");
                tokens.Add(source[index..(end + 1)]);
                index = end + 1;
                continue;
            }

            var wordStart = index;
            while (index < source.Length &&
                   !char.IsWhiteSpace(source[index]) &&
                   source[index] is not (':' or ';' or '|'))
            {
                index++;
            }
            tokens.Add(source[wordStart..index]);
        }
        return tokens;
    }

    public static bool IsQuoted(string token) =>
        token.Length >= 2 && token[0] is '\'' or '"';

    public static void SkipTrivia(string source, ref int index)
    {
        while (index < source.Length)
        {
            if (char.IsWhiteSpace(source[index]))
            {
                index++;
            }
            else if (StartsWith(source, index, "/*"))
            {
                var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                    throw new GuardrailException("unterminated lexer comment");
                index = end + 2;
            }
            else if (StartsWith(source, index, "//"))
            {
                var end = source.IndexOf('\n', index + 2);
                index = end < 0 ? source.Length : end + 1;
            }
            else
            {
                return;
            }
        }
    }

    public static int FindLexerActionBrace(string source, int start)
    {
        var quote = '\0';
        var inClass = false;
        var escaped = false;
        for (var index = start; index < source.Length; index++)
        {
            var current = source[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (current == '\\')
            {
                escaped = true;
                continue;
            }
            if (quote != '\0')
            {
                if (current == quote)
                    quote = '\0';
                continue;
            }
            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }
            if (current == '[')
            {
                inClass = true;
                continue;
            }
            if (current == ']' && inClass)
            {
                inClass = false;
                continue;
            }
            if (!inClass && current == '{' &&
                (index == start || char.IsWhiteSpace(source[index - 1]) || source[index - 1] == '>'))
            {
                return index;
            }
            if (current == '\n' && source[start..index].TrimStart().StartsWith('%'))
                return -1;
        }
        return -1;
    }

    public static int SkipBalancedBrace(string source, int openingBrace)
    {
        var depth = 0;
        var quote = '\0';
        var escaped = false;
        var lineComment = false;
        var blockComment = false;
        for (var index = openingBrace; index < source.Length; index++)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (lineComment)
            {
                if (current == '\n')
                    lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (current == '*' && next == '/')
                {
                    blockComment = false;
                    index++;
                }
                continue;
            }
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (quote != '\0')
            {
                if (current == '\\')
                    escaped = true;
                else if (current == quote)
                    quote = '\0';
                continue;
            }
            if (current == '/' && next == '/')
            {
                lineComment = true;
                index++;
                continue;
            }
            if (current == '/' && next == '*')
            {
                blockComment = true;
                index++;
                continue;
            }
            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }
            if (current == '{')
                depth++;
            else if (current == '}' && --depth == 0)
                return index + 1;
        }
        throw new GuardrailException("unterminated semantic action");
    }

    public static string NormalizePattern(string pattern)
    {
        var result = new StringBuilder(pattern.Length);
        var quote = '\0';
        var inClass = false;
        var escaped = false;
        var pendingSpace = false;
        foreach (var current in pattern.Trim())
        {
            if (escaped)
            {
                result.Append(current);
                escaped = false;
                continue;
            }
            if (current == '\\')
            {
                if (pendingSpace && result.Length != 0)
                    result.Append(' ');
                pendingSpace = false;
                result.Append(current);
                escaped = true;
                continue;
            }
            if (quote != '\0')
            {
                result.Append(current);
                if (current == quote)
                    quote = '\0';
                continue;
            }
            if (current is '\'' or '"')
            {
                if (pendingSpace && result.Length != 0)
                    result.Append(' ');
                pendingSpace = false;
                quote = current;
                result.Append(current);
                continue;
            }
            if (current == '[')
                inClass = true;
            else if (current == ']')
                inClass = false;
            if (char.IsWhiteSpace(current) && !inClass)
            {
                pendingSpace = true;
                continue;
            }
            if (pendingSpace && result.Length != 0)
                result.Append(' ');
            pendingSpace = false;
            result.Append(current);
        }
        // Springcomp.GPLEX 1.2.5 warns on Flex's [+-] spelling. Escaping the
        // hyphen is the only accepted lexer-pattern normalization in this
        // pinned tool; all other pattern text remains exact.
        return result.ToString().Replace(@"[+\-]", "[+-]", StringComparison.Ordinal);
    }

    public static int FindCodeBlockEnd(string source, int start)
    {
        var end = source.IndexOf("%}", start, StringComparison.Ordinal);
        if (end < 0)
            throw new GuardrailException("unterminated lexer %{ code block");
        return end + 2;
    }

    public static string Preview(string source, int index) =>
        source[index..Math.Min(source.Length, index + 40)].Replace('\n', ' ');

    private static bool StartsWith(string source, int index, string value) =>
        index + value.Length <= source.Length && source.AsSpan(index, value.Length).SequenceEqual(value);

    private static void Blank(char[] result, string source, int start, int end)
    {
        for (var index = start; index < end; index++)
            if (source[index] != '\n' && source[index] != '\r')
                result[index] = ' ';
    }

    private static void PreserveNewlines(StringBuilder result, string source, int start, int end)
    {
        for (var index = start; index < end; index++)
            if (source[index] is '\n' or '\r')
                result.Append(source[index]);
    }

    private static int CopyQuoted(string source, char[]? characters, StringBuilder? builder, int start)
    {
        var quote = source[start];
        var index = start;
        while (index < source.Length)
        {
            var current = source[index];
            if (characters is not null)
                characters[index] = current;
            builder?.Append(current);
            index++;
            if (current == '\\' && index < source.Length)
            {
                if (characters is not null)
                    characters[index] = source[index];
                builder?.Append(source[index]);
                index++;
            }
            else if (current == quote && index > start + 1)
            {
                return index;
            }
        }
        throw new GuardrailException("unterminated quoted grammar symbol");
    }
}
