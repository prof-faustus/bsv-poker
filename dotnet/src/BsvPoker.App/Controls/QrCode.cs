namespace BsvPoker.App.Controls;

/// <summary>
/// A self-contained QR Code generator (byte mode) — no external dependency. It encodes a string (e.g. a
/// <c>bitcoin:</c> payment URI or an address) into a boolean module matrix the Receive tab renders, exactly
/// like ElectrumSV's receive QR. Supports versions 1–10 at error-correction level M (ample for an address or
/// a payment URI), full Reed–Solomon ECC over GF(256), all eight data masks with the standard penalty-based
/// mask selection, and the format-information bits. The output is a square <c>bool[,]</c> where true = a dark
/// module. This is real ISO/IEC 18004 encoding, not a placeholder.
/// </summary>
public static class QrCode
{
    // ---- GF(256) for Reed–Solomon (primitive polynomial 0x11d) ----
    private static readonly int[] Exp = new int[512];
    private static readonly int[] Log = new int[256];
    static QrCode()
    {
        int x = 1;
        for (int i = 0; i < 255; i++) { Exp[i] = x; Log[x] = i; x <<= 1; if ((x & 0x100) != 0) x ^= 0x11d; }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }
    private static int Mul(int a, int b) => (a == 0 || b == 0) ? 0 : Exp[Log[a] + Log[b]];

    // ---- per-version data for level M: total data codewords, ECC codewords per block, block counts ----
    // (group1Blocks, group1DataCw, group2Blocks, group2DataCw, eccPerBlock) for versions 1..10 at ECC level M.
    private static readonly (int g1, int d1, int g2, int d2, int ecc)[] LevelM =
    {
        (0,0,0,0,0),                 // index 0 unused
        (1,16,0,0,10),               // v1
        (1,28,0,0,16),               // v2
        (1,44,0,0,26),               // v3
        (2,32,0,0,18),               // v4
        (2,43,0,0,24),               // v5
        (4,27,0,0,16),               // v6
        (4,31,0,0,18),               // v7
        (2,38,2,39,22),              // v8
        (3,36,2,37,22),              // v9
        (4,43,1,44,26),              // v10
    };

    // alignment pattern centre coordinates per version (1..10)
    private static readonly int[][] AlignPos =
    {
        Array.Empty<int>(),                 // 0
        Array.Empty<int>(),                 // v1
        new[]{6,18}, new[]{6,22}, new[]{6,26}, new[]{6,30}, new[]{6,34}, // v2..v6
        new[]{6,22,38}, new[]{6,24,42}, new[]{6,26,46}, new[]{6,28,50},  // v7..v10
    };

