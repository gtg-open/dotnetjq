using System.Text.Json;

namespace DotNetJq.Cli.Tests;

// jq 1.8.2 source references:
// - src/util.c:jq_util_input_read_more/jq_util_input_next_input
// - src/util.c:jq_util_input_get_position/jq_util_input_get_current_line
// These process tests freeze the observable consequences of the native
// 4096-byte state buffer, fgets(4092), and UTF-8 tail fread.
public sealed class CliInputBufferCompatibilityTests
{
    private const string RawBoundaryFilter =
        "[input_line_number,contains(\"\\n\"),length,utf8bytelength]";
    private const string RawSlurpBoundaryFilter =
        "[input_line_number,(split(\"\\n\")|length),length,utf8bytelength]";

    [Fact]
    public async Task ParsedEndAndSlurpUseTheFinalInputStatePosition()
    {
        var trailingBlankLines = await RunSubjectAsync(
            ["-M", "-c", "[.,(try input catch .),input_filename,input_line_number]"],
            "0\n\n"u8.ToArray());
        CliAssertions.Equal(
            trailingBlankLines,
            0,
            "[0,\"break\",\"<stdin>\",2]\n",
            "");

        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var one = directory.File("one.json");
        var finalEmpty = directory.File("final-empty.json");
        var finalBlank = directory.File("final-blank.json");
        await File.WriteAllBytesAsync(
            one,
            "1\n"u8.ToArray(),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            finalEmpty,
            [],
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            finalBlank,
            "\n\n"u8.ToArray(),
            TestContext.Current.CancellationToken);

        var onlyEmpty = await RunSubjectAsync(
            ["-M", "-c", "-s", "[input_filename,input_line_number,.]", finalEmpty],
            []);
        CliAssertions.Equal(
            onlyEmpty,
            0,
            $"[{JsonSerializer.Serialize(finalEmpty)},0,[]]\n",
            "");

        var emptyAfterValue = await RunSubjectAsync(
            ["-M", "-c", "-s", "[input_filename,input_line_number,.]", one, finalEmpty],
            []);
        CliAssertions.Equal(
            emptyAfterValue,
            0,
            $"[{JsonSerializer.Serialize(finalEmpty)},0,[1]]\n",
            "");

        var blanksAfterValue = await RunSubjectAsync(
            ["-M", "-c", "-s", "[input_filename,input_line_number,.]", one, finalBlank],
            []);
        CliAssertions.Equal(
            blanksAfterValue,
            0,
            $"[{JsonSerializer.Serialize(finalBlank)},2,[1]]\n",
            "");
    }

    [Fact]
    public async Task RawInputUsesFgetsPayloadAndUtf8TailBoundaries()
    {
        var c2Tail = PrefixAndSuffix(4_090, 0xc2, (byte)'\n', (byte)'z', (byte)'\n');
        var e2Tail = PrefixAndSuffix(4_090, 0xe2, (byte)'\n', (byte)'Z', (byte)'\n');
        var f0Tail = PrefixAndSuffix(
            4_090,
            0xf0,
            (byte)'\n',
            (byte)'Z',
            (byte)'Q',
            (byte)'\n');

        var c2Raw = await RunSubjectAsync(["-M", "-R", "-c", RawBoundaryFilter], c2Tail);
        CliAssertions.Equal(c2Raw, 0, "[0,false,4091,4093]\n[1,false,1,1]\n", "");

        var e2Raw = await RunSubjectAsync(["-M", "-R", "-c", RawBoundaryFilter], e2Tail);
        CliAssertions.Equal(e2Raw, 0, "[1,true,4093,4095]\n", "");

        var f0Raw = await RunSubjectAsync(["-M", "-R", "-c", RawBoundaryFilter], f0Tail);
        CliAssertions.Equal(f0Raw, 0, "[1,true,4094,4096]\n", "");

        var c2Slurp = await RunSubjectAsync(
            ["-M", "-R", "-c", "-s", RawSlurpBoundaryFilter],
            c2Tail);
        CliAssertions.Equal(c2Slurp, 0, "[1,3,4094,4096]\n", "");

        var e2Slurp = await RunSubjectAsync(
            ["-M", "-R", "-c", "-s", RawSlurpBoundaryFilter],
            e2Tail);
        CliAssertions.Equal(e2Slurp, 0, "[1,3,4094,4096]\n", "");

        var f0Slurp = await RunSubjectAsync(
            ["-M", "-R", "-c", "-s", RawSlurpBoundaryFilter],
            f0Tail);
        CliAssertions.Equal(f0Slurp, 0, "[1,3,4095,4097]\n", "");

        var exactPayload = PrefixAndSuffix(4_091);
        var exactPayloadResult = await RunSubjectAsync(
            ["-M", "-R", "-c", "[input_line_number,length,utf8bytelength]"],
            exactPayload);
        CliAssertions.Equal(exactPayloadResult, 0, "[0,4091,4091]\n", "");

        var completedC2Tail = PrefixAndSuffix(4_090, 0xc2, 0xa2);
        var completedC2TailResult = await RunSubjectAsync(
            ["-M", "-R", "-c", "[input_line_number,length,utf8bytelength]"],
            completedC2Tail);
        CliAssertions.Equal(completedC2TailResult, 0, "[0,4091,4092]\n", "");
    }

