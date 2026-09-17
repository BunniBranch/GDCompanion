using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace GrimDawnCompanion.Core;

/// <summary>Read-only reader for Grim Dawn ARC v3 archives.</summary>
public static class ArcReader
{
    private const int HeaderSize = 28;
    private const int RecordSize = 12;
    private const int TocSize = 44;

    public static Dictionary<string, string> ReadTags(string arcPath)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var stream = File.Open(arcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var header = reader.ReadBytes(HeaderSize);
        if (header.Length != HeaderSize || Encoding.ASCII.GetString(header, 0, 4) != "ARC\0")
            throw new InvalidDataException($"Unsupported ARC header: {arcPath}");
        var version = U32(header, 4);
        if (version != 3) throw new InvalidDataException($"Unsupported ARC version {version}: {arcPath}");

        var fileCount = checked((int)U32(header, 8));
        var recordCount = checked((int)U32(header, 12));
        var stringSize = checked((int)U32(header, 20));
        var recordOffset = U32(header, 24);
        stream.Position = recordOffset;

        var records = new Part[recordCount];
        for (var i = 0; i < records.Length; i++)
        {
            var raw = reader.ReadBytes(RecordSize);
            records[i] = new Part(U32(raw, 0), checked((int)U32(raw, 4)), checked((int)U32(raw, 8)));
        }
        var strings = reader.ReadBytes(stringSize);
        var toc = new Toc[fileCount];
        for (var i = 0; i < toc.Length; i++)
        {
            var raw = reader.ReadBytes(TocSize);
            toc[i] = new Toc(
                checked((int)U32(raw, 28)), checked((int)U32(raw, 32)),
                checked((int)U32(raw, 36)), checked((int)U32(raw, 40)),
                checked((int)U32(raw, 12)));
        }

        foreach (var entry in toc)
        {
            if (entry.NameOffset < 0 || entry.NameLength < 0 || entry.NameOffset + entry.NameLength > strings.Length) continue;
            var name = Decode(strings.AsSpan(entry.NameOffset, entry.NameLength));
            if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || !name.Contains("tag", StringComparison.OrdinalIgnoreCase)) continue;
            var content = ReadEntry(stream, records, entry);
            ParseTagText(content, tags);
        }
        return tags;
    }

    /// <summary>Reads selected entries without extracting or modifying the source archive.</summary>
    public static Dictionary<string, byte[]> ReadEntries(string arcPath, Func<string, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        using var stream = File.Open(arcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var (_, records, strings, toc) = ReadIndex(stream, reader, arcPath);
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in toc)
        {
            if (entry.NameOffset < 0 || entry.NameLength < 0 || entry.NameOffset + entry.NameLength > strings.Length) continue;
            var name = Decode(strings.AsSpan(entry.NameOffset, entry.NameLength)).Replace('\\', '/');
            if (predicate(name)) result[name] = ReadEntry(stream, records, entry);
        }
        return result;
    }

    public static Dictionary<string, string> ReadTextEntries(string arcPath, Func<string, bool> predicate) =>
        ReadEntries(arcPath, predicate).ToDictionary(x => x.Key, x => DecodeText(x.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>Streams the first matching entry without imposing the CLR single-array size limit.</summary>
    public static string CopyFirstEntryToStream(string arcPath, Func<string, bool> predicate, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(destination);
        using var stream = File.Open(arcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var (_, records, strings, toc) = ReadIndex(stream, reader, arcPath);
        foreach (var entry in toc)
        {
            if (entry.NameOffset < 0 || entry.NameLength < 0 || entry.NameOffset + entry.NameLength > strings.Length) continue;
            var name = Decode(strings.AsSpan(entry.NameOffset, entry.NameLength)).Replace('\\', '/');
            if (!predicate(name)) continue;
            CopyEntry(stream, records, entry, destination);
            return name;
        }
        throw new InvalidDataException($"No matching entry was found in {arcPath}.");
    }

    private static (int FileCount, Part[] Records, byte[] Strings, Toc[] Toc) ReadIndex(Stream stream, BinaryReader reader, string arcPath)
    {
        stream.Position = 0;
        var header = reader.ReadBytes(HeaderSize);
        if (header.Length != HeaderSize || Encoding.ASCII.GetString(header, 0, 4) != "ARC\0")
            throw new InvalidDataException($"Unsupported ARC header: {arcPath}");
        var version = U32(header, 4);
        if (version != 3) throw new InvalidDataException($"Unsupported ARC version {version}: {arcPath}");

        var fileCount = checked((int)U32(header, 8));
        var recordCount = checked((int)U32(header, 12));
        var stringSize = checked((int)U32(header, 20));
        var recordOffset = U32(header, 24);
        stream.Position = recordOffset;
        var records = new Part[recordCount];
        for (var i = 0; i < records.Length; i++)
        {
            var raw = reader.ReadBytes(RecordSize);
            if (raw.Length != RecordSize) throw new InvalidDataException("ARC record table is truncated.");
            records[i] = new Part(U32(raw, 0), checked((int)U32(raw, 4)), checked((int)U32(raw, 8)));
        }
        var strings = reader.ReadBytes(stringSize);
        if (strings.Length != stringSize) throw new InvalidDataException("ARC string table is truncated.");
        var toc = new Toc[fileCount];
        for (var i = 0; i < toc.Length; i++)
        {
            var raw = reader.ReadBytes(TocSize);
            if (raw.Length != TocSize) throw new InvalidDataException("ARC table of contents is truncated.");
            toc[i] = new Toc(
                checked((int)U32(raw, 28)), checked((int)U32(raw, 32)),
                checked((int)U32(raw, 36)), checked((int)U32(raw, 40)),
                U32(raw, 12));
        }
        return (fileCount, records, strings, toc);
    }

    private static byte[] ReadEntry(Stream stream, Part[] records, Toc toc)
    {
        if (toc.OutputSize > int.MaxValue) throw new InvalidDataException("ARC entry is too large for an in-memory read; use CopyFirstEntryToStream.");
        using var output = new MemoryStream((int)toc.OutputSize);
        CopyEntry(stream, records, toc, output);
        return output.ToArray();
    }

    private static void CopyEntry(Stream stream, Part[] records, Toc toc, Stream output)
    {
        var initialPosition = output.CanSeek ? output.Position : 0;
        for (var i = toc.FirstPart; i < toc.FirstPart + toc.PartCount; i++)
        {
            if ((uint)i >= records.Length) throw new InvalidDataException("ARC part index is outside the record table.");
            var part = records[i];
            stream.Position = part.Offset;
            var input = new byte[part.CompressedSize];
            stream.ReadExactly(input);
            if (part.CompressedSize == part.OutputSize) output.Write(input);
            else output.Write(Decompress(input, part.OutputSize));
        }
        if (output.CanSeek && output.Position - initialPosition != toc.OutputSize)
            throw new InvalidDataException("ARC entry size does not match its parts.");
    }

    private static byte[] Decompress(byte[] input, int expected)
    {
        try { return Lz4Block(input, expected); }
        catch (InvalidDataException)
        {
            try
            {
                using var source = new MemoryStream(input);
                using var deflate = new DeflateStream(source, CompressionMode.Decompress);
                using var output = new MemoryStream(expected);
                deflate.CopyTo(output);
                if (output.Length != expected) throw new InvalidDataException("Unexpected deflate output size.");
                return output.ToArray();
            }
            catch (Exception ex) { throw new InvalidDataException("ARC block is neither valid LZ4 nor deflate data.", ex); }
        }
    }

    private static byte[] Lz4Block(ReadOnlySpan<byte> source, int expected)
    {
        var output = new byte[expected];
        var si = 0;
        var di = 0;
        while (si < source.Length)
        {
            var token = source[si++];
            var literal = token >> 4;
            if (literal == 15)
            {
                byte extra;
                do { if (si >= source.Length) throw new InvalidDataException(); extra = source[si++]; literal += extra; } while (extra == 255);
            }
            if (si + literal > source.Length || di + literal > output.Length) throw new InvalidDataException();
            source.Slice(si, literal).CopyTo(output.AsSpan(di));
            si += literal;
            di += literal;
            if (si >= source.Length) break;
            if (si + 2 > source.Length) throw new InvalidDataException();
            var distance = source[si] | source[si + 1] << 8;
            si += 2;
            if (distance <= 0 || distance > di) throw new InvalidDataException();
            var length = (token & 0x0f) + 4;
            if ((token & 0x0f) == 15)
            {
                byte extra;
                do { if (si >= source.Length) throw new InvalidDataException(); extra = source[si++]; length += extra; } while (extra == 255);
            }
            if (di + length > output.Length) throw new InvalidDataException();
            for (var j = 0; j < length; j++) output[di + j] = output[di + j - distance];
            di += length;
        }
        if (di != expected) throw new InvalidDataException($"LZ4 output was {di} bytes; expected {expected}.");
        return output;
    }

    private static void ParseTagText(byte[] bytes, IDictionary<string, string> tags)
    {
        var text = DecodeText(bytes);
        using var lines = new StringReader(text);
        while (lines.ReadLine() is { } line)
        {
            line = line.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#' || line[0] == '/') continue;
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim().Replace("{^n}", " ");
            if (key.Length > 0 && value.Length > 0) tags[key] = value;
        }
    }

    private static string DecodeText(byte[] value)
    {
        if (value.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) return Encoding.UTF8.GetString(value, 3, value.Length - 3);
        return Encoding.UTF8.GetString(value);
    }

    private static string Decode(ReadOnlySpan<byte> value)
    {
        var zero = value.IndexOf((byte)0);
        if (zero >= 0) value = value[..zero];
        return Encoding.UTF8.GetString(value);
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private sealed record Part(uint Offset, int CompressedSize, int OutputSize);
    private sealed record Toc(int PartCount, int FirstPart, int NameLength, int NameOffset, long OutputSize);
}
