namespace CL.Imaging.Internal;

/// <summary>CRC-32 (IEEE 802.3, reflected 0xEDB88320) for PNG chunks.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>Feeds <paramref name="data"/> into a running CRC (start with 0xFFFFFFFF, finish by XOR 0xFFFFFFFF).</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