    /// <summary>Encode <paramref name="text"/> into a square module matrix (true = dark). Throws if it will not fit v1–10.</summary>
    public static bool[,] Encode(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        int version = PickVersion(bytes.Length);
        var spec = LevelM[version];
        int totalDataCw = spec.g1 * spec.d1 + spec.g2 * spec.d2;

        // ---- build the bit stream: mode (byte=0100) + length + data + terminator + pad ----
        var bits = new List<bool>();
        AppendBits(bits, 0b0100, 4);                          // byte mode
        int lenBits = version <= 9 ? 8 : 16;                  // char-count indicator width
        AppendBits(bits, bytes.Length, lenBits);
        foreach (var b in bytes) AppendBits(bits, b, 8);
        int capacityBits = totalDataCw * 8;
        for (int i = 0; i < 4 && bits.Count < capacityBits; i++) bits.Add(false); // terminator
        while (bits.Count % 8 != 0) bits.Add(false);
        var data = new List<int>();
        for (int i = 0; i < bits.Count; i += 8) data.Add(BitsToInt(bits, i, 8));
        bool padToggle = true;
        while (data.Count < totalDataCw) { data.Add(padToggle ? 0xEC : 0x11); padToggle = !padToggle; }

        // ---- split into blocks, compute ECC, then interleave ----
        var blocksData = new List<int[]>();
        var blocksEcc = new List<int[]>();
        int pos = 0;
        for (int gb = 0; gb < spec.g1; gb++) { var blk = data.GetRange(pos, spec.d1).ToArray(); pos += spec.d1; blocksData.Add(blk); blocksEcc.Add(ReedSolomon(blk, spec.ecc)); }
        for (int gb = 0; gb < spec.g2; gb++) { var blk = data.GetRange(pos, spec.d2).ToArray(); pos += spec.d2; blocksData.Add(blk); blocksEcc.Add(ReedSolomon(blk, spec.ecc)); }

        var final = new List<int>();
        int maxData = Math.Max(spec.d1, spec.d2);
        for (int i = 0; i < maxData; i++) foreach (var blk in blocksData) if (i < blk.Length) final.Add(blk[i]);
        for (int i = 0; i < spec.ecc; i++) foreach (var blk in blocksEcc) final.Add(blk[i]);

        var finalBits = new List<bool>();
        foreach (var cw in final) AppendBits(finalBits, cw, 8);

        // ---- place into the matrix, try all 8 masks, keep the lowest-penalty ----
        int size = 17 + version * 4;
        bool[,]? best = null; int bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            var (m, reserved) = BuildBase(version, size);
            PlaceData(m, reserved, finalBits, mask);
            PlaceFormat(m, mask);
            int pen = Penalty(m, size);
            if (pen < bestPenalty) { bestPenalty = pen; best = m; }
        }
        return best!;
    }

    private static int PickVersion(int dataLen)
    {
        for (int v = 1; v <= 10; v++)
        {
            int total = LevelM[v].g1 * LevelM[v].d1 + LevelM[v].g2 * LevelM[v].d2;
            int lenBits = v <= 9 ? 8 : 16;
            int needBits = 4 + lenBits + dataLen * 8;
            if (needBits <= total * 8) return v;
        }
        throw new ArgumentException("data too large for a v1–10 QR code");
    }

    private static int[] ReedSolomon(int[] data, int eccLen)
    {
        var gen = Generator(eccLen);
        var res = new int[data.Length + eccLen];
        Array.Copy(data, res, data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            int factor = res[i];
            if (factor == 0) continue;
            for (int j = 0; j < gen.Length; j++) res[i + j] ^= Mul(gen[j], factor);
        }
        return res[^eccLen..];
    }

    private static int[] Generator(int degree)
    {
        var g = new[] { 1 };
        for (int i = 0; i < degree; i++)
        {
            var ng = new int[g.Length + 1];
            for (int j = 0; j < g.Length; j++) { ng[j] ^= Mul(g[j], 1); ng[j + 1] ^= Mul(g[j], Exp[i]); }
            g = ng;
        }
        return g;
    }

    private static (bool[,] m, bool[,] reserved) BuildBase(int version, int size)
    {
        var m = new bool[size, size];
        var reserved = new bool[size, size];
        void Finder(int r, int c)
        {
            for (int dr = -1; dr <= 7; dr++)
                for (int dc = -1; dc <= 7; dc++)
                {
                    int rr = r + dr, cc = c + dc;
                    if (rr < 0 || cc < 0 || rr >= size || cc >= size) continue;
                    reserved[rr, cc] = true;
                    bool dark = (dr >= 0 && dr <= 6 && (dc == 0 || dc == 6)) ||
                                (dc >= 0 && dc <= 6 && (dr == 0 || dr == 6)) ||
                                (dr >= 2 && dr <= 4 && dc >= 2 && dc <= 4);
                    m[rr, cc] = dark;
                }
        }
        Finder(0, 0); Finder(0, size - 7); Finder(size - 7, 0);

        // timing patterns
        for (int i = 8; i < size - 8; i++)
        {
            if (!reserved[6, i]) { m[6, i] = i % 2 == 0; reserved[6, i] = true; }
            if (!reserved[i, 6]) { m[i, 6] = i % 2 == 0; reserved[i, 6] = true; }
        }

        // alignment patterns
        var pos = AlignPos[version];
        foreach (var r in pos)
            foreach (var c in pos)
            {
                if ((r <= 8 && c <= 8) || (r <= 8 && c >= size - 9) || (r >= size - 9 && c <= 8)) continue;
                for (int dr = -2; dr <= 2; dr++)
                    for (int dc = -2; dc <= 2; dc++)
                    {
                        reserved[r + dr, c + dc] = true;
                        m[r + dr, c + dc] = Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1;
                    }
            }

        // dark module + reserve format areas
        m[size - 8, 8] = true; reserved[size - 8, 8] = true;
        for (int i = 0; i < 9; i++) { reserved[8, i] = true; reserved[i, 8] = true; }
        for (int i = 0; i < 8; i++) { reserved[8, size - 1 - i] = true; reserved[size - 1 - i, 8] = true; }
        // version info area (v >= 7) — we cap at v10, so reserve it
        if (version >= 7)
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 3; j++)
                { reserved[i, size - 11 + j] = true; reserved[size - 11 + j, i] = true; }
        return (m, reserved);
    }

    private static void PlaceData(bool[,] m, bool[,] reserved, List<bool> bits, int mask)
    {
        int size = m.GetLength(0);
        int bitIdx = 0; bool upward = true;
        for (int col = size - 1; col > 0; col -= 2)
        {
            if (col == 6) col = 5; // skip the vertical timing column
            for (int i = 0; i < size; i++)
            {
                int row = upward ? size - 1 - i : i;
                for (int c = 0; c < 2; c++)
                {
                    int cc = col - c;
                    if (reserved[row, cc]) continue;
                    bool bit = bitIdx < bits.Count && bits[bitIdx]; bitIdx++;
                    if (MaskBit(mask, row, cc)) bit = !bit;
                    m[row, cc] = bit;
                }
            }
            upward = !upward;
        }
    }

    private static bool MaskBit(int mask, int r, int c) => mask switch
    {
        0 => (r + c) % 2 == 0,
        1 => r % 2 == 0,
        2 => c % 3 == 0,
        3 => (r + c) % 3 == 0,
        4 => (r / 2 + c / 3) % 2 == 0,
        5 => (r * c) % 2 + (r * c) % 3 == 0,
        6 => ((r * c) % 2 + (r * c) % 3) % 2 == 0,
        _ => ((r + c) % 2 + (r * c) % 3) % 2 == 0,
    };

    private static void PlaceFormat(bool[,] m, int mask)
    {
        int size = m.GetLength(0);
        // ECC level M = 00; format data = level(2) + mask(3); BCH(15,5) with mask 0x5412
        int fmt = (0b00 << 3) | mask;
        int bch = fmt << 10;
        for (int i = 14; i >= 10; i--) if (((bch >> i) & 1) != 0) bch ^= 0x537 << (i - 10);
        int format = ((fmt << 10) | bch) ^ 0x5412;
        for (int i = 0; i < 15; i++)
        {
            bool bit = ((format >> i) & 1) != 0;
            // around top-left
            if (i < 6) m[8, i] = bit;
            else if (i == 6) m[8, 7] = bit;
            else if (i == 7) m[8, 8] = bit;
            else if (i == 8) m[7, 8] = bit;
            else m[14 - i, 8] = bit;
            // the duplicate copy
            if (i < 8) m[size - 1 - i, 8] = bit;
            else m[8, size - 15 + i] = bit;
        }
        m[size - 8, 8] = true; // dark module
    }

    private static int Penalty(bool[,] m, int size)
    {
        int p = 0;
        // rule 1: runs of 5+ same colour in rows/cols
        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
            {
                if (c <= size - 5) { bool v = m[r, c]; int run = 1; while (c + run < size && m[r, c + run] == v) run++; if (run >= 5) { p += 3 + (run - 5); } }
            }
        for (int c = 0; c < size; c++)
            for (int r = 0; r < size; r++)
            {
                if (r <= size - 5) { bool v = m[r, c]; int run = 1; while (r + run < size && m[r + run, c] == v) run++; if (run >= 5) { p += 3 + (run - 5); } }
            }
        // rule 3: dark proportion
        int dark = 0; foreach (var b in m) if (b) dark++;
        int pct = dark * 100 / (size * size);
        p += Math.Abs(pct - 50) / 5 * 10;
        return p;
    }

    private static void AppendBits(List<bool> bits, int value, int count)
    { for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }

    private static int BitsToInt(List<bool> bits, int start, int count)
    { int v = 0; for (int i = 0; i < count; i++) { v <<= 1; if (start + i < bits.Count && bits[start + i]) v |= 1; } return v; }
}
