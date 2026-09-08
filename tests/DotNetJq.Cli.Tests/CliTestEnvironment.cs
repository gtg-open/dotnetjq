using System.Security.Cryptography;

namespace DotNetJq.Cli.Tests;

internal static class CliTestEnvironment
{
    private const string OracleEnvironmentVariable = "DOTNETJQ_JQ182";
    private const string SubjectEnvironmentVariable = "DOTNETJQ_CLI";
    private const string UpstreamEnvironmentVariable = "DOTNETJQ_UPSTREAM";
    private const string FullCompatibilityEnvironmentVariable =
        "DOTNETJQ_REQUIRE_FULL_COMPATIBILITY";
    private static readonly string[] ManagedAssemblyNames = ["DotNetJq.Cli", "dotnetjq"];
    private static readonly Dictionary<string, string> PinnedFixtureSha256 =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["jq.test"] = "329689763b651096989bd8260b643731083fc5fd17f6bd7834d158713f738cbd",
            ["man.test"] = "3c25698e94d5199c75b784cc361cda9c6775389eb61f8f4fb05fde1a757ae3f9",
            ["onig.test"] = "e82dab356709d4a5e4dfd8c71aced12ed1f42eb23208bac0eaf5e3f05bedef05",
            ["manonig.test"] = "62738dd27ce0dbc4f21296e48270f20024274631cae4b9ffe85a0eecaa53af7f",
            ["base64.test"] = "aec7a4812b4bdf937e77ba1261c5d51ece66308258d60ca2227b209a4398a64d",
            ["uri.test"] = "530958297b8eac04093e742756ab0ecd57de4364a38ac69fa7fed693c381ec39",
            ["optional.test"] = "06d7249e3d9e09d572995e66276cd65f2da6f907a15e2f9cb012e672598ac5b9",
        };
    private static readonly Dictionary<string, string> PinnedModuleSha256 =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a.jq"] = "7b7313916208fa1da942666fed434b86764e793e2f0bdc958af434bb25c2d9c7",
            ["b/b.jq"] = "ab098816baea1d1c80a461f0e1531badefde8ec3c61b9df9186a4860efee2fcd",
            ["c/c.jq"] = "cd046303810732db16a92a41f34d00ff1db7df62a2015fdd4651731e7cb5ba8e",
            ["c/d.jq"] = "f1f1e4e99cb7e90e3ede5c213fdc10240ca382c02fc146fc0298b4b4cb91e54c",
            ["cycle_a.jq"] = "163235bbfa8db8e6189207d338976e9c938f0ca463350e03685183a85386d28b",
            ["cycle_b.jq"] = "d250d077621822e8816230b8a8a4e4b0c574cbb8c8619ce22fd4bcde38df3e3b",
            ["cycle_self.jq"] = "b1f15cc427e16e90538ed34705e4389ca66cb57fb7f34203a17eff8f7b025577",
            ["data.json"] = "2792ffbd2efa54ed2c0d1fc2d49a15c2c78d66fc327de2c62e5d67517600b512",
            ["home1/.jq"] = "9af4419dd48334fece4fffd6e609fa959050dbcb4d0579e6df9d4b433d2dd435",
            ["home2/.jq/g.jq"] = "290ac9ccd65fbc3207f858afcb698b4c03214b6fa53584613f731257aeaed3cd",
            ["lib/jq/e/e.jq"] = "5a8139806897104ba640598676d1ceca16751c20cb2b470806f6ab58a588724a",
            ["lib/jq/f.jq"] = "5b17ccce65d6949daca26d51b078106954549830ee346a7d4473ea369218f0e6",
            ["shadow1.jq"] = "f81cd9dadecb4261b551897b5fe1928fb8801bd2e9edac87253bc3f96a8cfc1f",
            ["shadow2.jq"] = "9ca1a1064b6f2905b8e19982ce5c7d9b8faf550421bc404567fb1ea97021e06e",
            ["syntaxerror/syntaxerror.jq"] = "97a34c7388efcdc06458d4170e68f0d5466c5427be5f193f3dbf78ee8a30fc88",
            ["test_bind_order.jq"] = "27e7416b8f1f50b56b40e062c3dadd143dda941ee3c5da87b2225a9c5bf6f0d1",
            ["test_bind_order0.jq"] = "48bbb068b4be846c573367ff0e6eb877d14bff744888942dd0466ce193b9c495",
            ["test_bind_order1.jq"] = "9080d347b1e59e17080169d424468c4e86311b3218c9fbad9a16b848797f8aa5",
            ["test_bind_order2.jq"] = "6ba3c7b667796643474ac6f4ec2cb2cd4af6221c3152ea03df9497319cc4153f",
        };

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static CliCommand Subject
    {
        get
        {
            var subject = FindSubject(out var unavailableReason);
            return subject ?? CompatibilityUnavailable<CliCommand>(unavailableReason);
        }
    }

    public static async Task<CliCommand> RequireOracleAsync()
    {
        var oracle = FindOracle(out var unavailableReason);
        if (oracle is null)
        {
            return CompatibilityUnavailable<CliCommand>(unavailableReason);
        }

        CliResult version;
        try
        {
            version = await CliProcess.RunAsync(oracle, ["--version"]);
        }
        catch (Exception exception) when (exception is
            IOException or
            InvalidOperationException or
            TimeoutException or
            UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            return CompatibilityUnavailable<CliCommand>(
                $"Could not execute jq oracle '{oracle.FileName}': {exception.Message}");
        }

        if (version.ExitCode != 0 || version.StandardOutputText.Trim() != "jq-1.8.2")
        {
            return CompatibilityUnavailable<CliCommand>(
                $"{OracleEnvironmentVariable} must identify pinned jq 1.8.2; " +
                $"received exit {version.ExitCode}, stdout {Quote(version.StandardOutputText)}, " +
                $"stderr {Quote(version.StandardErrorText)}.");
        }

        return oracle;
    }

    public static string RequirePinnedUpstreamFixtures(IReadOnlyList<string> fixtureNames)
    {
        ArgumentNullException.ThrowIfNull(fixtureNames);
        var strict = IsFullCompatibilityRequired(
            Environment.GetEnvironmentVariable(FullCompatibilityEnvironmentVariable));
        var upstream = ResolveUpstreamCheckout(
            Environment.GetEnvironmentVariable(UpstreamEnvironmentVariable),
            strict,
            RepositoryRoot,
            out var resolutionFailure);
        if (upstream is null)
        {
            return CompatibilityUnavailable<string>(resolutionFailure!);
        }

        if (!TryValidatePinnedFixtures(upstream, fixtureNames, out var validationFailure))
        {
            return CompatibilityUnavailable<string>(validationFailure!);
        }

        return upstream;
    }

    public static TemporaryDirectory CreateTemporaryDirectory() => new();

    internal static bool IsFullCompatibilityRequired(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal);

    internal static string? ResolveUpstreamCheckout(
        string? configured,
        bool strict,
        string repositoryRoot,
        out string? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var fullPath = Path.GetFullPath(configured);
                if (Directory.Exists(fullPath))
                {
                    failure = null;
                    return fullPath;
                }

                failure = $"{UpstreamEnvironmentVariable} does not identify an existing " +
                    $"directory: '{fullPath}'.";
                return null;
            }
            catch (Exception exception) when (exception is
                ArgumentException or
                IOException or
                NotSupportedException or
                UnauthorizedAccessException)
            {
                failure = $"{UpstreamEnvironmentVariable} is not a valid path: {exception.Message}";
                return null;
            }
        }

        if (strict)
        {
            failure = $"Strict compatibility mode requires {UpstreamEnvironmentVariable} to " +
                "identify the pinned jq 1.8.2 checkout.";
            return null;
        }

        var checkout = Path.GetFullPath(Path.Combine(repositoryRoot, "upstream", "jq"));
        if (Directory.Exists(checkout))
        {
            failure = null;
            return checkout;
        }

        failure = $"Set {UpstreamEnvironmentVariable} to the pinned jq 1.8.2 checkout; " +
            $"local fallback '{checkout}' was not found.";
        return null;
    }

    internal static bool TryValidatePinnedFixtures(
        string upstream,
        IReadOnlyList<string> fixtureNames,
        out string? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upstream);
        ArgumentNullException.ThrowIfNull(fixtureNames);
        var testsRoot = Path.Combine(upstream, "tests");
        var requirements = new List<(string Path, string ExpectedSha256)>();
        var invalidNames = new List<string>();
        foreach (var name in fixtureNames.Distinct(StringComparer.Ordinal))
        {
            if (PinnedFixtureSha256.TryGetValue(name, out var expectedSha256))
                requirements.Add((Path.Combine(testsRoot, name), expectedSha256));
            else
                invalidNames.Add(name);
        }

        var modules = Path.Combine(testsRoot, "modules");
        var unexpectedModuleFiles = new List<string>();
        string? moduleInventoryFailure = null;
        if (Directory.Exists(modules))
        {
            requirements.AddRange(PinnedModuleSha256.Select(pair =>
                (Path.Combine(modules, pair.Key.Replace('/', Path.DirectorySeparatorChar)), pair.Value)));
            try
            {
                unexpectedModuleFiles.AddRange(
                    Directory.EnumerateFiles(modules, "*", SearchOption.AllDirectories)
                        .Select(path => Path.GetRelativePath(modules, path)
                            .Replace(Path.DirectorySeparatorChar, '/')
                            .Replace(Path.AltDirectorySeparatorChar, '/'))
                        .Where(path => !PinnedModuleSha256.ContainsKey(path))
                        .Order(StringComparer.Ordinal));
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException)
            {
                moduleInventoryFailure = exception.Message;
            }
        }

        var missing = requirements
            .Select(requirement => requirement.Path)
            .Where(path => !File.Exists(path))
            .ToList();
        if (!Directory.Exists(modules))
            missing.Add(modules);

        var mismatched = new List<string>();
        foreach (var requirement in requirements.Where(requirement => File.Exists(requirement.Path)))
        {
            if (!TryComputeSha256(requirement.Path, out var actualSha256, out var hashFailure))
            {
                mismatched.Add($"{requirement.Path} ({hashFailure})");
            }
            else if (!string.Equals(
                         requirement.ExpectedSha256,
                         actualSha256,
                         StringComparison.Ordinal))
            {
                mismatched.Add(
                    $"{requirement.Path} (expected {requirement.ExpectedSha256}, got {actualSha256})");
            }
        }

        if (invalidNames.Count == 0 &&
            missing.Count == 0 &&
            mismatched.Count == 0 &&
            unexpectedModuleFiles.Count == 0 &&
            moduleInventoryFailure is null)
        {
            failure = null;
            return true;
        }

        var problems = new List<string>();
        if (invalidNames.Count != 0)
        {
            problems.Add(
                "unrecognized fixture names: " + string.Join(", ", invalidNames.Select(Quote)));
        }

        if (missing.Count != 0)
        {
            problems.Add(
                "missing paths: " + string.Join(", ", missing.Select(Path.GetFullPath)));
        }

        if (mismatched.Count != 0)
            problems.Add("SHA-256 mismatches: " + string.Join(", ", mismatched));
        if (unexpectedModuleFiles.Count != 0)
        {
            problems.Add(
                "unexpected module files: " +
                string.Join(", ", unexpectedModuleFiles.Select(Quote)));
        }

        if (moduleInventoryFailure is not null)
            problems.Add("could not enumerate module fixtures: " + moduleInventoryFailure);

        failure = "The jq compatibility fixtures do not match pinned jq 1.8.2: " +
            string.Join("; ", problems);
        return false;
    }

    private static bool TryComputeSha256(
        string path,
        out string? sha256,
        out string? failure)
    {
        try
        {
            using var stream = File.OpenRead(path);
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException)
        {
            sha256 = null;
            failure = exception.Message;
            return false;
        }
    }

    private static CliCommand? FindSubject(out string unavailableReason)
    {
        var configured = Environment.GetEnvironmentVariable(SubjectEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return ResolveConfiguredSubject(configured, out unavailableReason);

        var copiedAssembly = FindNewestManagedCli(AppContext.BaseDirectory);
        if (copiedAssembly is not null)
        {
            unavailableReason = string.Empty;
            return CliCommand.ManagedAssembly(copiedAssembly);
        }

        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        var outputDirectory = Path.Combine(
            RepositoryRoot,
            "src",
            "DotNetJq.Cli",
            "bin",
            configuration,
            "net10.0");
        var builtAssembly = FindNewestManagedCli(outputDirectory);
        if (builtAssembly is not null)
        {
            unavailableReason = string.Empty;
            return CliCommand.ManagedAssembly(builtAssembly);
        }

        unavailableReason =
            "Could not locate the DotNetJq CLI. Build src/DotNetJq.Cli/DotNetJq.Cli.csproj " +
            $"or set {SubjectEnvironmentVariable} to a published dotnetjq executable.";
        return null;
    }

    internal static CliCommand? ResolveConfiguredSubject(
        string configured,
        out string unavailableReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configured);
        try
        {
            var fullPath = Path.GetFullPath(configured);
            if (!File.Exists(fullPath))
            {
                unavailableReason = $"{SubjectEnvironmentVariable} does not identify an " +
                    $"existing file: '{fullPath}'.";
                return null;
            }

            if (new FileInfo(fullPath).Length == 0)
            {
                unavailableReason = $"{SubjectEnvironmentVariable} identifies an empty file: " +
                    $"'{fullPath}'.";
                return null;
            }

            if (string.Equals(Path.GetExtension(fullPath), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                var runtimeConfig = Path.ChangeExtension(fullPath, ".runtimeconfig.json");
                if (!File.Exists(runtimeConfig))
                {
                    unavailableReason = $"Managed {SubjectEnvironmentVariable} subject " +
                        $"'{fullPath}' has no adjacent runtimeconfig.json.";
                    return null;
                }

                unavailableReason = string.Empty;
                return CliCommand.ManagedAssembly(fullPath);
            }

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(fullPath);
                const UnixFileMode execute =
                    UnixFileMode.UserExecute |
                    UnixFileMode.GroupExecute |
                    UnixFileMode.OtherExecute;
                if ((mode & execute) == 0)
                {
                    unavailableReason = $"{SubjectEnvironmentVariable} is not executable: " +
                        $"'{fullPath}'.";
                    return null;
                }
            }

            unavailableReason = string.Empty;
            return CliCommand.Executable(fullPath);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            NotSupportedException or
            UnauthorizedAccessException)
        {
            unavailableReason = $"{SubjectEnvironmentVariable} is invalid: {exception.Message}";
            return null;
        }
    }

    private static string? FindNewestManagedCli(string directory) =>
        ManagedAssemblyNames
            .Select(name => new
            {
                Assembly = Path.Combine(directory, name + ".dll"),
                RuntimeConfig = Path.Combine(directory, name + ".runtimeconfig.json"),
            })
            .Where(candidate => File.Exists(candidate.Assembly) && File.Exists(candidate.RuntimeConfig))
            .OrderByDescending(candidate => File.GetLastWriteTimeUtc(candidate.RuntimeConfig))
            .Select(candidate => candidate.Assembly)
            .FirstOrDefault();

    private static CliCommand? FindOracle(out string unavailableReason)
    {
        var configured = Environment.GetEnvironmentVariable(OracleEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var fullPath = Path.GetFullPath(configured);
                if (File.Exists(fullPath))
                {
                    unavailableReason = string.Empty;
                    return CreateOracleCommand(fullPath);
                }

                unavailableReason = $"{OracleEnvironmentVariable} does not identify an existing " +
                    $"file: '{fullPath}'.";
                return null;
            }
            catch (Exception exception) when (exception is
                ArgumentException or
                IOException or
                NotSupportedException or
                UnauthorizedAccessException)
            {
                unavailableReason =
                    $"{OracleEnvironmentVariable} is not a valid path: {exception.Message}";
                return null;
            }
        }

        foreach (var name in OperatingSystem.IsWindows()
                     ? new[] { "jq.exe", "jq" }
                     : new[] { "jq", "jq.exe" })
        {
            var releaseOracle = Path.Combine(RepositoryRoot, "artifacts", "test-assets", "jq-1.8.2", "oracle", name);
            if (File.Exists(releaseOracle))
            {
                unavailableReason = string.Empty;
                return CreateOracleCommand(releaseOracle);
            }
        }

        var sourceCheckout = Path.Combine(RepositoryRoot, "upstream", "jq");
        var executableNames = OperatingSystem.IsWindows()
            ? new[] { "jq.exe", "jq" }
            : new[] { "jq", "jq.exe" };
        foreach (var name in executableNames)
        {
            foreach (var relativePath in new[] { Path.Combine(".libs", name), name })
            {
                var candidate = Path.Combine(sourceCheckout, relativePath);
                if (File.Exists(candidate))
                {
                    unavailableReason = string.Empty;
                    return CreateOracleCommand(candidate);
                }
            }
        }

        unavailableReason =
            $"Set {OracleEnvironmentVariable} to the jq executable built from pinned commit " +
            "34f7186b86743a083a589741b6cea95293524108 to run differential CLI tests.";
        return null;
    }

    private static CliCommand CreateOracleCommand(string path) =>
        OperatingSystem.IsWindows()
            ? new CliCommand(path, ["-b"])
            : CliCommand.Executable(path);

    private static T CompatibilityUnavailable<T>(string message)
    {
        if (IsFullCompatibilityRequired(
                Environment.GetEnvironmentVariable(FullCompatibilityEnvironmentVariable)))
        {
            Assert.Fail(message);
        }

        Assert.Skip(message);
        throw new InvalidOperationException("xUnit compatibility disposition did not terminate the test.");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "DotNetJq.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the DotNetJq repository root.");
    }

    private static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dotnetjq-cli-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relativePath)
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A failed test should not be hidden by best-effort fixture cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Windows may briefly retain a process file handle after exit.
        }
    }
}
