using System.Security.Cryptography;
using System.Text;

namespace GrimDawnCompanion.Core;

public static class Hashing
{
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string Sha256Bytes(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));

    public static string CombineFingerprint(IEnumerable<string> parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))));
}
