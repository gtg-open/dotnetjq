using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotNetJq.ParserGen;

internal static partial class Program
{
    private const string PinnedRepository = "https://github.com/jqlang/jq";
    private const string PinnedRevision = "34f7186b86743a083a589741b6cea95293524108";

    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (GuardrailException exception)
        {
            Console.Error.WriteLine($"guardrail: {exception.Message}");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"guardrail: unexpected failure: {exception}");
            return 2;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        var options = ParseOptions(args[1..]);
        switch (args[0])
        {
            case "validate":
                ValidateManagedFiles(
                    Required(options, "parser"),
                    Optional(options, "lexer"),
                    Optional(options, "expectations"),
                    Optional(options, "gppg") ?? "dotnet-gppg");
                Console.WriteLine("managed parser/lexer guardrails passed");
                return 0;

            case "compare":
                CompareFiles(
                    Required(options, "upstream-parser"),
                    Required(options, "managed-parser"),
                    Optional(options, "upstream-lexer"),
                    Optional(options, "managed-lexer"));
                Console.WriteLine("managed parser/lexer structure matches the supplied upstream sources");
                return 0;

            case "verify-gppg":
                VerifyWithGppg(Required(options, "parser"), Required(options, "gppg"));
                Console.WriteLine("GPPG conflict and precedence-only guards passed");
                return 0;

            case "generate-parser":
                GenerateParser(
                    Required(options, "parser"),
                    Required(options, "output"),
                    Optional(options, "expectations"),
                    Optional(options, "gppg") ?? "dotnet-gppg",
                    check: false);
                return 0;

            case "check-generated-parser":
                GenerateParser(
                    Required(options, "parser"),
                    Required(options, "output"),
                    Optional(options, "expectations"),
                    Optional(options, "gppg") ?? "dotnet-gppg",
                    check: true);
                return 0;

            case "capture-expectations":
                CaptureExpectations(
                    Required(options, "upstream-parser"),
                    Required(options, "upstream-lexer"),
                    Required(options, "out"));
                Console.WriteLine("wrote normalized structural expectations");
                return 0;

            case "inspect-parser":
                Console.Write(ParserGrammar.Parse(File.ReadAllText(Required(options, "parser")), managed: false)
                    .CanonicalStructure());
                return 0;

            case "self-test":
                SelfTests.Run(Optional(options, "gppg"));
                return 0;

            default:
                throw new GuardrailException($"unknown command '{args[0]}'");
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new GuardrailException("options must be written as --name value");

            result.Add(args[index][2..], args[index + 1]);
        }

