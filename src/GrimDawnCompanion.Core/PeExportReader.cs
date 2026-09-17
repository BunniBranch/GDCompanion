using System.Buffers.Binary;
using System.Text;

namespace GrimDawnCompanion.Core;

public sealed class PeExportReader
{
    private readonly byte[] _image;
    private readonly List<Section> _sections = [];
    private readonly uint _exportRva;

    public PeExportReader(string path)
    {
        _image = File.ReadAllBytes(path);
        if (_image.Length < 0x100 || U16(0) != 0x5a4d) throw new InvalidDataException("Not a valid PE image.");
        var pe = checked((int)U32(0x3c));
        if (U32(pe) != 0x00004550) throw new InvalidDataException("PE signature is missing.");
        var sectionCount = U16(pe + 6);
        var optionalSize = U16(pe + 20);
        var optional = pe + 24;
        if (U16(optional) != 0x20b) throw new InvalidDataException("The module is not a 64-bit PE image.");
        _exportRva = U32(optional + 112);
        var sectionTable = optional + optionalSize;
        for (var i = 0; i < sectionCount; i++)
        {
            var offset = sectionTable + i * 40;
            var name = Encoding.ASCII.GetString(_image, offset, 8).TrimEnd('\0');
            _sections.Add(new(name, U32(offset + 12), Math.Max(U32(offset + 8), U32(offset + 16)), U32(offset + 20), U32(offset + 36)));
        }
    }

    public ExportSymbol? Find(string decoratedName)
    {
        if (_exportRva == 0) return null;
        var export = Offset(_exportRva);
        var functionCount = U32(export + 20);
        var nameCount = U32(export + 24);
        var functions = U32(export + 28);
        var names = U32(export + 32);
        var ordinals = U32(export + 36);
        for (uint i = 0; i < nameCount; i++)
        {
            var nameRva = U32(Offset(names + i * 4));
            var name = ReadAscii(Offset(nameRva));
            if (!string.Equals(name, decoratedName, StringComparison.Ordinal)) continue;
            var ordinal = U16(Offset(ordinals + i * 2));
            if (ordinal >= functionCount) return null;
            var rva = U32(Offset(functions + (uint)ordinal * 4));
            var section = _sections.FirstOrDefault(x => rva >= x.VirtualAddress && rva < x.VirtualAddress + x.VirtualSize);
            if (section is null) return null;
            var fileOffset = Offset(rva);
            // Keep enough verified body bytes for capability-specific instruction
            // checks while still producing a compact patch fingerprint.
            var byteCount = Math.Min(256, _image.Length - fileOffset);
            var prologue = _image.AsSpan(fileOffset, byteCount).ToArray();
            return new(name, rva, section.Name, (section.Characteristics & 0x20000000) != 0, (section.Characteristics & 0x40000000) != 0, prologue);
        }
        return null;
    }

    private int Offset(uint rva)
    {
        var section = _sections.FirstOrDefault(x => rva >= x.VirtualAddress && rva < x.VirtualAddress + x.VirtualSize)
                      ?? throw new InvalidDataException($"RVA 0x{rva:X} is outside the PE sections.");
        return checked((int)(section.RawOffset + rva - section.VirtualAddress));
    }

    private string ReadAscii(int offset)
    {
        var end = offset;
        while (end < _image.Length && _image[end] != 0) end++;
        return Encoding.ASCII.GetString(_image, offset, end - offset);
    }

    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_image.AsSpan(offset));
    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_image.AsSpan(offset));
    private sealed record Section(string Name, uint VirtualAddress, uint VirtualSize, uint RawOffset, uint Characteristics);
}

public sealed record ExportSymbol(string Name, uint Rva, string Section, bool IsExecutable, bool IsReadable, byte[] Prologue);
