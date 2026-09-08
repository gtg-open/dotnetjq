namespace DotNetJq.DifferentialProbe;

internal sealed record ProbeCase(
    int Id,
    string Category,
    string Filter,
    string Input,
    string? CoveredBuiltinSignature = null);

internal static class Corpus
{
    internal const int DefaultSeed = 18_082;
    internal const int DefaultCount = 5_040;
    internal const int ExpectedBuiltinSignatureCount = 226;
    internal const int ExpectedDeclaredCategoryCount = 20;
    internal const int ExpectedDeclaredFilterCount = 437;
    internal const int ExpectedDeclaredInputCount = 48;

    internal static int DeclaredCategoryCount => Filters.Count + 1;

    internal static int DeclaredFilterCount => Filters.Sum(static pair => pair.Value.Length);

    internal static int DeclaredInputCount => Inputs.Length;

    private static int DistinctDeclaredFilterCount => Filters
        .SelectMany(static pair => pair.Value.Select(filter => (Category: pair.Key, Filter: filter)))
        .Distinct()
        .Count();

    private static int DistinctDeclaredInputCount => Inputs
        .Distinct(StringComparer.Ordinal)
        .Count();

    private static readonly string[] Inputs =
    [
        "null",
        "true",
        "false",
        "0",
        "-0",
        "1",
        "-1",
        "2",
        "-2",
        "0.5",
        "-0.5",
        "1.5",
        "9007199254740991",
        "9007199254740992",
        "9007199254740993",
        "123456789012345678901234567890",
        "1e-100",
        "1e100",
        "1e-1000",
        "1.7976931348623157e308",
        "\"\"",
        "\"0\"",
        "\"12.50\"",
        "\"true\"",
        "\"false\"",
        "\"null\"",
        "\"abc\"",
        "\"A,b z\"",
        "\" a\\tb\\n \"",
        "\"é😀\"",
        "\"á\"",
        "\"2020-01-02T03:04:05Z\"",
        "\"{\\\"a\\\":1}\"",
        "[]",
        "[0]",
        "[1,2,3]",
        "[3,1,2,1]",
        "[\"a\",null,\"b\"]",
        "[true,false,null]",
        "[[1],[2,3],[]]",
        "[{\"a\":1},{\"a\":2},{\"b\":0}]",
        "{}",
        "{\"a\":1}",
        "{\"a\":1,\"b\":2}",
        "{\"foo\":[1,2],\"bar\":\"a,b\"}",
        "{\"a\":{\"b\":[0,false,null]}}",
        "{\"0\":\"zero\",\"é\":\"unicode\"}",
        "{\"key\":\"value\",\"nested\":{\"x\":1},\"array\":[1,null,3]}",
    ];

