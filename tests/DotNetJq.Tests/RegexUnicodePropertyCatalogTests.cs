using DotNetJq.Compatibility.Regex;

namespace DotNetJq.Tests;

public sealed class RegexUnicodePropertyCatalogTests
{
    [Theory]
    [InlineData("\u0080", "\u0000", "In_Latin_1_Supplement")]
    [InlineData("\U0001E900", "\u0000", "inadlam")]
    [InlineData("\u2FE0", "\u0000", "innoblock")]
    [InlineData("\U00030000", "\u0000", "incjkunifiedideographsextensiong")]
    [InlineData("\u0600", "\u0700", "inarabic")]
    [InlineData("\u0400", "A", "cyrl")]
    [InlineData("\u00A0", "A", "whitespace")]
    [InlineData("\u0345", "\u0000", "Alpha")]
    [InlineData("\u00BD", "-", "Word")]
    public void CanonicalAndAliasPropertiesUseTheirPinnedOnigurumaRanges(
        string member,
        string nonmember,
        string property)
    {
        Assert.True(JqRegex.Test(member, $"\\p{{{property}}}"));
        Assert.False(JqRegex.Test(nonmember, $"\\p{{{property}}}"));
    }
}
