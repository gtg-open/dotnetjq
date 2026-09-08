namespace DotNetJq.Tests;

public sealed class ConcurrencyArchitectureGuardTests
{
    [Fact]
    public void ValueCoreAndVmRemainFreeOfManagedSynchronizationPrimitives()
    {
        var repositoryRoot = FindRepositoryRoot();
        string[] sourceFiles =
        [
            "src/DotNetJq/Port/src/jv.c.cs",
            "src/DotNetJq/Port/src/execute.c.cs",
            "src/DotNetJq/Port/src/exec_stack.h.cs",
        ];

        foreach (var relativePath in sourceFiles)
        {
            var source = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
            Assert.False(
                source.Contains("Interlocked.", StringComparison.Ordinal),
                $"{relativePath} must keep jq's non-atomic ownership model.");
            Assert.False(
                source.Contains("Volatile.", StringComparison.Ordinal),
                $"{relativePath} must not add volatile synchronization to jq state.");
            Assert.False(
                source.Contains("CompareExchange", StringComparison.Ordinal),
                $"{relativePath} must not add compare/exchange synchronization to jq state.");
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(source, @"\block\s*\("),
                $"{relativePath} must not add a VM/value-state lock.");
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "DotNetJq.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the DotNetJq repository root.");
    }
}
