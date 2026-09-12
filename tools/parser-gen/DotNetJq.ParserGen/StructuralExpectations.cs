using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotNetJq.ParserGen;

internal sealed record StructuralExpectations(
    int SchemaVersion,
    string UpstreamRepository,
    string UpstreamRevision,
    string ParserSourcePath,
    string LexerSourcePath,
    int ExpectedConflicts,
    int ParserTokenEntries,
    int ParserPrecedenceDeclarations,
    int ParserProductionAlternatives,
    int ParserExplicitEmptyAlternatives,
    int LexerStartConditions,
    int LexerRules,
    string ParserStructuralSha256,
    string LexerStructuralSha256)
{
    private const string ResourceName =
        "DotNetJq.ParserGen.jq-1.8.2.structure.json";

    public static StructuralExpectations Create(ParserGrammar parser, LexerGrammar lexer) => new(
        SchemaVersion: 1,
        UpstreamRepository: "https://github.com/jqlang/jq",
        UpstreamRevision: "34f7186b86743a083a589741b6cea95293524108",
        ParserSourcePath: "src/parser.y",
        LexerSourcePath: "src/lexer.l",
        ExpectedConflicts: parser.ExpectedConflicts,
        ParserTokenEntries: parser.Tokens.Count,
        ParserPrecedenceDeclarations: parser.Precedence.Count,
        ParserProductionAlternatives: parser.Productions.Count,
        ParserExplicitEmptyAlternatives: parser.Productions.Count(item => item.ExplicitEmpty),
        LexerStartConditions: lexer.StartConditions.Count,
        LexerRules: lexer.Rules.Count,
        ParserStructuralSha256: Digest(parser.CanonicalStructure()),
        LexerStructuralSha256: Digest(lexer.CanonicalStructure()));

    public static StructuralExpectations Load(string? path)
    {
        Stream stream;
        if (path is null)
        {
            stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName) ??
                throw new GuardrailException($"embedded expectations resource '{ResourceName}' is missing");
        }
        else
        {
            stream = File.OpenRead(path);
        }

        using (stream)
        {
            return JsonSerializer.Deserialize<StructuralExpectations>(stream, JsonOptions) ??
                throw new GuardrailException("structural expectations JSON is empty");
        }
    }

    public void Validate(ParserGrammar parser, LexerGrammar? lexer)
    {
        var errors = new List<string>();
        Check(errors, SchemaVersion == 1, $"unsupported expectations schema {SchemaVersion}");
        Check(errors, UpstreamRepository == "https://github.com/jqlang/jq",
            "expectations repository does not identify the pinned jq repository");
        Check(errors, UpstreamRevision == "34f7186b86743a083a589741b6cea95293524108",
            "expectations revision does not identify the pinned jq commit");
        Check(errors, ParserSourcePath == "src/parser.y" && LexerSourcePath == "src/lexer.l",
            "expectations source paths do not identify jq's parser.y and lexer.l");
        Check(errors, ExpectedConflicts == parser.ExpectedConflicts,
            $"expected-conflict count is {parser.ExpectedConflicts}; pinned structure requires {ExpectedConflicts}");
        Check(errors, ParserTokenEntries == parser.Tokens.Count,
            $"token entry count is {parser.Tokens.Count}; pinned structure requires {ParserTokenEntries}");
        Check(errors, ParserPrecedenceDeclarations == parser.Precedence.Count,
            $"precedence count is {parser.Precedence.Count}; pinned structure requires {ParserPrecedenceDeclarations}");
        Check(errors, ParserProductionAlternatives == parser.Productions.Count,
            $"production count is {parser.Productions.Count}; pinned structure requires {ParserProductionAlternatives}");
        Check(errors, ParserExplicitEmptyAlternatives == parser.Productions.Count(item => item.ExplicitEmpty),
            $"explicit-empty count is {parser.Productions.Count(item => item.ExplicitEmpty)}; pinned structure requires {ParserExplicitEmptyAlternatives}");
        Check(errors, ParserStructuralSha256 == Digest(parser.CanonicalStructure()),
            "normalized parser structure differs from the pinned expectation");

        if (lexer is not null)
        {
            Check(errors, LexerStartConditions == lexer.StartConditions.Count,
                $"lexer start-condition count is {lexer.StartConditions.Count}; pinned structure requires {LexerStartConditions}");
            Check(errors, LexerRules == lexer.Rules.Count,
                $"lexer rule count is {lexer.Rules.Count}; pinned structure requires {LexerRules}");
            Check(errors, LexerStructuralSha256 == Digest(lexer.CanonicalStructure()),
                "normalized lexer structure differs from the pinned expectation");
        }

        if (errors.Count != 0)
            throw new GuardrailException("checked-in structure mismatch: " + string.Join("; ", errors));
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions) + "\n";

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void Check(List<string> errors, bool condition, string message)
    {
        if (!condition)
            errors.Add(message);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
