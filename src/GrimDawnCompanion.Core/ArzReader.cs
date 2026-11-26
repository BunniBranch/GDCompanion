using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace GrimDawnCompanion.Core;

/// <summary>Read-only in-memory reader for Grim Dawn ARZ v3 database archives.</summary>
internal sealed class ArzReader
{
    public const int HeaderSize = 24;

    public readonly record struct RecordHeader(
        uint NameId,
        int TypeOffset,
        int TypeLength,
        uint DataOffset,
        uint CompressedSize,
        uint UncompressedSize);

    public byte[] RawBytes { get; }
    public int StringTableOffset { get; }
    public uint StringCount { get; }
    public (int Offset, int Length)[] StringSlices { get; }
    public int RecordTableOffset { get; }
    public RecordHeader[] Records { get; }

    private ArzReader(
        byte[] rawBytes,
        int stringTableOffset,
        uint stringCount,
        (int Offset, int Length)[] stringSlices,
        int recordTableOffset,
        RecordHeader[] records)
    {
        RawBytes = rawBytes;
        StringTableOffset = stringTableOffset;
        StringCount = stringCount;
        StringSlices = stringSlices;
        RecordTableOffset = recordTableOffset;
        Records = records;
    }

    public static ArzReader Parse(byte[] rawBytes, string sourceName)
    {
        if (rawBytes.Length < HeaderSize)
            throw new InvalidDataException($"ARZ header is truncated: {sourceName}");

        var header = rawBytes.AsSpan(0, HeaderSize);
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(header);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        if (magic is not (2 or 3) || version != 3)
            throw new InvalidDataException($"Unsupported ARZ version ({magic}/{version}): {sourceName}");

        var recordTableOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        var recordTableSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var recordCount = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var stringTableOffset = (long)BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        var stringTableSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);

        if (recordTableOffset < HeaderSize || recordTableSize < 0 || recordTableOffset > rawBytes.Length - recordTableSize)
            throw new InvalidDataException($"ARZ record table is out of bounds: {sourceName}");
        if (stringTableOffset < HeaderSize || stringTableSize < 4 || stringTableOffset > rawBytes.Length - stringTableSize)
            throw new InvalidDataException($"ARZ string table is out of bounds: {sourceName}");

        var strSpan = rawBytes.AsSpan((int)stringTableOffset, (int)stringTableSize);
        var stringCount = BinaryPrimitives.ReadUInt32LittleEndian(strSpan);
        if (stringCount > 4_000_000)
            throw new InvalidDataException($"ARZ string count is invalid: {sourceName}");

        var stringSlices = new (int Offset, int Length)[stringCount];
        var sc = 4;
        for (uint i = 0; i < stringCount; i++)
        {
            if (sc > strSpan.Length - 4)
                throw new InvalidDataException($"ARZ string table is truncated: {sourceName}");
            var len = BinaryPrimitives.ReadUInt32LittleEndian(strSpan.Slice(sc, 4));
            sc += 4;
            if (len > (uint)(strSpan.Length - sc))
                throw new InvalidDataException($"ARZ string entry is truncated: {sourceName}");
            stringSlices[i] = (sc, (int)len);
            sc += (int)len;
        }

        var recSpan = rawBytes.AsSpan((int)recordTableOffset, (int)recordTableSize);
        var records = new RecordHeader[recordCount];
        var rc = 0;
        for (uint i = 0; i < recordCount; i++)
        {
            if (rc > recSpan.Length - 28)
                throw new InvalidDataException($"ARZ record table is truncated: {sourceName}");
            var nameId = BinaryPrimitives.ReadUInt32LittleEndian(recSpan.Slice(rc, 4)); rc += 4;
            var typeLen = BinaryPrimitives.ReadUInt32LittleEndian(recSpan.Slice(rc, 4)); rc += 4;
            if (typeLen > (uint)(recSpan.Length - rc - 20))
                throw new InvalidDataException($"ARZ record type is truncated: {sourceName}");
            var typeOff = rc;
            rc += (int)typeLen;
            var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(recSpan.Slice(rc, 4)); rc += 4;
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(recSpan.Slice(rc, 4)); rc += 4;
            var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(recSpan.Slice(rc, 4)); rc += 4;
            rc += 8; // timestamp
            records[i] = new RecordHeader(nameId, typeOff, (int)typeLen, dataOffset, compressedSize, uncompressedSize);
        }

