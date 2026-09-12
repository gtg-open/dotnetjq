// Behavioral smoke run by validate.py; never part of the normal
// DotNetJq project or shipped output.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DotNetJq;
using DotNetJq.Port;

namespace DotNetJq.Port.GeneratedParser;

internal static class JqGeneratedParserSmoke
{
    private const string SourceName = "<gppg-managed-parser-smoke>";

    public static int Main()
    {
        var executionCases = new (string Source, string Input, string[] Expected)[]
        {
            (".", "{\"x\":4}", ["{\"x\":4}"]),
            ("1 + 2 * 3", "null", ["7"]),
            (".foo // 9", "{\"foo\":4}", ["4"]),
            ("[.[], 7]", "[1,2]", ["[1,2,7]"]),
            ("{a: .x, b: 2}", "{\"x\":3}", ["{\"a\":3,\"b\":2}"]),
            ("if .flag then .value else 0 end", "{\"flag\":true,\"value\":8}", ["8"]),
            (". as $x | $x + 1", "4", ["5"]),
            ("1 as $v | $$$$v", "null", ["1"]),
            ("reduce .[] as $x (0; . + $x)", "[1,2,3]", ["6"]),
            ("def add($x): . + $x; add(2)", "5", ["7"]),
            ("\"a\\(1 + 1)b\"", "null", ["\"a2b\""]),
        };

        foreach (var (source, input, expected) in executionCases)
        {
            var parsed = ParseProduction(source);
            try
            {
                Equal(true, libjq.block_has_main(parsed), source + " has main");
                Equal(opcode.TOP, parsed.first?.op, source + " TOP opcode");
            }
            finally
            {
                libjq.block_free(parsed);
            }

            EqualList(expected, Evaluate(source, input), source + " results");
        }

        const string referenceSource =
            "module {\"m\":1}; " +
            "import \"thing\" as thing {\"x\":2}; " +
            "def local($x): $x + $ENV.missing; " +
            "local(1), external, $missing, $ARGS";
        var referenceProgram = ParseProduction(referenceSource);
        try
        {
            Equal(true, libjq.block_has_main(referenceProgram), "metadata HasMainExpression");
            var instructions = AllInstructions(referenceProgram).ToArray();
            var moduleMetadata = instructions.Single(
                static instruction => instruction.op == opcode.MODULEMETA);
            Equal(
                "{\"m\":1}",
                DumpBorrowed(moduleMetadata.imm.constant),
                "metadata ModuleMetadata");
            var dependency = instructions.Single(
                static instruction => instruction.op == opcode.DEPS);
            Equal(
                "{\"x\":2,\"as\":\"thing\",\"is_data\":false,\"relpath\":\"thing\"}",
                DumpBorrowed(dependency.imm.constant),
                "metadata Import");
            var definition = instructions.Single(
                static instruction =>
                    instruction.op == opcode.CLOSURE_CREATE &&
                    instruction.symbol == "local");
            Equal(1, definition.nformals, "metadata Definition arity");

            var rawReferences = instructions
                .Where(static instruction =>
                    instruction.bound_by is null &&
                    instruction.source.start >= 0 &&
                    instruction.op is opcode.CALL_JQ or opcode.LOADV or opcode.LOADVN)
                .Select(ReferenceSignature)
                .ToArray();
            Contains("external/0", rawReferences, "metadata CallReferences");
            Contains("$missing", rawReferences, "metadata UnresolvedVariables");

            var sourceNames = instructions
                .Where(static instruction => instruction.source.start >= 0)
                .Select(static instruction => instruction.locfile?.fname.StringValue)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            EqualList([SourceName], sourceNames, "metadata SourceName");
        }
        finally
        {
            libjq.block_free(referenceProgram);
        }

        var invalidCases = new (string Family, string Source)[]
        {
            ("module metadata must be constant", "module (.+1); 0"),
            ("module metadata must be an object", "module []; 0"),
            ("import metadata must be constant", "include \"a\" (.+1); 0"),
            ("import metadata must be an object", "include \"a\" []; 0"),
            ("import path must be constant", "include \"\\(a)\"; 0"),
            ("dictionary constant object key", "{(1+2):0}"),
            ("pattern constant object key", ". as {(1+2): $x} | ."),
            ("unresolved break label", "break $missing"),
            ("Term: BREAK error", "break $__loc__"),
            ("Term: '.' error", ". 0"),
            ("Term: '.' IDENT error", ". foo"),
            ("Term: if Query then error", "if true then"),
            ("Term: try Expr catch error", "try . catch"),
            ("Term: '(' error ')'", "(;)"),
            ("Term: '[' error ']'", "[;]"),
            ("Term: Term '[' error ']'", ".[;]"),
            ("Term: '{' error '}'", "{;}"),
            ("ObjPat: error ':' Pattern", ". as {foo +: $x} | ."),
            ("DictPair: error ':' DictExpr", "{$__loc__:1}"),
        };
        var nativeOracle = Environment.GetEnvironmentVariable(
            "JQ_PARSER_NATIVE_ORACLE");
        foreach (var (family, source) in invalidCases)
        {
            var generatedError = CompileError(source);
            if (!string.IsNullOrEmpty(nativeOracle))
            {
                NativeContains(nativeOracle, source, generatedError, family);
            }
        }

        var scannerDiagnosticCases = new (
            string Family,
            string Source,
            string Expected)[]
        {
            (
                "bare format marker",
                "@",
                "jq: error: syntax error, unexpected INVALID_CHARACTER, expecting end of file " +
                "at " + SourceName + ", line 1, column 1:\n    @\n    ^"),
            (
                "unterminated string",
                "\"unterminated",
                "jq: error: syntax error, unexpected end of file, expecting QQSTRING_TEXT " +
                "or QQSTRING_INTERP_START or QQSTRING_END at " + SourceName +
                ", line 1, column 2:\n    \"unterminated\n     ^^^^^^^^^^^^"),
            (
                "unterminated interpolation",
                "\"\\(",
                "jq: error: syntax error, unexpected end of file at " + SourceName +
                ", line 1, column 2:\n    \"\\(\n     ^^"),
        };
        foreach (var (family, source, expected) in scannerDiagnosticCases)
        {
            var generatedError = ParseError(source);
            Equal(expected, generatedError, family);
            if (!string.IsNullOrEmpty(nativeOracle))
            {
                NativeContains(nativeOracle, source, generatedError, family);
            }
        }

        var stackError = ParseError("\"\\(" + new string('(', 250_000));
        Equal("jq: error: memory exhausted", stackError, "10,000-entry parser stack");

        var acceptedNesting = JqGeneratedParser.MaximumNestedParenthesisDepth;
        var accepted = ParseProduction(
            new string('(', acceptedNesting) + "." + new string(')', acceptedNesting));
        libjq.block_free(accepted);
        var rejectedNesting = acceptedNesting + 1;
        Equal(
            "jq: error: memory exhausted",
            ParseError(
                new string('(', rejectedNesting) + "." + new string(')', rejectedNesting)),
            "first rejected nested-parenthesis depth");
        Console.WriteLine(
            $"managed parser nesting boundary: accepts {acceptedNesting}; rejects {rejectedNesting}");

        Console.WriteLine(
            "managed parser smoke: " + executionCases.Length +
            " execution cases, metadata/reference corpus, and " +
            invalidCases.Length + " invalid-input cases rejected" +
            (string.IsNullOrEmpty(nativeOracle)
                ? string.Empty
                : " with diagnostics matched by pinned native jq"));
        return 0;
    }

