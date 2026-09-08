namespace DotNetJq;

/// <summary>Describes the upstream jq release implemented by this DotNetJq build.</summary>
public static class DotNetJqVersionInfo
{
    /// <summary>Gets the compatible upstream jq release.</summary>
    public const string CompatibleJqVersion = "1.8.2";

    /// <summary>Gets the exact upstream jq commit used as the semantic source of truth.</summary>
    public const string CompatibleJqCommit = "34f7186b86743a083a589741b6cea95293524108";
}
