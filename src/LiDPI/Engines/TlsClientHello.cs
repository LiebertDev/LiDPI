using System.Buffers.Binary;
using System.Security.Cryptography;
using LiDPI.Core;

namespace LiDPI.Engines;

/// <summary>Allocation-free helpers for inspecting and crafting TLS ClientHello messages.</summary>
internal static class TlsClientHello
{
    private const int RecordHeader = 5;
    private const int MaxRecordBody = 16384;

    private static ReadOnlySpan<byte> FakeHost => "www.w3.org"u8;

    private static readonly ushort[] FakeCiphers =
        [0x1301, 0x1302, 0x1303, 0xC02B, 0xC02F, 0xC02C, 0xC030, 0xCCA9, 0xCCA8];

    /// <summary>True if <paramref name="p"/> starts with a TLS handshake record carrying a ClientHello.</summary>
    public static bool IsClientHello(ReadOnlySpan<byte> p) =>
        p.Length > RecordHeader && p[0] == 0x16 && p[1] == 0x03 && p[5] == 0x01;

    /// <summary>
    /// Locates the SNI host name inside a (possibly truncated) ClientHello.
    /// <paramref name="offset"/> is relative to the start of <paramref name="p"/>; if the name is cut off
    /// by the end of the buffer, <paramref name="length"/> covers only the bytes present.
    /// </summary>
    public static bool TryFindSni(ReadOnlySpan<byte> p, out int offset, out int length)
    {
        offset = 0;
        length = 0;
        if (!IsClientHello(p)) return false;

        int pos = RecordHeader + 4;     // record header + handshake type/length
        pos += 2 + 32;                  // legacy_version + random
        if (pos + 1 > p.Length) return false;
        pos += 1 + p[pos];              // session id
        if (pos + 2 > p.Length) return false;
        pos += 2 + U16(p, pos);         // cipher suites
        if (pos + 1 > p.Length) return false;
        pos += 1 + p[pos];              // compression methods
        if (pos + 2 > p.Length) return false;

        int extEnd = Math.Min(pos + 2 + U16(p, pos), p.Length);
        pos += 2;
        while (pos + 4 <= extEnd)
        {
            int type = U16(p, pos);
            int len = U16(p, pos + 2);
            int body = pos + 4;
            if (type == 0x0000)
            {
                // ServerNameList: u16 length, then { u8 name_type, u16 length, bytes }
                int q = body + 2;
                int listEnd = Math.Min(body + len, p.Length);
                while (q + 3 <= listEnd)
                {
                    byte nameType = p[q];
                    int nameLen = U16(p, q + 1);
                    int nameStart = q + 3;
                    if (nameType == 0)
                    {
                        if (nameLen == 0 || nameStart >= p.Length) return false;
                        offset = nameStart;
                        length = Math.Min(nameLen, p.Length - nameStart);
                        return true;
                    }
                    q = nameStart + nameLen;
                }
                return false;
            }
            pos = body + len;
        }
        return false;
    }

    /// <summary>Returns the SNI as a string (for logging only), or null.</summary>
    public static string? GetSni(ReadOnlySpan<byte> p) =>
        TryFindSni(p, out int o, out int l) ? System.Text.Encoding.ASCII.GetString(p.Slice(o, l)) : null;

    /// <summary>
    /// Chooses the byte offset at which to split <paramref name="p"/> (always in [1, len-1]),
    /// or -1 if the payload is too short.
    /// </summary>
    public static int ChooseSplit(ReadOnlySpan<byte> p, BypassProfile profile)
    {
        int n = p.Length;
        if (n < 2) return -1;
        if (profile.SplitAtSni && TryFindSni(p, out int o, out int l))
            return Math.Clamp(o + l / 2, 1, n - 1);
        return Math.Clamp(profile.SplitPosition, 1, n - 1);
    }

