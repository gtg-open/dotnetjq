using System.IO.Compression;

if (args.Length != 2)
{
    Console.Error.WriteLine(
        "Usage: DotNetJq.DeterministicZip SOURCE_DIRECTORY DESTINATION.zip");
    return 2;
}

var sourceDirectory = Path.GetFullPath(args[0]);
var destination = Path.GetFullPath(args[1]);
if (!Directory.Exists(sourceDirectory))
{
    Console.Error.WriteLine($"Source directory does not exist: {sourceDirectory}");
    return 2;
}

if (File.Exists(destination) || Directory.Exists(destination))
{
    Console.Error.WriteLine($"Destination already exists: {destination}");
    return 2;
}

var sourcePrefix = sourceDirectory.TrimEnd(
    Path.DirectorySeparatorChar,
    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (destination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Destination must be outside the source directory.");
    return 2;
}

var files = Directory
    .EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
    .Select(path => new
    {
        Source = path,
        Entry = Path.GetRelativePath(sourceDirectory, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/'),
    })
    .OrderBy(file => file.Entry, StringComparer.Ordinal)
    .ToArray();

if (files.Length == 0)
{
    Console.Error.WriteLine("Source directory contains no files.");
    return 2;
}

Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
var fixedTimestamp = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
await using var output = new FileStream(
    destination,
    FileMode.CreateNew,
    FileAccess.ReadWrite,
    FileShare.None);
using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
{
    foreach (var file in files)
    {
        // Stored entries avoid zlib-version-dependent bytes. The sorted names,
        // fixed DOS timestamp, and fixed regular-file mode make the ZIP repeatable.
        var entry = archive.CreateEntry(file.Entry, CompressionLevel.NoCompression);
        entry.LastWriteTime = fixedTimestamp;
        entry.ExternalAttributes = file.Entry == "dotnetjq.exe"
            ? unchecked((int)0x81ED0000) // regular file, 0755
            : unchecked((int)0x81A40000); // regular file, 0644
        await using var source = new FileStream(
            file.Source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        await using var target = entry.Open();
        await source.CopyToAsync(target);
    }
}

await output.FlushAsync();
RecordUnixMetadataPlatform(output, files.Length);
return 0;

static void RecordUnixMetadataPlatform(FileStream archive, int expectedEntryCount)
{
    const uint endOfCentralDirectorySignature = 0x06054B50;
    const uint centralDirectoryHeaderSignature = 0x02014B50;
    const byte unixPlatform = 3;
    const int endOfCentralDirectoryLength = 22;
    const int centralDirectoryHeaderLength = 46;

    if (archive.Length < endOfCentralDirectoryLength)
    {
        throw new InvalidDataException("ZIP end-of-central-directory record is missing.");
    }

    Span<byte> end = stackalloc byte[endOfCentralDirectoryLength];
    var endOffset = archive.Length - end.Length;
    archive.Position = endOffset;
    archive.ReadExactly(end);
    if (ReadUInt32(end, 0) != endOfCentralDirectorySignature ||
        ReadUInt16(end, 4) != 0 ||
        ReadUInt16(end, 6) != 0 ||
        ReadUInt16(end, 8) != expectedEntryCount ||
        ReadUInt16(end, 10) != expectedEntryCount ||
        ReadUInt16(end, 20) != 0)
    {
        throw new InvalidDataException("ZIP central-directory metadata is unexpected.");
    }

    var centralDirectoryLength = ReadUInt32(end, 12);
    var centralDirectoryOffset = ReadUInt32(end, 16);
    if ((long)centralDirectoryOffset + centralDirectoryLength != endOffset)
    {
        throw new InvalidDataException("ZIP central-directory bounds are invalid.");
    }

    Span<byte> header = stackalloc byte[centralDirectoryHeaderLength];
    var nextHeaderOffset = (long)centralDirectoryOffset;
    for (var index = 0; index < expectedEntryCount; index++)
    {
        archive.Position = nextHeaderOffset;
        archive.ReadExactly(header);
        if (ReadUInt32(header, 0) != centralDirectoryHeaderSignature)
        {
            throw new InvalidDataException("ZIP central-directory entry is invalid.");
        }

        // ZipArchive records the current host in "version made by" even when
        // ExternalAttributes contains an explicit Unix file type and mode.
        // Normalize that host byte so those attributes have the same defined
        // interpretation on Windows and Unix builders.
        header[5] = unixPlatform;
        archive.Position = nextHeaderOffset;
        archive.Write(header);

        nextHeaderOffset += centralDirectoryHeaderLength +
            ReadUInt16(header, 28) +
            ReadUInt16(header, 30) +
            ReadUInt16(header, 32);
    }

    if (nextHeaderOffset != endOffset)
    {
        throw new InvalidDataException("ZIP central-directory entry count is invalid.");
    }

    static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
        (uint)(bytes[offset] |
            (bytes[offset + 1] << 8) |
            (bytes[offset + 2] << 16) |
            (bytes[offset + 3] << 24));
}