    [Fact]
    public async Task RawPartialRecordsAndUtf8RepairContinueAcrossFilesLikeJq()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var first = directory.File("first.txt");
        var empty = directory.File("empty.txt");
        var final = directory.File("final.txt");
        await File.WriteAllBytesAsync(first, "A"u8.ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(empty, [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(final, "B\nC"u8.ToArray(), TestContext.Current.CancellationToken);

        var joined = await RunSubjectAsync(
            ["-M", "-R", "-c", "[.,input_filename,input_line_number]", first, empty, final],
            []);
        CliAssertions.Equal(
            joined,
            0,
            $"[\"AB\",{JsonSerializer.Serialize(final)},1]\n" +
            $"[\"C\",{JsonSerializer.Serialize(final)},1]\n",
            "");

        var lead = directory.File("lead.txt");
        var continuation = directory.File("continuation.txt");
        await File.WriteAllBytesAsync(lead, [0xc2], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            continuation,
            [0xa2, (byte)'\n'],
            TestContext.Current.CancellationToken);

        var repairedSeparately = await RunSubjectAsync(
            [
                "-M",
                "-R",
                "-c",
                "[.,length,utf8bytelength,input_filename,input_line_number]",
                lead,
                continuation,
            ],
            []);
        CliAssertions.Equal(
            repairedSeparately,
            0,
            $"[\"��\",2,6,{JsonSerializer.Serialize(continuation)},1]\n",
            "");
    }

    [Fact]
    [Trait("Category", "OracleDifferential")]
    public async Task ReadMoreBoundaryCorpusMatchesPinnedJq182()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        var cases = new (string[] Arguments, byte[] Input)[]
        {
            (
                ["-M", "-c", "[.,(try input catch .),input_filename,input_line_number]"],
                "0\n\n"u8.ToArray()),
            (
                ["-M", "-R", "-c", RawBoundaryFilter],
                PrefixAndSuffix(4_090, 0xc2, (byte)'\n', (byte)'z', (byte)'\n')),
            (
                ["-M", "-R", "-c", RawBoundaryFilter],
                PrefixAndSuffix(4_090, 0xe2, (byte)'\n', (byte)'Z', (byte)'\n')),
            (
                ["-M", "-R", "-c", RawBoundaryFilter],
                PrefixAndSuffix(
                    4_090,
                    0xf0,
                    (byte)'\n',
                    (byte)'Z',
                    (byte)'Q',
                    (byte)'\n')),
            (
                ["-M", "-R", "-c", "-s", RawSlurpBoundaryFilter],
                PrefixAndSuffix(4_090, 0xc2, (byte)'\n', (byte)'z', (byte)'\n')),
            (
                ["-M", "-R", "-c", "-s", RawSlurpBoundaryFilter],
                PrefixAndSuffix(4_090, 0xe2, (byte)'\n', (byte)'Z', (byte)'\n')),
            (
                ["-M", "-R", "-c", "-s", RawSlurpBoundaryFilter],
                PrefixAndSuffix(
                    4_090,
                    0xf0,
                    (byte)'\n',
                    (byte)'Z',
                    (byte)'Q',
                    (byte)'\n')),
            (
                ["-M", "-R", "-c", "[input_line_number,length,utf8bytelength]"],
                PrefixAndSuffix(4_091)),
            (
                ["-M", "-R", "-c", "[input_line_number,length,utf8bytelength]"],
                PrefixAndSuffix(4_090, 0xc2, 0xa2)),
        };

        foreach (var (arguments, input) in cases)
        {
            var expected = await CliProcess.RunAsync(
                oracle,
                arguments,
                input,
                cancellationToken: TestContext.Current.CancellationToken);
            var actual = await RunSubjectAsync(arguments, input);
            CliAssertions.Equal(expected, actual);
        }

        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var one = directory.File("one.json");
        var finalEmpty = directory.File("final-empty.json");
        var rawFirst = directory.File("raw-first.txt");
        var rawEmpty = directory.File("raw-empty.txt");
        var rawFinal = directory.File("raw-final.txt");
        var utf8Lead = directory.File("utf8-lead.txt");
        var utf8Continuation = directory.File("utf8-continuation.txt");
        await File.WriteAllBytesAsync(one, "1\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(finalEmpty, [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(rawFirst, "A"u8.ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(rawEmpty, [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(rawFinal, "B\nC"u8.ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(utf8Lead, [0xc2], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            utf8Continuation,
            [0xa2, (byte)'\n'],
            TestContext.Current.CancellationToken);

        await AssertFileInvocationMatchesOracleAsync(
            oracle,
            ["-M", "-c", "-s", "[input_filename,input_line_number,.]", one, finalEmpty]);
        await AssertFileInvocationMatchesOracleAsync(
            oracle,
            [
                "-M",
                "-R",
                "-c",
                "[.,input_filename,input_line_number]",
                rawFirst,
                rawEmpty,
                rawFinal,
            ]);
        await AssertFileInvocationMatchesOracleAsync(
            oracle,
            [
                "-M",
                "-R",
                "-c",
                "[.,length,utf8bytelength,input_filename,input_line_number]",
                utf8Lead,
                utf8Continuation,
            ]);
    }

    private static Task<CliResult> RunSubjectAsync(string[] arguments, byte[] input) =>
        CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            arguments,
            input,
            cancellationToken: TestContext.Current.CancellationToken);

    private static async Task AssertFileInvocationMatchesOracleAsync(
        CliCommand oracle,
        string[] arguments)
    {
        var expected = await CliProcess.RunAsync(
            oracle,
            arguments,
            TestContext.Current.CancellationToken);
        var actual = await RunSubjectAsync(arguments, []);
        CliAssertions.Equal(expected, actual);
    }

    private static byte[] PrefixAndSuffix(int prefixLength, params byte[] suffix)
    {
        var result = new byte[prefixLength + suffix.Length];
        result.AsSpan(0, prefixLength).Fill((byte)'a');
        suffix.CopyTo(result, prefixLength);
        return result;
    }
}
