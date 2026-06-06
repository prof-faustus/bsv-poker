using System.Buffers.Binary;
using System.Numerics;
using BsvPoker.Crypto;

namespace BsvPoker.Net.Bsv;

/// <summary>
/// An 80-byte Bitcoin block header and its proof-of-work. The client validates the header chain itself
/// (it IS a node), so it trusts no one for which chain is real. Hash = SHA-256d(serialized header),
/// compared as a little-endian 256-bit number against the target encoded by <c>bits</c>.
/// </summary>
public sealed record BlockHeader(uint Version, byte[] PrevHash, byte[] MerkleRoot, uint Time, uint Bits, uint Nonce)
{
    public byte[] Serialize()
    {
        if (PrevHash.Length != 32 || MerkleRoot.Length != 32) throw new ArgumentException("hashes must be 32 bytes");
        var b = new byte[80];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0, 4), Version);
        PrevHash.CopyTo(b, 4);
        MerkleRoot.CopyTo(b, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(68, 4), Time);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(72, 4), Bits);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(76, 4), Nonce);
        return b;
    }

    public static BlockHeader Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 80) throw new ArgumentException("header must be 80 bytes");
        return new BlockHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(0, 4)),
            b.Slice(4, 32).ToArray(), b.Slice(36, 32).ToArray(),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(68, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(72, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(76, 4)));
    }

    /// <summary>The block hash (SHA-256d of the serialized header), in internal little-endian byte order.</summary>
    public byte[] Hash() => Hashes.Sha256d(Serialize());
    public string HashHex() { var h = (byte[])Hash().Clone(); Array.Reverse(h); return Convert.ToHexString(h).ToLowerInvariant(); } // display = big-endian

    /// <summary>Expand the compact <c>bits</c> to a full 256-bit target.</summary>
    public static BigInteger Target(uint bits)
    {
        int exp = (int)(bits >> 24);
        BigInteger mant = bits & 0x007fffff;
        return exp <= 3 ? mant >> (8 * (3 - exp)) : mant << (8 * (exp - 3));
    }

    /// <summary>True if the header's hash meets its own proof-of-work target.</summary>
    public bool MeetsPow()
    {
        var target = Target(Bits);
        if (target <= 0) return false;
        var h = new BigInteger(Hash(), isUnsigned: true, isBigEndian: false); // hash is little-endian
        return h <= target;
    }

    /// <summary>The work a header at this target contributes: 2^256 / (target+1).</summary>
    public static BigInteger Work(uint bits)
    {
        var t = Target(bits);
        if (t <= 0) return BigInteger.Zero;
        return (BigInteger.One << 256) / (t + 1);
    }
}