        return new ArzReader(rawBytes, (int)stringTableOffset, stringCount, stringSlices, (int)recordTableOffset, records);
    }

    public ReadOnlySpan<byte> GetStringBytes(uint id)
    {
        var (off, len) = StringSlices[id];
        return RawBytes.AsSpan(StringTableOffset + off, len);
    }

    public string GetString(uint id)
    {
        var (off, len) = StringSlices[id];
        return Encoding.Latin1.GetString(RawBytes, StringTableOffset + off, len);
    }

    public ReadOnlySpan<byte> ReadRecordData(in RecordHeader header, ref byte[] scratchBuffer)
    {
        var fileOffset = HeaderSize + (long)header.DataOffset;
        if (fileOffset < HeaderSize || fileOffset > RawBytes.Length - header.CompressedSize)
            throw new InvalidDataException("ARZ record data is outside archive bounds.");
        if (header.UncompressedSize > 16 * 1024 * 1024)
            throw new InvalidDataException("ARZ record exceeds maximum supported size.");

        var compressed = RawBytes.AsSpan((int)fileOffset, (int)header.CompressedSize);
        var uncompressedSize = (int)header.UncompressedSize;
        if (compressed.Length == uncompressedSize)
            return compressed;

        if (scratchBuffer.Length < uncompressedSize)
            scratchBuffer = new byte[Math.Max(uncompressedSize, scratchBuffer.Length * 2)];

        var output = scratchBuffer.AsSpan(0, uncompressedSize);
        if (!TryDecompressLz4(compressed, output))
            DecompressDeflate(compressed, output);
        return output;
    }

    private static bool TryDecompressLz4(ReadOnlySpan<byte> source, Span<byte> output)
    {
        var si = 0;
        var di = 0;
        while (si < source.Length)
        {
            var token = source[si++];
            var literal = token >> 4;
            if (literal == 15)
            {
                byte extra;
                do
                {
                    if (si >= source.Length) return false;
                    extra = source[si++];
                    literal += extra;
                } while (extra == 255);
            }
            if (si + literal > source.Length || di + literal > output.Length) return false;
            source.Slice(si, literal).CopyTo(output.Slice(di, literal));
            si += literal;
            di += literal;
            if (si >= source.Length) break;
            if (si + 2 > source.Length) return false;
            var distance = source[si] | (source[si + 1] << 8);
            si += 2;
            if (distance <= 0 || distance > di) return false;
            var length = (token & 0x0f) + 4;
            if ((token & 0x0f) == 15)
            {
                byte extra;
                do
                {
                    if (si >= source.Length) return false;
                    extra = source[si++];
                    length += extra;
                } while (extra == 255);
            }
            if (di + length > output.Length) return false;
            if (distance >= length)
                output.Slice(di - distance, length).CopyTo(output.Slice(di, length));
            else
                for (var j = 0; j < length; j++) output[di + j] = output[di + j - distance];
            di += length;
        }
        return di == output.Length;
    }

    private static void DecompressDeflate(ReadOnlySpan<byte> input, Span<byte> output)
    {
        try
        {
            using var source = new MemoryStream(input.ToArray());
            using var deflate = new DeflateStream(source, CompressionMode.Decompress);
            var total = 0;
            while (total < output.Length)
            {
                var read = deflate.Read(output.Slice(total));
                if (read == 0) break;
                total += read;
            }
            if (total != output.Length) throw new InvalidDataException("Unexpected deflate output size.");
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException("ARZ block is neither valid LZ4 nor deflate data.", ex);
        }
    }
}