    private static block ParseProduction(string source) =>
        JqGeneratedParser.ParseSource(source, SourceName);

    private static string[] Evaluate(string source, string input)
    {
        using var program = JqProgram.Compile(source);
        return program
            .Execute(input)
            .Select(static value => value.GetRawText())
            .ToArray();
    }

    private static string CompileError(string source)
    {
        try
        {
            using var program = JqProgram.Compile(source);
        }
        catch (JqCompileException exception)
        {
            return exception.Message;
        }

        throw new InvalidOperationException("invalid jq source unexpectedly parsed");
    }

    private static string ParseError(string source)
    {
        var parsed = default(block);
        try
        {
            parsed = ParseProduction(source);
        }
        catch (JqCompileException exception)
        {
            return exception.Message;
        }
        finally
        {
            libjq.block_free(parsed);
        }

        throw new InvalidOperationException("invalid jq source unexpectedly parsed");
    }

    private static void NativeContains(
        string executable,
        string source,
        string expected,
        string family)
    {
        expected = expected.Replace(SourceName, "<top-level>", StringComparison.Ordinal);
        var start = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--null-input");
        start.ArgumentList.Add(source);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("could not start native jq oracle");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode == 0 ||
            !standardError.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                family + " native jq mismatch\nexpected diagnostic:\n" + expected +
                "\nnative stdout:\n" + standardOutput +
                "\nnative stderr:\n" + standardError);
        }
    }

    private static string DumpBorrowed(jv value)
    {
        // jq's jv_dump_string() consumes its argument. Metadata remains owned by
        // the MODULEMETA/DEPS instruction until block_free(), so inspection must
        // spell the native jv_copy() contract through the managed borrow adapter.
        var refcount = libjq.jv_get_refcnt(value);
        var result = libjq.jv_dump_string_borrowed(value);
        Equal(refcount, libjq.jv_get_refcnt(value), "metadata dump ownership");
        return result;
    }

    private static string ReferenceSignature(inst instruction) =>
        instruction.op is opcode.LOADV or opcode.LOADVN
            ? "$" + instruction.symbol
            : instruction.symbol + "/" + instruction.nactuals;

    private static IEnumerable<inst> AllInstructions(block value)
    {
        for (var instruction = value.first;
             instruction is not null;
             instruction = instruction.next)
        {
            yield return instruction;
            foreach (var argument in AllInstructions(instruction.arglist))
            {
                yield return argument;
            }

            foreach (var nested in AllInstructions(instruction.subfn))
            {
                yield return nested;
            }
        }
    }

    private static void Contains<T>(T expected, IReadOnlyList<T> actual, string subject)
    {
        if (!actual.Contains(expected))
        {
            throw new InvalidOperationException(
                subject + " mismatch\nmissing: " + expected +
                "\nactual:  " + string.Join(" || ", actual));
        }
    }

    private static void Equal<T>(T expected, T actual, string subject)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                subject + " mismatch\nexpected: " + expected + "\nactual:   " + actual);
        }
    }

    private static void EqualList<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string subject)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                subject + " mismatch\nexpected: " + string.Join(" || ", expected) +
                "\nactual:   " + string.Join(" || ", actual));
        }
    }
}