    private static readonly IReadOnlyDictionary<string, string[]> Filters =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["streams"] =
            [
                ".", "empty", ".[]?", "[.[]?]", "(.[]?, .)",
                ". as $x | $x, $x", "select(. != null)", "select(type == \"number\")",
                "first(.[]?)", "last(.[]?)", "limit(2; .[]?)", "range(0; 3)",
                "[range(0; 4)]", ".[]? | select(. != null)", "[., empty, .]",
                "(.a? // empty), (.b? // empty)", "[.[]? | ., .]", "isempty(.[]?)",
                "nth(0; .[]?)", "nth(2; .[]?)", "skip(1; .[]?)", "[limit(3; repeat(.))]",
            ],
            ["generators"] =
            [
                "[range(0; 0)]", "[range(0; 5)]", "[range(5; 0; -2)]", "[range(-2; 3)]",
                "[limit(4; range(0; infinite))]", "[recurse(.[]?) | scalars]",
                "[limit(8; recurse(if type == \"number\" and . < 3 then . + 1 else empty end))]",
                "[while(. < 3; . + 1)?]", "[until(. >= 3; . + 1)?]",
                "[limit(3; repeat(.))]", "[combinations]?", "[combinations(2)]?",
                "[.[]? as $x | $x, ($x | tostring)]", "[first(.[]?), last(.[]?)]",
                "[limit(2; .[]?)]", "[skip(2; .[]?)]", "[nth(1; .[]?)]",
                "[foreach .[]? as $x (0; . + 1; [.,$x])]",
                "[reduce .[]? as $x ([]; . + [$x])]", "[paths]", "[leaf_paths]",
            ],
            ["updates"] =
            [
                ".foo", ".foo?", ".[0]", ".[-1]", ".[1:]", ".[0:2]",
                ".foo = 1", ".foo |= (. // 0) + 1", ".[0] = 9", ".[1:] = [7,8]",
                "del(.foo)", "getpath([\"a\"])", "setpath([\"a\",0]; 42)",
                "delpaths([[\"a\"]])", "(.a, .b) = 0", "(.a, .b) |= (. // 0) + 1",
                ".a.b? = 4", ".a.b? |= .", ".[2:1] = [9]", ".[1:99] |= reverse",
                "setpath([]; 7)", "getpath([])", "delpaths([])",
                "(.[]? | select(type == \"number\")) |= . + 10",
                "walk(if type == \"number\" then . + 1 else . end)",
            ],
            ["paths"] =
            [
                "path(.)", "path(.a)", "path(.a.b?)", "[path(.a, .b)]", "paths",
                "paths(type == \"number\")", "paths(scalars)", "[getpath([\"a\",\"b\"])]",
                "getpath([0])", "setpath([\"x\",1]; .)", "delpaths([[0],[2]])",
                "del(.a.b?)", "pick(.a, .b)?", "pick(.a.b, .[0])?",
                "[tostream]", "fromstream(1|truncate_stream(tostream))?",
                "[tostream | select(length == 2)]", "[paths as $p | [$p, getpath($p)]]",
                "reduce paths(scalars) as $p (.; setpath($p; getpath($p)))",
                "[.[]? | path(.)]",
            ],
            ["types"] =
            [
                "type", "tostring", "tonumber?", "toboolean?", "length", "keys?", "keys_unsorted?",
                "has(\"a\")", "has(0)", "contains(1)", "contains([1])", "inside([.,1])",
                "nulls", "booleans", "numbers", "strings", "arrays", "objects", "scalars",
                "iterables", "values", "normals", "finites", "type == \"object\"",
                "[nulls, booleans, numbers, strings, arrays, objects]",
            ],
            ["type-error-boundaries"] =
            [
                "try .[] catch type", "try .foo catch type", "try .[0] catch type",
                "try (. + {}) catch type", "try (. - []) catch type", "try (. * \"x\") catch type",
                "try (. / 0) catch type", "try length catch type", "try keys catch type",
                "try explode catch type", "try implode catch type", "try tonumber catch type",
                "try toboolean catch type", "try contains({}) catch type", "try inside({}) catch type",
                "try has([]) catch type", "try join(0) catch type", "try split(null) catch type",
                "try startswith(null) catch type", "try setpath(null;0) catch type",
                "try getpath(null) catch type", "try delpaths(null) catch type",
                "try range(.; 2) catch type", "try bsearch(.) catch type",
                "try from_entries catch type", "try transpose catch type",
                "try error(.) catch [type,.]", "try error catch [type,.]",
                "try .[] catch .", "try .foo catch .", "try .[0] catch .",
                "try has([]) catch .", "try contains({}) catch .", "try inside({}) catch .",
                "try join(0) catch .", "try split(null) catch .", "try getpath(null) catch .",
                "try setpath(null; 0) catch .", "try delpaths(null) catch .",
                "try from_entries catch .", "try transpose catch .",
            ],
            ["numbers"] =
            [
                ". + 1", ". - 1", ". * 2", ". / 2", ". % 2", "-.", "abs",
                "floor", "ceil", "round", "trunc", "sqrt?", "log?", "exp?", ". == -0",
                ". < 1", ". >= 0", "9007199254740993", "1e-1000", "-0",
                "[nan, infinite, -infinite]", "isnan", "isinfinite", "isfinite", "isnormal",
                "[frexp]", "[modf]", "significand", "nextafter(.; 1)?", "copysign(.; -1)?",
            ],
            ["numeric-literals"] =
            [
                "0", "-0", "0.0", "-0.0", "1e0", "1e+0", "1e-0", "1.00",
                "9007199254740991", "9007199254740992", "9007199254740993",
                "123456789012345678901234567890", "0.000000000000000000000000000001",
                "1e-308", "1e308", "1e-1000", "1e1000", "[., . + 0]",
                "[. == 0, . == -0, . < 0]", "tonumber?", "tostring",
                "try (. + 0.000000000000000000000000000001) catch type",
            ],
            ["math-general"] =
            [
                "acos?", "acosh?", "asin?", "asinh?", "atan?", "atanh?", "cbrt?",
                "cos?", "cosh?", "sin?", "sinh?", "tan?", "tanh?", "log10?", "log1p?",
                "log2?", "exp2?", "expm1?", "fabs?", "nearbyint?", "rint?",
                "atan2(.; 2)?", "hypot(.; 2)?", "pow(.; 2)?", "fmod(.; 2)?",
                "remainder(.; 2)?", "fdim(.; 2)?", "fmax(.; 2)?", "fmin(.; 2)?",
                "ldexp(.; 2)?", "fma(.; 2; 3)?",
            ],
            ["strings"] =
            [
                "ascii_downcase", "ascii_upcase", "explode", "implode?", "utf8bytelength",
                "split(\",\")", "join(\"-\")", "startswith(\"a\")", "endswith(\"z\")",
                "ltrimstr(\"a\")", "rtrimstr(\"c\")", "trimstr(\"a\")", "trim", "ltrim", "rtrim",
                "indices(\"a\")", "index(\"a\")", "rindex(\"a\")", "@text", "@json", "@uri",
                "@base64", "@base64d?", "\"value=\\(.) type=\\(type)\"",
                "explode | implode", "[explode[]?]", "split(\"\")", "[., tostring, tojson]",
            ],
            ["formats"] =
            [
                "@json", "@text", "@html", "@uri", "@csv", "@tsv", "@sh", "@base64", "@base64d?",
                "tojson", "fromjson?", "tostring", "format(\"json\")", "format(\"text\")",
                "format(\"html\")", "format(\"uri\")", "format(\"csv\")", "format(\"tsv\")",
                "format(\"sh\")", "format(\"base64\")", "format(\"base64d\")?",
                "[., @json, (@base64 | @base64d)]", "try fromjson catch type",
            ],
            ["objects-arrays"] =
            [
                "[., .]", "{value: ., kind: type}", "to_entries?", "from_entries?",
                "with_entries(.key |= ascii_upcase)?", "map(.)?", "map_values(.)?",
                "add?", "flatten?", "flatten(1)?", "reverse?", "sort?", "unique?",
                "group_by(type)?", "min?", "max?", "min_by(type)?", "max_by(type)?",
                "transpose?", "bsearch(1)?", "[paths]", "[leaf_paths]", "sort_by(type, tostring)?",
                "unique_by(type)?", "[.[]?]", "[to_entries[]? | [.key,.value]]",
                "with_entries(select(.value != null))?", "map_values(select(. != null))?",
            ],
            ["control-errors"] =
            [
                "try .[] catch .", "try error(\"x\") catch .", "try error(.) catch .",
                ".foo[]?", ".a? // .b? // 0", "if . then \"yes\" else \"no\" end",
                "if type == \"array\" then length else 0 end", "(. // empty)",
                "try tonumber catch \"bad-number\"", "label $out | (., break $out, 1)",
                "[.[]? as $x | try ($x + 1) catch null]", "first(.[]?) // null",
                "reduce .[]? as $x (0; . + ($x | numbers))",
                "foreach .[]? as $x (0; . + ($x | numbers); .)",
                "try (try error(.) catch error(.)) catch [type,.]",
                "if . == null then empty elif . then 1 else 0 end",
                "(. as $x | select($x == .))", "label $x | [1, break $x, 2]",
                "try (1, error(\"x\"), 2) catch .", "[empty // 1, false // 2, null // 3]",
            ],
            ["language-compiler"] =
            [
                "def f: .; f", "def f($x): [$x,$x]; f(.)", "def f: g; def g: .; f",
                "def twice(f): f|f; twice(.)", "def fact: if . <= 1 then 1 else . * (.-1|fact) end; try (select(type == \"number\" and . >= 0 and . <= 12) | fact) catch empty",
                ". as $x | [$x, ($x as $x | $x), $x]", ". as {$a, b:$b}? | [$a,$b]",
                ". as [$a,$b]? | [$a,$b]", "[.[]? as {$a}? | $a]", "[.[]? as [$a]? | $a]",
                "(.a? // .b?) as $x | $x", "[.[]? | . as $x | select($x)]",
                "1 + 2 * 3", "(1 + 2) * 3", "not not", ". == (. as $x | $x)",
                "\"outer \\(" + "\"inner \\(.)\"" + ")\"", "{(type): ., static: .}",
                "[1,2,3] | .[1:]", "{a,b:(. // null)}", "try does_not_exist catch empty",
                "[foreach range(0;3) as $x (.; .; [$x,.])]",
            ],
            ["modules"] =
            [
                "try modulemeta catch empty", "try (\"__dotnetjq_missing_probe__\" | modulemeta) catch type",
                "include \"__dotnetjq_missing_probe__\"; .", "import \"__dotnetjq_missing_probe__\" as p; .",
                "import \"__dotnetjq_missing_probe__\" as $p; .", "module {name:\"probe\"}; .",
            ],
            ["dates"] =
            [
                "1577934245 | gmtime", "1577934245 | gmtime | mktime", "1577934245 | todate",
                "1577934245 | todateiso8601", "\"2020-01-02T03:04:05Z\" | fromdate",
                "\"2020-01-02T03:04:05Z\" | fromdateiso8601",
                "\"2020-01-02T03:04:05Z\" | strptime(\"%Y-%m-%dT%H:%M:%SZ\")",
                "1577934245 | gmtime | strftime(\"%Y-%m-%dT%H:%M:%SZ\")",
                "1577934245 | gmtime | strftime(\"%a %b %d %H:%M:%S %Y\")",
                "try gmtime catch type", "try mktime catch type", "try strftime(\"%Y\") catch type",
                "try strptime(\"%Y-%m-%d\") catch type", "try fromdate catch type",
            ],
            ["stateful-no-capability"] =
            [
                "try input catch empty", "try inputs catch empty", "try (debug | empty) catch empty",
                "try (debug(\"probe\") | empty) catch empty", "try (stderr | empty) catch empty",
                "try (input_filename | empty) catch empty", "try (input_line_number | empty) catch empty",
                "try (env | empty) catch empty", "try (now | empty) catch empty",
                "try (get_jq_origin | empty) catch empty", "try (get_prog_origin | empty) catch empty",
                "try (get_search_list | empty) catch empty", "(1, halt, 2)",
            ],
            ["regex"] =
            [
                "test(\"a\")?", "test(\"^a\"; \"i\")?", "match(\"a+\")?",
                "match(\"(?<x>a)(?<y>b)?\"; \"g\")?", "capture(\"(?<x>a+)\")?",
                "[scan(\"[[:alpha:]]+\")]?", "split(\"a\")?", "[splits(\"a\")]?",
                "sub(\"a\"; \"X\")?", "gsub(\"a\"; \"X\")?",
                "gsub(\"(?<x>a)\"; \"<\\(.x)>\")?", "test(\"😀\")?",
                "match(\".\"; \"g\")?", "[scan(\"(?:a|b)*\")]?",
                "split(\"[, ]+\")?", "sub(\"^\"; \"start:\")?",
            ],
            ["builtins"] =
            [
                "any", "all", "any(.[]?; .)", "all(.[]?; .)",
                "map(select(. != null))?", "sort_by(type)?", "unique_by(type)?",
                "min_by(type)?", "max_by(type)?", "arrays | length", "objects | length",
                "recurse(.[]?)", "[recurse(.[]?) | scalars]", "while(. < 3; . + 1)?",
                "until(. >= 3; . + 1)?", "[combinations]?",
                "walk(if type == \"number\" then .+1 else . end)",
                "pick(.a, .b)?", "in({\"a\":1})", "path(.a?)", "paths(type == \"number\")",
                "[range(1; 5) | . * .]", "nth(1; .[]?)", "[limit(3; recurse(.+1))]",
                "INDEX(.[]?; .a?)", "JOIN(INDEX(.[]?; .a?); .[]?; .a?; [.])",
                "IN(.[]?; .)", "builtins | length", "have_decnum", "have_literal_numbers",
            ],
        };

    internal static IReadOnlyList<ProbeCase> Generate(int seed, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ValidateDeclarationInventory();

        var signatureProbes = BuiltinProbeCatalog.Create().ToList();
        Shuffle(signatureProbes, new Random(unchecked(seed ^ 0x51A7_226)));

        var queues = Filters
            .Select((pair, categoryIndex) =>
            {
                var required = pair.Value
                    .Select((filter, filterIndex) =>
                        (Filter: filter, Input: Inputs[PositiveModulo(
                            seed + categoryIndex * 17 + filterIndex * 31,
                            Inputs.Length)]))
                    .ToList();
                Shuffle(required, new Random(unchecked(seed ^ (categoryIndex + 1) * 104_729)));

                var requiredSet = required.ToHashSet();
                var remainder = pair.Value
                    .SelectMany(filter => Inputs.Select(input => (Filter: filter, Input: input)))
                    .Where(combination => !requiredSet.Contains(combination))
                    .ToList();
                Shuffle(remainder, new Random(unchecked(seed + categoryIndex * 7_919)));
                var combinations = required.Concat(remainder).ToList();
                return (Category: pair.Key, Combinations: combinations, Index: 0);
            })
            .ToArray();

        var maximumCapacity = signatureProbes.Count + queues.Sum(queue => queue.Combinations.Count);
        if (count > maximumCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Requested corpus exceeds unique case capacity.");
        }

        var cases = new List<ProbeCase>(count);
        var uniquePairs = new HashSet<(string Filter, string Input)>();
        foreach (var probe in signatureProbes)
        {
            if (cases.Count == count)
            {
                break;
            }

            if (!uniquePairs.Add((probe.Filter, probe.Input)))
            {
                throw new InvalidOperationException("Builtin signature probes must use unique filter/input pairs.");
            }

            cases.Add(new ProbeCase(
                cases.Count + 1,
                "builtin-signatures",
                probe.Filter,
                probe.Input,
                probe.Signature));
        }

        var categoryCursor = Math.Abs(seed % queues.Length);
        while (cases.Count < count)
        {
            var selected = -1;
            (string Filter, string Input) combination = (string.Empty, string.Empty);
            for (var offset = 0; offset < queues.Length; offset++)
            {
                var candidate = (categoryCursor + offset) % queues.Length;
                while (queues[candidate].Index < queues[candidate].Combinations.Count)
                {
                    var queue = queues[candidate];
                    combination = queue.Combinations[queue.Index];
                    queues[candidate] = (queue.Category, queue.Combinations, queue.Index + 1);
                    if (uniquePairs.Add(combination))
                    {
                        selected = candidate;
                        break;
                    }
                }

                if (selected >= 0)
                {
                    break;
                }
            }

            if (selected < 0)
            {
                throw new InvalidOperationException("Corpus generation exhausted unexpectedly.");
            }

            cases.Add(new ProbeCase(cases.Count + 1, queues[selected].Category, combination.Filter, combination.Input));
            categoryCursor = (selected + 1) % queues.Length;
        }

        Validate(cases, count);
        return cases;
    }

    internal static IReadOnlySet<string> CoveredBuiltinSignatures(IEnumerable<ProbeCase> cases) =>
        cases
            .Where(static testCase => testCase.CoveredBuiltinSignature is not null)
            .Select(static testCase => testCase.CoveredBuiltinSignature!)
            .ToHashSet(StringComparer.Ordinal);

    private static void ValidateDeclarationInventory()
    {
        if (DeclaredCategoryCount != ExpectedDeclaredCategoryCount)
        {
            throw new InvalidOperationException(
                $"General differential corpus declares {DeclaredCategoryCount} categories; " +
                $"expected {ExpectedDeclaredCategoryCount}.");
        }

        if (DeclaredFilterCount != ExpectedDeclaredFilterCount)
        {
            throw new InvalidOperationException(
                $"General differential corpus declares {DeclaredFilterCount} category filters; " +
                $"expected {ExpectedDeclaredFilterCount}.");
        }

        if (DistinctDeclaredFilterCount != ExpectedDeclaredFilterCount)
        {
            throw new InvalidOperationException(
                $"General differential corpus declares {DistinctDeclaredFilterCount} distinct category/filter pairs; " +
                $"expected {ExpectedDeclaredFilterCount}; duplicate declarations are forbidden.");
        }

        if (DeclaredInputCount != ExpectedDeclaredInputCount)
        {
            throw new InvalidOperationException(
                $"General differential corpus declares {DeclaredInputCount} JSON inputs; " +
                $"expected {ExpectedDeclaredInputCount}.");
        }

        if (DistinctDeclaredInputCount != ExpectedDeclaredInputCount)
        {
            throw new InvalidOperationException(
                $"General differential corpus declares {DistinctDeclaredInputCount} distinct JSON inputs; " +
                $"expected {ExpectedDeclaredInputCount}; duplicate declarations are forbidden.");
        }
    }

    private static void Validate(List<ProbeCase> cases, int requestedCount)
    {
        if (cases.Count != requestedCount)
        {
            throw new InvalidOperationException("Corpus generation returned the wrong case count.");
        }

        if (cases.Select(static testCase => (testCase.Filter, testCase.Input)).Distinct().Count() != cases.Count)
        {
            throw new InvalidOperationException("Every differential case must have a unique filter/input pair.");
        }

        if (requestedCount >= ExpectedBuiltinSignatureCount)
        {
            var covered = CoveredBuiltinSignatures(cases);
            var expected = BuiltinProbeCatalog.ExpectedSignatures.ToHashSet(StringComparer.Ordinal);
            if (!covered.SetEquals(expected))
            {
                throw new InvalidOperationException("The corpus does not cover all 226 pinned public builtin signatures.");
            }
        }

        if (requestedCount >= DefaultCount)
        {
            var missingCategories = Filters.Keys
                .Where(category => !cases.Any(testCase => testCase.Category.Equals(category, StringComparison.Ordinal)))
                .ToArray();
            if (missingCategories.Length != 0)
            {
                throw new InvalidOperationException(
                    "The default corpus omitted categories: " + string.Join(", ", missingCategories));
            }

            var missingFilters = Filters
                .SelectMany(static pair => pair.Value.Select(filter => (Category: pair.Key, Filter: filter)))
                .Where(expected => !cases.Any(testCase =>
                    testCase.Category.Equals(expected.Category, StringComparison.Ordinal) &&
                    testCase.Filter.Equals(expected.Filter, StringComparison.Ordinal)))
                .ToArray();
            if (missingFilters.Length != 0)
            {
                throw new InvalidOperationException(
                    "The default corpus omitted declared filters: " +
                    string.Join(", ", missingFilters.Select(static item => item.Category + ":" + item.Filter)));
            }

            var missingInputs = Inputs
                .Where(input => !cases.Any(testCase => testCase.Input.Equals(input, StringComparison.Ordinal)))
                .ToArray();
            if (missingInputs.Length != 0)
            {
                throw new InvalidOperationException(
                    "The default corpus omitted declared inputs: " + string.Join(", ", missingInputs));
            }

            var balancedCounts = cases
                .Where(static testCase => !testCase.Category.Equals("builtin-signatures", StringComparison.Ordinal))
                .GroupBy(static testCase => testCase.Category)
                .Select(static group => group.Count())
                .ToArray();
            if (balancedCounts.Max() - balancedCounts.Min() > 1)
            {
                throw new InvalidOperationException("The default category distribution must remain balanced.");
            }
        }
    }

    private static void Shuffle<T>(List<T> values, Random random)
    {
        for (var index = values.Count - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (values[index], values[other]) = (values[other], values[index]);
        }
    }

    private static int PositiveModulo(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }
}
