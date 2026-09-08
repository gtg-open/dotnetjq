using System.Text;
using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;

namespace DotNetJq.Cli.Tests;

public sealed class CliFileAndModuleTests
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task UnicodeAndSpaceBearingFilenamesAreReadInArgumentOrder()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var first = directory.File("one value.json");
        var second = directory.File("δεύτερο-😎.json");
        await File.WriteAllTextAsync(first, "{\"source\":1}", Utf8NoBom, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "{\"source\":2}", Utf8NoBom, TestContext.Current.CancellationToken);

        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-c", ".", first, second],
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(result, 0, "{\"source\":1}\n{\"source\":2}\n");
    }

    [Fact]
    public async Task JsonParserStateContinuesAcrossFileBoundaries()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var first = directory.File("first.json");
        var second = directory.File("second.json");
        await File.WriteAllTextAsync(first, "[1,", Utf8NoBom, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "2]", Utf8NoBom, TestContext.Current.CancellationToken);

        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-c", ".", first, second],
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(result, 0, "[1,2]\n");
    }

    [Fact]
    public async Task NumericTokenContinuesAcrossFileBoundaries()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var first = directory.File("first-number.json");
        var second = directory.File("second-number.json");
        await File.WriteAllTextAsync(first, "1", Utf8NoBom, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "2", Utf8NoBom, TestContext.Current.CancellationToken);

        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-c", ".", first, second],
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(result, 0, "12\n");
    }

    [Fact]
    public async Task StdinMarkerParticipatesAtItsFilenamePosition()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var first = directory.File("first.json");
        var third = directory.File("third.json");
        await File.WriteAllTextAsync(first, "1\n", Utf8NoBom, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(third, "3\n", Utf8NoBom, TestContext.Current.CancellationToken);

        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-c", ".", first, "-", third],
            "2\n",
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(result, 0, "1\n2\n3\n");
    }

    [Fact]
    public async Task OptionTerminatorAllowsOptionShapedFilterFilename()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var filter = directory.File("-filter.jq");
        await File.WriteAllTextAsync(filter, "{answer: 40 + 2}", Utf8NoBom, TestContext.Current.CancellationToken);

        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-ncf", "--", "-filter.jq"],
            workingDirectory: directory.Path,
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(result, 0, "{\"answer\":42}\n");
    }

    [Fact]
    public async Task FromFileLoadsFilterTextFromSpaceBearingFilename()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var filter = directory.File(Path.Combine("program origin", "main.jq"));
        var input = directory.File("input.json");
        await File.WriteAllTextAsync(filter, ". + 1", Utf8NoBom, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(input, "41", Utf8NoBom, TestContext.Current.CancellationToken);

        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-c", "-f", filter, input],
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(result, 0, "42\n");
    }

    [Fact]
    public async Task LibraryPathWithSpacesAndUnicodeLoadsModules()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var moduleDirectory = Path.Combine(directory.Path, "modules μ");
        Directory.CreateDirectory(moduleDirectory);
        var module = Path.Combine(moduleDirectory, "helper.jq");
        await File.WriteAllTextAsync(module, "def helper: 42;", Utf8NoBom, TestContext.Current.CancellationToken);

        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-n", "-c", "-L", Path.GetDirectoryName(module)!, "include \"helper\"; helper"],
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(result, 0, "42\n");
    }

    [Fact]
    public async Task SlurpfileAndRawfilePreserveTheirDistinctRepresentations()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var data = directory.File("data file.json");
        const string contents = "{\"n\":1}\n{\"n\":2}\n";
        await File.WriteAllTextAsync(data, contents, Utf8NoBom, TestContext.Current.CancellationToken);

        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-n", "-c", "--slurpfile", "foo", data, "--rawfile", "bar", data, "{$foo, $bar}"],
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(
            result,
            0,
            "{\"foo\":[{\"n\":1},{\"n\":2}],\"bar\":\"{\\\"n\\\":1}\\n{\\\"n\\\":2}\\n\"}\n");

        AssertProgramArgumentUsesSingleFileSnapshot(
            CliArgumentKind.RawFile,
            "first snapshot"u8.ToArray(),
            "replacement snapshot"u8.ToArray());
        AssertProgramArgumentUsesSingleFileSnapshot(
            CliArgumentKind.SlurpFile,
            "1\n"u8.ToArray(),
            "not-json"u8.ToArray());
    }

    [Fact]
    public async Task BinaryOptionDoesNotChangeRawNamedFileBytes()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var data = directory.File("crlf-and-control-z.txt");
        await File.WriteAllBytesAsync(
            data,
            [(byte)'a', (byte)'\r', (byte)'\n', (byte)'b', 0x1a, (byte)'c'],
            TestContext.Current.CancellationToken);
        var expected = "\"a\\r\\nb\\u001ac\"\n"u8.ToArray();

        var byDefault = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-R", "-s", "-c", ".", data],
            cancellationToken: TestContext.Current.CancellationToken);
        var explicitlyBinary = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-R", "-s", "-c", "-b", ".", data],
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(byDefault, 0, expected, []);
        CliAssertions.Equal(explicitlyBinary, 0, expected, []);
    }

    [Fact]
    public async Task DuplicateFileArgumentDoesNotReadItsInvalidReplacement()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var data = directory.File("data.json");
        var absent = directory.File("does-not-exist.json");
        await File.WriteAllTextAsync(data, "1\n2\n", Utf8NoBom, TestContext.Current.CancellationToken);

        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-n", "-c", "--slurpfile", "values", data, "--slurpfile", "values", absent, "$values"],
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(result, 0, "[1,2]\n");
    }

    [Fact]
    public async Task NullInputDoesNotOpenTrailingInputFilenames()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var absent = directory.File("does-not-exist.json");

        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-n", "-c", ".", absent],
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(result, 0, "null\n");
    }

    [Fact]
    public async Task DefaultHomeJqDefinitionsAreLoaded()
    {
        Assert.Null(JqCliApplication.ResolveHome(
            new Dictionary<string, string> { ["USERPROFILE"] = "ignored-on-posix" },
            isWindows: false));
        Assert.Equal(
            "explicit-home",
            JqCliApplication.ResolveHome(
                new Dictionary<string, string>
                {
                    ["HOME"] = "explicit-home",
                    ["USERPROFILE"] = "profile-home",
                    ["HOMEDRIVE"] = "D:",
                    ["HOMEPATH"] = "\\path-home",
                },
                isWindows: true));
        Assert.Equal(
            "profile-home",
            JqCliApplication.ResolveHome(
                new Dictionary<string, string>
                {
                    ["USERPROFILE"] = "profile-home",
                    ["HOMEDRIVE"] = "D:",
                    ["HOMEPATH"] = "\\path-home",
                },
                isWindows: true));
        Assert.Equal(
            "D:\\path-home",
            JqCliApplication.ResolveHome(
                new Dictionary<string, string>
                {
                    ["HOMEDRIVE"] = "D:",
                    ["HOMEPATH"] = "\\path-home",
                },
                isWindows: true));
        Assert.Equal(
            "\\path-home",
            JqCliApplication.ResolveHome(
                new Dictionary<string, string> { ["HOMEPATH"] = "\\path-home" },
                isWindows: true));

        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var home = directory.File("home/.jq");
        await File.WriteAllTextAsync(home, "def home_definition: \"loaded\";", Utf8NoBom, TestContext.Current.CancellationToken);

        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-n", "-r", "home_definition"],
            environment: new Dictionary<string, string?> { ["HOME"] = Path.GetDirectoryName(home)! },
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(result, 0, "loaded\n");

        if (OperatingSystem.IsWindows())
        {
            var homeDirectory = Path.GetDirectoryName(home)!;
            var homeDrive = Path.GetPathRoot(homeDirectory)!
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var homePath = homeDirectory[homeDrive.Length..];
            var fallbackResult = await CliProcess.RunAsync(
                CliTestEnvironment.Subject,
                ["-n", "-r", "home_definition"],
                environment: new Dictionary<string, string?>
                {
                    ["HOME"] = null,
                    ["USERPROFILE"] = null,
                    ["HOMEDRIVE"] = homeDrive,
                    ["HOMEPATH"] = homePath,
                },
                cancellationToken: TestContext.Current.CancellationToken);

            CliAssertions.Equal(fallbackResult, 0, "loaded\n");
        }
    }

    private static void AssertProgramArgumentUsesSingleFileSnapshot(
        CliArgumentKind kind,
        ReadOnlyMemory<byte> firstContents,
        ReadOnlyMemory<byte> replacementContents)
    {
        const string path = "/mutable/argument.data";
        const string name = "snapshot";
        var fileSystem = new MutableCountingFileSystem(firstContents, replacementContents);
        var options = new CliOptions();
        options.NamedArguments.Add(new CliNamedArgument(name, path, kind));

        var programArguments = JqCliApplication.BuildProgramArguments(options, fileSystem);
        try
        {
            Assert.Equal(1, fileSystem.ReadCount);
            var value = libjq.jv_object_get(libjq.jv_copy(programArguments), name);
            try
            {
                if (kind == CliArgumentKind.RawFile)
                {
                    Assert.Equal("first snapshot", value.StringValue);
                }
                else
                {
                    Assert.Single(value.ArrayValue);
                    Assert.Equal(1, value.ArrayValue[0].NumberValue);
                }
            }
            finally
            {
                libjq.jv_free(value);
            }
        }
        finally
        {
            libjq.jv_free(programArguments);
        }
    }

    private sealed class MutableCountingFileSystem(
        ReadOnlyMemory<byte> firstContents,
        ReadOnlyMemory<byte> replacementContents) : IJqFileSystem
    {
        internal int ReadCount { get; private set; }

        public JqFileReadResult ReadFile(string path)
        {
            var contents = ReadCount++ == 0 ? firstContents : replacementContents;
            return JqFileReadResult.Success(path, contents);
        }
    }
}
