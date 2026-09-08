using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace DotNetJq.Tests;

public sealed partial class VersionMetadataTests
{
    [Fact]
    public void PublicCompatibilityIdentityMatchesAssemblyMetadata()
    {
        var assembly = typeof(JqProgram).Assembly;
        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);

        Assert.Equal("1.8.2", DotNetJqVersionInfo.CompatibleJqVersion);
        Assert.Equal(
            "34f7186b86743a083a589741b6cea95293524108",
            DotNetJqVersionInfo.CompatibleJqCommit);
        Assert.Equal(
            DotNetJqVersionInfo.CompatibleJqVersion,
            metadata["JqCompatibilityVersion"]);
        Assert.Equal(
            DotNetJqVersionInfo.CompatibleJqCommit,
            metadata["JqCompatibilityCommit"]);
    }

    [Fact]
    public void AssemblyAndBuildVersionsHaveTheDeclaredShape()
    {
        var assembly = typeof(JqProgram).Assembly;
        var assemblyName = assembly.GetName();
        var fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);

        Assert.Equal(new Version(1, 0, 0, 0), assemblyName.Version);
        Assert.Matches(FileVersionPattern(), Assert.IsType<string>(fileVersion));
        Assert.Matches(
            InformationalVersionPattern(),
            Assert.IsType<string>(informationalVersion));
        Assert.Matches(BuildNumberPattern(), metadata["DotNetJqBuildNumber"]);

        if (metadata.TryGetValue("DotNetJqSourceRevision", out var revision))
        {
            Assert.Matches(SourceRevisionPattern(), revision);
            Assert.EndsWith($".sha.{revision}", informationalVersion, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"^1\.0\.0\.(?:0|[1-9][0-9]{0,4})$")]
    private static partial Regex FileVersionPattern();

    [GeneratedRegex(@"^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\+jq\.1\.8\.2\.build\.(?:0|[1-9][0-9]{0,4})(?:\.sha\.[0-9a-f]{40})?$")]
    private static partial Regex InformationalVersionPattern();

    [GeneratedRegex(@"^(?:0|[1-9][0-9]{0,4})$")]
    private static partial Regex BuildNumberPattern();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex SourceRevisionPattern();
}