        return result;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value)
            ? value
            : throw new GuardrailException($"missing --{name}");

    private static string? Optional(Dictionary<string, string> options, string name) =>
        options.GetValueOrDefault(name);

    private static void ValidateManagedFiles(
        string parserPath,
        string? lexerPath,
        string? expectationsPath,
        string gppgPath)
    {
        var parserText = File.ReadAllText(parserPath);
        var parser = ParserGrammar.Parse(parserText, managed: true);
        ValidateProvenance(parser.Provenance ?? throw new GuardrailException("missing parser provenance"), "src/parser.y");
        parser.ValidateManagedDialect();

        LexerGrammar? lexer = null;
        if (lexerPath is null)
        {
            StructuralExpectations.Load(expectationsPath).Validate(parser, null);
            GppgVerifier.Verify(parserText, parser.ExpectedConflicts, gppgPath);
            return;
        }

        var lexerText = File.ReadAllText(lexerPath);
        ValidateProvenance(Provenance.Parse(lexerText), "src/lexer.l");
        lexer = LexerGrammar.Parse(lexerText);
        StructuralExpectations.Load(expectationsPath).Validate(parser, lexer);
        GppgVerifier.Verify(parserText, parser.ExpectedConflicts, gppgPath);
    }

    private static void CompareFiles(
        string upstreamParserPath,
        string managedParserPath,
        string? upstreamLexerPath,
        string? managedLexerPath)
    {
        if ((upstreamLexerPath is null) != (managedLexerPath is null))
            throw new GuardrailException("--upstream-lexer and --managed-lexer must be supplied together");

        var upstream = ParserGrammar.Parse(File.ReadAllText(upstreamParserPath), managed: false);
        var managed = ParserGrammar.Parse(File.ReadAllText(managedParserPath), managed: true);
        ValidateProvenance(managed.Provenance ?? throw new GuardrailException("missing parser provenance"), "src/parser.y");
        managed.ValidateManagedDialect();
        ParserGrammar.Compare(upstream, managed);

        if (upstreamLexerPath is not null && managedLexerPath is not null)
        {
            var upstreamLexer = LexerGrammar.Parse(File.ReadAllText(upstreamLexerPath));
            var managedLexerText = File.ReadAllText(managedLexerPath);
            ValidateProvenance(Provenance.Parse(managedLexerText), "src/lexer.l");
            var managedLexer = LexerGrammar.Parse(managedLexerText);
            LexerGrammar.Compare(upstreamLexer, managedLexer);
        }
    }

    private static void ValidateProvenance(Provenance provenance, string sourcePath)
    {
        var errors = new List<string>();
        if (provenance.Repository != PinnedRepository)
            errors.Add($"repository must be {PinnedRepository}");
        if (provenance.Revision != PinnedRevision)
            errors.Add($"revision must be {PinnedRevision}");
        if (provenance.SourcePath != sourcePath)
            errors.Add($"source path must be {sourcePath}");
        if (errors.Count != 0)
            throw new GuardrailException("invalid provenance: " + string.Join("; ", errors));
    }

    private static void VerifyWithGppg(string parserPath, string gppgPath)
    {
        var source = File.ReadAllText(parserPath);
        var grammar = ParserGrammar.Parse(source, managed: true);
        ValidateProvenance(grammar.Provenance ?? throw new GuardrailException("missing parser provenance"), "src/parser.y");
        grammar.ValidateManagedDialect();
        GppgVerifier.Verify(source, grammar.ExpectedConflicts, gppgPath);
    }

    private static void GenerateParser(
        string parserPath,
        string outputPath,
        string? expectationsPath,
        string gppgPath,
        bool check)
    {
        var source = File.ReadAllText(parserPath);
        var grammar = ParserGrammar.Parse(source, managed: true);
        ValidateProvenance(grammar.Provenance ?? throw new GuardrailException("missing parser provenance"), "src/parser.y");
        grammar.ValidateManagedDialect();
        StructuralExpectations.Load(expectationsPath).Validate(grammar, null);
        var result = ParserOutputPipeline.Run(source, outputPath, gppgPath, grammar.ExpectedConflicts, check);
        Console.WriteLine(result);
    }

    private static void CaptureExpectations(string parserPath, string lexerPath, string outputPath)
    {
        var parser = ParserGrammar.Parse(File.ReadAllText(parserPath), managed: false);
        var lexer = LexerGrammar.Parse(File.ReadAllText(lexerPath));
        File.WriteAllText(outputPath, StructuralExpectations.Create(parser, lexer).ToJson());
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            jq parser/lexer port guardrail

              validate --parser MANAGED.y [--lexer MANAGED.lex]
                       [--expectations STRUCTURE.json] [--gppg dotnet-gppg]
              compare --upstream-parser UPSTREAM.y --managed-parser MANAGED.y
                      [--upstream-lexer UPSTREAM.l --managed-lexer MANAGED.lex]
              verify-gppg --parser MANAGED.y --gppg PATH_TO_GPPG_DLL_OR_EXE
              generate-parser --parser MANAGED.y --output PARSER.g.cs [--gppg dotnet-gppg]
              check-generated-parser --parser MANAGED.y --output PARSER.g.cs [--gppg dotnet-gppg]
              capture-expectations --upstream-parser UPSTREAM.y --upstream-lexer UPSTREAM.l
                                   --out STRUCTURE.json
              inspect-parser --parser GRAMMAR.y
              self-test [--gppg PATH_TO_GPPG_DLL_OR_EXE]
            """);
    }
}

internal sealed class GuardrailException(string message) : Exception(message);

internal sealed record Provenance(string Repository, string Revision, string SourcePath)
{
    public static Provenance Parse(string source)
    {
        string Read(string name)
        {
            var match = Regex.Match(
                source,
                @"(?m)^\s*(?://|/\*+|\*)?\s*jq-port-upstream-" + Regex.Escape(name) + @"\s*:\s*([^\s*]+)",
                RegexOptions.CultureInvariant);
            return match.Success
                ? match.Groups[1].Value
                : throw new GuardrailException($"missing jq-port-upstream-{name} provenance comment");
        }

        return new Provenance(Read("repository"), Read("revision"), Read("path"));
    }
}

internal sealed record PrecedenceDeclaration(
    string EffectiveKind,
    string PhysicalKind,
    IReadOnlyList<string> Symbols,
    bool IsPrecedenceOnlyMapping)
{
    public string Display => $"%{EffectiveKind} {string.Join(' ', Symbols)}";
}

internal sealed record Production(string Left, IReadOnlyList<string> Right, bool ExplicitEmpty)
{
    public string Display => $"{Left}: {(Right.Count == 0 ? "<empty>" : string.Join(' ', Right))}";
}

internal sealed class ParserGrammar
{
    private const string PrecedenceMarker = "jq-port: upstream-%precedence";
    private const string EmptyMarker = "jq-port: upstream-%empty";
    private const string ActionToken = "$action";

    private ParserGrammar(
        bool managed,
        Provenance? provenance,
        int expectedConflicts,
        bool hasRawExpect,
        bool hasRawUnsupportedDirective,
        IReadOnlyList<string> tokens,
        IReadOnlyList<string> typedSymbols,
        IReadOnlyList<PrecedenceDeclaration> precedence,
        IReadOnlyList<Production> productions)
    {
        Managed = managed;
        Provenance = provenance;
        ExpectedConflicts = expectedConflicts;
        HasRawExpect = hasRawExpect;
        HasRawUnsupportedDirective = hasRawUnsupportedDirective;
        Tokens = tokens;
        TypedSymbols = typedSymbols;
        Precedence = precedence;
        Productions = productions;
    }

    private bool Managed { get; }
    public Provenance? Provenance { get; }
    public int ExpectedConflicts { get; }
    private bool HasRawExpect { get; }
    private bool HasRawUnsupportedDirective { get; }
    internal IReadOnlyList<string> Tokens { get; }
    internal IReadOnlyList<string> TypedSymbols { get; }
    internal IReadOnlyList<PrecedenceDeclaration> Precedence { get; }
    internal IReadOnlyList<Production> Productions { get; }

    internal string CanonicalStructure()
    {
        var result = new StringBuilder();
        result.Append("expect\t").Append(ExpectedConflicts).AppendLine();
        foreach (var token in Tokens)
            result.Append("token\t").Append(token).AppendLine();
        foreach (var declaration in Precedence)
            result.Append("precedence\t").Append(declaration.Display).AppendLine();
        foreach (var production in Productions)
        {
            result.Append("production\t").Append(production.Left).Append('\t')
                .Append(production.ExplicitEmpty ? "explicit-empty" : "ordinary").Append('\t')
                .AppendJoin(' ', production.Right).AppendLine();
        }
        return result.ToString();
    }

    public static ParserGrammar Parse(string source, bool managed)
    {
        var sections = SourceText.SplitSections(source);
        if (sections.Count < 2)
            throw new GuardrailException("parser grammar must contain at least one %% delimiter");

        var declarations = sections[0];
        var maskedDeclarations = SourceText.MaskCommentsAndCodeBlocks(declarations);
        var rawExpect = Regex.Match(maskedDeclarations, @"(?m)^\s*%expect\s+(\d+)\s*$");
        var markerExpect = Regex.Match(
            declarations,
            @"(?m)jq-port\s*:\s*upstream-%expect\s+(\d+)",
            RegexOptions.CultureInvariant);

        var expectedConflicts = rawExpect.Success
            ? int.Parse(rawExpect.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : markerExpect.Success
                ? int.Parse(markerExpect.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                : throw new GuardrailException("missing %expect or 'jq-port: upstream-%expect N' marker");

        if (rawExpect.Success && markerExpect.Success && rawExpect.Groups[1].Value != markerExpect.Groups[1].Value)
            throw new GuardrailException("%expect and jq-port: upstream-%expect disagree");

        var tokens = ParseDeclarationSymbols(maskedDeclarations, "token", includeAliases: true);
        var aliases = BuildTokenAliasMap(tokens);
        var typedSymbols = ParseDeclarationSymbols(maskedDeclarations, "type", includeAliases: false);
        var precedence = ParsePrecedence(declarations, maskedDeclarations, managed)
            .Select(item => item with { Symbols = item.Symbols.Select(symbol => NormalizeAlias(symbol, aliases)).ToArray() })
            .ToArray();
        var productions = ParseProductions(sections[1], managed);
        var unsupported = Regex.IsMatch(maskedDeclarations, @"(?m)^\s*%(?:precedence|expect)\b") ||
                          Regex.IsMatch(SourceText.MaskCommentsAndActions(sections[1]), @"(?<![\w-])%empty\b");

        return new ParserGrammar(
            managed,
            managed ? Provenance.Parse(source) : null,
            expectedConflicts,
            rawExpect.Success,
            unsupported,
            tokens,
            typedSymbols,
            precedence,
            productions);
    }

    public void ValidateManagedDialect()
    {
        if (!Managed)
            throw new GuardrailException("internal error: managed validation requested for upstream grammar");
        if (ExpectedConflicts != 0)
            throw new GuardrailException($"managed jq grammar must declare zero expected conflicts, found {ExpectedConflicts}");
        if (HasRawExpect || HasRawUnsupportedDirective)
            throw new GuardrailException("managed GPPG input contains unsupported %expect, %precedence, or %empty syntax");

        foreach (var declaration in Precedence.Where(item => item.IsPrecedenceOnlyMapping))
        {
            if (declaration.PhysicalKind != "left")
                throw new GuardrailException(
                    $"precedence-only stand-in must use canonical %left form before dual generation: {declaration.Display}");
        }

        foreach (var production in Productions.Where(item => item.ExplicitEmpty))
        {
            var grammarSymbols = production.Right.Where(item => item != ActionToken).ToArray();
            if (grammarSymbols.Length != 0)
                throw new GuardrailException($"{EmptyMarker} marker is not an empty alternative: {production.Display}");
        }
    }

    public static void Compare(ParserGrammar upstream, ParserGrammar managed)
    {
        var differences = new List<string>();
        if (upstream.ExpectedConflicts != managed.ExpectedConflicts)
            differences.Add($"expected conflicts: upstream={upstream.ExpectedConflicts}, managed={managed.ExpectedConflicts}");
        CompareSequence("token declarations", upstream.Tokens, managed.Tokens, differences);
        var missingTypedSymbols = upstream.TypedSymbols.Except(managed.TypedSymbols).Order().ToArray();
        if (missingTypedSymbols.Length != 0)
            differences.Add("managed grammar dropped upstream typed symbols: " + string.Join(", ", missingTypedSymbols));
        CompareSequence(
            "precedence declarations",
            upstream.Precedence.Select(item => item.Display).ToArray(),
            managed.Precedence.Select(item => item.Display).ToArray(),
            differences);
        CompareSequence(
            "productions",
            upstream.Productions.Select(ComparisonProduction).ToArray(),
            managed.Productions.Select(ComparisonProduction).ToArray(),
            differences);

        var upstreamEmpty = upstream.Productions.Select((item, index) => (item, index))
            .Where(pair => pair.item.ExplicitEmpty).Select(pair => pair.index).ToArray();
        var managedEmpty = managed.Productions.Select((item, index) => (item, index))
            .Where(pair => pair.item.ExplicitEmpty).Select(pair => pair.index).ToArray();
        CompareSequence("explicit %empty mappings", upstreamEmpty, managedEmpty, differences);

        if (differences.Count != 0)
            throw new GuardrailException("parser structure mismatch:\n  " + string.Join("\n  ", differences));
    }

    private static string ComparisonProduction(Production production) =>
        $"{production.Left}: {string.Join(' ', production.Right)}";

    private static void CompareSequence<T>(
        string name,
        IReadOnlyList<T> upstream,
        IReadOnlyList<T> managed,
        List<string> differences)
    {
        var count = Math.Max(upstream.Count, managed.Count);
        for (var index = 0; index < count; index++)
        {
            if (index >= upstream.Count)
            {
                differences.Add($"{name}[{index}]: extra managed '{managed[index]}'");
                return;
            }

            if (index >= managed.Count)
            {
                differences.Add($"{name}[{index}]: missing managed item; upstream is '{upstream[index]}'");
                return;
            }

            if (!EqualityComparer<T>.Default.Equals(upstream[index], managed[index]))
            {
                differences.Add($"{name}[{index}]: upstream='{upstream[index]}', managed='{managed[index]}'");
                return;
            }
        }
    }

    private static List<string> ParseDeclarationSymbols(
        string maskedDeclarations,
        string directive,
        bool includeAliases)
    {
        var result = new List<string>();
        foreach (Match match in Regex.Matches(
                     maskedDeclarations,
                     @"(?m)^\s*%" + Regex.Escape(directive) + @"\s+(.+?)\s*$"))
        {
            var words = SourceText.TokenizeGrammar(match.Groups[1].Value)
                .Where(word => !word.StartsWith('<') && !int.TryParse(word, out _))
                .ToArray();
            if (directive == "type")
            {
                result.AddRange(words);
                continue;
            }

            for (var index = 0; index < words.Length; index++)
            {
                var word = words[index];
                if (SourceText.IsQuoted(word))
                {
                    if (includeAliases)
                        result.Add("alias=" + word);
                }
                else
                {
                    result.Add("token=" + word);
                }
            }
        }

        return result;
    }

    private static Dictionary<string, string> BuildTokenAliasMap(IReadOnlyList<string> tokens)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? currentToken = null;
        foreach (var entry in tokens)
        {
            if (entry.StartsWith("token=", StringComparison.Ordinal))
                currentToken = entry["token=".Length..];
            else if (entry.StartsWith("alias=", StringComparison.Ordinal) && currentToken is not null)
                result.Add(entry["alias=".Length..], currentToken);
        }
        return result;
    }

    private static string NormalizeAlias(string symbol, IReadOnlyDictionary<string, string> aliases) =>
        aliases.GetValueOrDefault(symbol, symbol);

    private static List<PrecedenceDeclaration> ParsePrecedence(
        string declarations,
        string maskedDeclarations,
        bool managed)
    {
        var originalLines = declarations.Split('\n');
        var maskedLines = maskedDeclarations.Split('\n');
        var result = new List<PrecedenceDeclaration>();
        for (var index = 0; index < maskedLines.Length; index++)
        {
            var match = Regex.Match(maskedLines[index], @"^\s*%(precedence|right|left|nonassoc)\s+(.+?)\s*$");
            if (!match.Success)
                continue;

            var physicalKind = match.Groups[1].Value;
            var mapped = originalLines[index].Contains(PrecedenceMarker, StringComparison.Ordinal);
            if (mapped && !managed)
                throw new GuardrailException($"upstream grammar unexpectedly contains {PrecedenceMarker}");
            if (mapped && physicalKind is not ("left" or "right"))
                throw new GuardrailException($"{PrecedenceMarker} must annotate a %left or %right declaration");

            result.Add(new PrecedenceDeclaration(
                mapped ? "precedence" : physicalKind,
                physicalKind,
                SourceText.TokenizeGrammar(match.Groups[2].Value),
                mapped));
        }

        return result;
    }

    private static List<Production> ParseProductions(string rules, bool managed)
    {
        var markedEmptyCount = Regex.Count(rules, @"/\*\s*" + EmptyMarker + @"\s*\*/");
        var prepared = SourceText.MaskCommentsAndActions(rules, EmptyMarker, "%empty", ActionToken);
        var result = new List<Production>();
        var headers = Regex.Matches(prepared, @"(?m)^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:");
        if (headers.Count == 0)
            throw new GuardrailException("parser grammar contains no productions");
        for (var headerIndex = 0; headerIndex < headers.Count; headerIndex++)
        {
            var header = headers[headerIndex];
            var left = header.Groups[1].Value;
            var bodyStart = header.Index + header.Length;
            var bodyEnd = headerIndex + 1 < headers.Count ? headers[headerIndex + 1].Index : prepared.Length;
            var words = SourceText.TokenizeGrammar(prepared[bodyStart..bodyEnd]).ToList();
            if (words.Count != 0 && words[^1] == ";")
                words.RemoveAt(words.Count - 1);
            var right = new List<string>();
            var explicitEmpty = false;
            foreach (var word in words.Append("|"))
            {
                if (word == "|")
                {
                    result.Add(BuildProduction(left, right, explicitEmpty));
                    right.Clear();
                    explicitEmpty = false;
                    continue;
                }

                if (word == ";")
                    throw new GuardrailException($"unexpected production terminator inside {left}");

                if (word == "%empty")
                {
                    explicitEmpty = true;
                    continue;
                }

                right.Add(word);
            }
        }

        if (managed && result.Count(item => item.ExplicitEmpty) != markedEmptyCount)
            throw new GuardrailException($"not every {EmptyMarker} marker was parsed as an alternative");
        return result;
    }

    private static Production BuildProduction(string left, IReadOnlyList<string> right, bool explicitEmpty)
    {
        if (explicitEmpty && right.Any(item => item != ActionToken))
            throw new GuardrailException($"%empty must be the only grammar symbol in {left}");
        return new Production(left, right.ToArray(), explicitEmpty);
    }
}

internal sealed class LexerGrammar
{
    private LexerGrammar(IReadOnlyList<string> startConditions, IReadOnlyList<string> rules)
    {
        StartConditions = startConditions;
        Rules = rules;
    }

    internal IReadOnlyList<string> StartConditions { get; }
    internal IReadOnlyList<string> Rules { get; }

    internal string CanonicalStructure()
    {
        var result = new StringBuilder();
        foreach (var condition in StartConditions)
            result.Append("start-condition\t").Append(condition).AppendLine();
        foreach (var rule in Rules)
            result.Append("rule\t").Append(rule).AppendLine();
        return result.ToString();
    }

    public static LexerGrammar Parse(string source)
    {
        var sections = SourceText.SplitSections(source);
        if (sections.Count < 2)
            throw new GuardrailException("lexer grammar must contain at least one %% delimiter");
        return new LexerGrammar(ParseStartConditions(sections[0]), ParseRules(sections[1]));
    }

    public static void Compare(LexerGrammar upstream, LexerGrammar managed)
    {
        var differences = new List<string>();
        CompareSequence("start condition", upstream.StartConditions, managed.StartConditions, differences);
        var count = Math.Max(upstream.Rules.Count, managed.Rules.Count);
        for (var index = 0; index < count; index++)
        {
            if (index >= upstream.Rules.Count)
            {
                differences.Add($"extra managed rule[{index}] '{managed.Rules[index]}'");
                break;
            }

            if (index >= managed.Rules.Count)
            {
                differences.Add($"missing managed rule[{index}] '{upstream.Rules[index]}'");
                break;
            }

            if (upstream.Rules[index] != managed.Rules[index])
            {
                differences.Add(
                    $"rule[{index}]: upstream='{upstream.Rules[index]}', managed='{managed.Rules[index]}'");
                break;
            }
        }

        if (differences.Count != 0)
            throw new GuardrailException("lexer structure mismatch: " + string.Join("; ", differences));
    }

    private static void CompareSequence(
        string name,
        IReadOnlyList<string> upstream,
        IReadOnlyList<string> managed,
        List<string> differences)
    {
        var count = Math.Max(upstream.Count, managed.Count);
        for (var index = 0; index < count; index++)
        {
            if (index >= upstream.Count || index >= managed.Count || upstream[index] != managed[index])
            {
                var upstreamValue = index < upstream.Count ? upstream[index] : "<missing>";
                var managedValue = index < managed.Count ? managed[index] : "<missing>";
                differences.Add($"{name}[{index}]: upstream='{upstreamValue}', managed='{managedValue}'");
                return;
            }
        }
    }

    private static List<string> ParseStartConditions(string declarations)
    {
        var masked = SourceText.MaskCommentsAndCodeBlocks(declarations);
        var result = new List<string>();
        foreach (Match match in Regex.Matches(masked, @"(?m)^\s*%(s|x)\s+(.+?)\s*$"))
        {
            var kind = match.Groups[1].Value;
            foreach (var name in match.Groups[2].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                result.Add(kind + " " + name);
        }
        return result;
    }

    private static List<string> ParseRules(string source)
    {
        var result = new List<string>();
        var scopes = new Stack<string>();
        var index = 0;
        while (index < source.Length)
        {
            SourceText.SkipTrivia(source, ref index);
            if (index >= source.Length)
                break;

            if (source[index] == '}' && scopes.Count != 0)
            {
                scopes.Pop();
                index++;
                continue;
            }

            var openingBrace = SourceText.FindLexerActionBrace(source, index);
            if (openingBrace < 0)
                throw new GuardrailException($"could not find action for lexer text near '{SourceText.Preview(source, index)}'");

            var pattern = source[index..openingBrace].Trim();
            if (pattern.StartsWith('<') && pattern.EndsWith('>') &&
                openingBrace == index + pattern.Length)
            {
                scopes.Push(SourceText.NormalizePattern(pattern));
                index = openingBrace + 1;
                continue;
            }

            if (pattern == "%")
            {
                index = SourceText.FindCodeBlockEnd(source, openingBrace + 1);
                continue;
            }

            var scope = scopes.Count == 0 ? "*" : string.Join("/", scopes.Reverse());
            result.Add(scope + " :: " + SourceText.NormalizePattern(pattern));
            index = SourceText.SkipBalancedBrace(source, openingBrace);
        }

        return result;
    }
}
