using System.Collections.Concurrent;
using System.Reflection;
using DotNetJq.Compatibility.Regex;

namespace DotNetJq.Tests;

[Collection(RegexStaticStateGroup.Name)]
public sealed class RegexUnicodePropertyCacheConcurrencyTests
{
    private const string CacheFieldName = "UnicodePropertyPatternCache";
    private const string ResolverMethodName = "ScalarPropertyPattern";

    [Fact]
    public void ConcurrentSameKeyResolutionPublishesOneStableCachedPattern()
    {
        var cache = GetCache();
        const string normalizedName = "INMAHJONGTILES";
        cache.TryRemove(normalizedName, out _);
        var countBefore = cache.Count;
        var patterns = new string?[64];

        Parallel.For(
            0,
            patterns.Length,
            index => patterns[index] = ResolvePropertyPattern(normalizedName));

        Assert.Equal(countBefore + 1, cache.Count);
        var published = Assert.IsType<string>(cache[normalizedName]);
        Assert.All(patterns, pattern => Assert.Same(published, pattern));
    }

    [Fact]
    public void ArbitraryInvalidPropertyNamesDoNotGrowTheCache()
    {
        var cache = GetCache();
        var countBefore = cache.Count;

        for (var index = 0; index < 1_000; index++)
        {
            Assert.Null(ResolvePropertyPattern($"DOTNETJQ_INVALID_PROPERTY_{index}"));
        }

        Assert.Equal(countBefore, cache.Count);
    }

    private static ConcurrentDictionary<string, string> GetCache()
    {
        var field = typeof(JqRegex).GetField(
            CacheFieldName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return Assert.IsType<ConcurrentDictionary<string, string>>(field.GetValue(null));
    }

    private static string? ResolvePropertyPattern(string name)
    {
        var method = typeof(JqRegex).GetMethod(
            ResolverMethodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method.Invoke(null, [name]);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RegexStaticStateGroup
{
    internal const string Name = nameof(RegexStaticStateGroup);
}