    /// <summary>
    /// Writes a syntactically valid ClientHello for a harmless host into <paramref name="d"/>, padded
    /// (padding extension) to <paramref name="targetLen"/> when possible. Returns bytes written.
    /// </summary>
    public static int BuildFake(Span<byte> d, int targetLen)
    {
        int host = FakeHost.Length;
        int sniExt = 4 + 2 + 1 + 2 + host;
        const int svExt = 4 + 1 + 4;        // supported_versions: TLS1.3, TLS1.2
        const int grpExt = 4 + 2 + 4;       // supported_groups: x25519, secp256r1
        const int sigExt = 4 + 2 + 6;       // signature_algorithms: 3 entries
        int extLen = sniExt + svExt + grpExt + sigExt;
        int fixedLen = RecordHeader + 4 + 2 + 32 + 1 + 32 + 2 + FakeCiphers.Length * 2 + 2 + 2;
        int min = fixedLen + extLen;

        targetLen = Math.Min(targetLen, Math.Min(d.Length, RecordHeader + MaxRecordBody));
        int padData = targetLen - min - 4;
        bool pad = padData >= 0;
        int total = pad ? targetLen : min;
        if (total > d.Length) return 0;
        if (pad) extLen += 4 + padData;

        int w = 0;
        d[w++] = 0x16; d[w++] = 0x03; d[w++] = 0x01;
        W16(d, ref w, total - RecordHeader);
        d[w++] = 0x01;                                   // ClientHello
        int hsLen = total - RecordHeader - 4;
        d[w++] = (byte)(hsLen >> 16); d[w++] = (byte)(hsLen >> 8); d[w++] = (byte)hsLen;
        d[w++] = 0x03; d[w++] = 0x03;                    // legacy_version TLS1.2
        RandomNumberGenerator.Fill(d.Slice(w, 32)); w += 32;
        d[w++] = 32;
        RandomNumberGenerator.Fill(d.Slice(w, 32)); w += 32;
        W16(d, ref w, FakeCiphers.Length * 2);
        foreach (ushort c in FakeCiphers) W16(d, ref w, c);
        d[w++] = 0x01; d[w++] = 0x00;                    // compression: null
        W16(d, ref w, extLen);

        // server_name
        W16(d, ref w, 0x0000); W16(d, ref w, 2 + 1 + 2 + host);
        W16(d, ref w, 1 + 2 + host); d[w++] = 0x00; W16(d, ref w, host);
        FakeHost.CopyTo(d[w..]); w += host;
        // supported_versions
        W16(d, ref w, 0x002B); W16(d, ref w, 5); d[w++] = 4; W16(d, ref w, 0x0304); W16(d, ref w, 0x0303);
        // supported_groups
        W16(d, ref w, 0x000A); W16(d, ref w, 6); W16(d, ref w, 4); W16(d, ref w, 0x001D); W16(d, ref w, 0x0017);
        // signature_algorithms
        W16(d, ref w, 0x000D); W16(d, ref w, 8); W16(d, ref w, 6);
        W16(d, ref w, 0x0403); W16(d, ref w, 0x0804); W16(d, ref w, 0x0401);
        if (pad)
        {
            W16(d, ref w, 0x0015); W16(d, ref w, padData);
            d.Slice(w, padData).Clear(); w += padData;
        }
        return w;
    }

    /// <summary>
    /// Re-frames the first TLS record of <paramref name="src"/> as two records split at absolute offset
    /// <paramref name="splitAt"/> (which must fall inside the record body). Trailing bytes are copied as-is.
    /// Returns bytes written (src.Length + 5) or -1 if not applicable.
    /// </summary>
    public static int FragmentRecord(ReadOnlySpan<byte> src, int splitAt, Span<byte> dst)
    {
        if (src.Length < RecordHeader + 2 || src[0] != 0x16) return -1;
        int recLen = U16(src, 3);
        int recEnd = RecordHeader + recLen;
        if (recEnd > src.Length || splitAt <= RecordHeader || splitAt >= recEnd) return -1;
        if (dst.Length < src.Length + RecordHeader) return -1;

        int len1 = splitAt - RecordHeader;
        int len2 = recEnd - splitAt;
        int w = 0;
        dst[w++] = src[0]; dst[w++] = src[1]; dst[w++] = src[2];
        W16(dst, ref w, len1);
        src.Slice(RecordHeader, len1).CopyTo(dst[w..]); w += len1;
        dst[w++] = src[0]; dst[w++] = src[1]; dst[w++] = src[2];
        W16(dst, ref w, len2);
        src.Slice(splitAt, len2).CopyTo(dst[w..]); w += len2;
        src[recEnd..].CopyTo(dst[w..]); w += src.Length - recEnd;
        return w;
    }

    private static int U16(ReadOnlySpan<byte> p, int at) => BinaryPrimitives.ReadUInt16BigEndian(p.Slice(at, 2));

    private static void W16(Span<byte> d, ref int w, int v)
    {
        BinaryPrimitives.WriteUInt16BigEndian(d.Slice(w, 2), (ushort)v);
        w += 2;
    }
}
