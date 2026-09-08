using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using DotNetJq.Port;

namespace DotNetJq.Tests;

/// <summary>
/// Frozen jq-1.8.2 exact binary64 corpus for the managed Sun fdlibm elementary kernels.
/// The jq executable was used only to generate the compressed expected bit patterns.
/// <c>tools/generate_fdlibm_elementary_corpus.py --check</c> reconstructs every input,
/// verifies the pinned oracle version/SHA, and checks the deterministic gzip payload.
/// Public jq serialization matrices are kept separately in
/// <see cref="FdlibmElementaryCompatibilityTests"/>; this corpus compares kernel result bits.
/// </summary>
public sealed class FdlibmElementaryExactOracleCorpusTests
{
    private const int ExpectedCaseCount = 2596;
    private const string ExpectedInputSha256 = "38abae8cd999583f386a18a9f34b0c48a1fa2faf4cf6190caf645ea3d888b411";
    private const string ExpectedOutputSha256 = "c01d7a7915c31e9af5f197264926192bd7dfb724163ef6f53fd9528bbe754d87";
    private const string ExpectedPairBlobSha256 = "6a20a1decbd074c2accc5cb94a813757471269495560fa35de1711092472b161";

    private static readonly FunctionCorpus[] Functions =
    [
        new("acosh", 411, libjq.jq_acosh),
        new("asinh", 427, libjq.jq_asinh),
        new("atanh", 415, libjq.jq_atanh),
        new("expm1", 510, libjq.jq_expm1),
        new("log1p", 833, libjq.jq_log1p),
    ];

    [Fact]
    public void ManagedFdlibmElementaryKernelsMatchFrozenJqOracleBitsExactly()
    {
        var compressed = Convert.FromBase64String(ExpectedGzipBase64);
        using var compressedStream = new MemoryStream(compressed);
        using var gzip = new GZipStream(compressedStream, CompressionMode.Decompress);
        using var pairStream = new MemoryStream();
        gzip.CopyTo(pairStream);
        var pairBytes = pairStream.ToArray();

        Assert.Equal(ExpectedCaseCount * 2 * sizeof(ulong), pairBytes.Length);
        Assert.Equal(ExpectedPairBlobSha256, Sha256(pairBytes));

        using var inputHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var outputHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var reader = new BinaryReader(new MemoryStream(pairBytes));
        var hashRecord = new byte[1 + sizeof(ulong)];
        var completed = 0;

        for (var functionIndex = 0; functionIndex < Functions.Length; functionIndex++)
        {
            var function = Functions[functionIndex];
            for (var caseIndex = 0; caseIndex < function.Count; caseIndex++)
            {
                var inputBits = reader.ReadUInt64();
                var expectedBits = reader.ReadUInt64();
                var input = BitConverter.UInt64BitsToDouble(inputBits);
                var actual = function.Operation(input);
                var actualBits = BitConverter.DoubleToUInt64Bits(actual);

                Assert.True(
                    expectedBits == actualBits,
                    $"{function.Name} corpus case {caseIndex}: input={input:R} " +
                    $"(0x{inputBits:x16}), expected=0x{expectedBits:x16}, actual=0x{actualBits:x16}.");

                hashRecord[0] = (byte)functionIndex;
                BinaryPrimitives.WriteUInt64LittleEndian(hashRecord.AsSpan(1), inputBits);
                inputHash.AppendData(hashRecord);
                BinaryPrimitives.WriteUInt64LittleEndian(hashRecord.AsSpan(1), expectedBits);
                outputHash.AppendData(hashRecord);
                completed++;
            }
        }

        Assert.Equal(pairBytes.Length, reader.BaseStream.Position);
        Assert.Equal(ExpectedCaseCount, completed);
        Assert.Equal(ExpectedInputSha256, Hex(inputHash.GetHashAndReset()));
        Assert.Equal(ExpectedOutputSha256, Hex(outputHash.GetHashAndReset()));
    }

    private static string Sha256(byte[] value) => Hex(SHA256.HashData(value));

    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

    private sealed record FunctionCorpus(string Name, int Count, Func<double, double> Operation);

    private const string ExpectedGzipBase64 =
        "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" +
        "OECiAAA=";
}
