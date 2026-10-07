using System.Buffers.Binary;
using System.Globalization;
using System.Net;

namespace LiDPI.Dns;

/// <summary>Minimal DNS wire-format helpers (RFC 1035).</summary>
internal static class DnsWire
{
    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;

    private static readonly IdnMapping Idn = new();

    /// <summary>Builds a recursive query with ID 0 (RFC 8484 cache friendliness).</summary>
    public static byte[] BuildQuery(string host, ushort qtype)
    {
        string ascii = Idn.GetAscii(host.TrimEnd('.'));
        if (ascii.Length == 0 || ascii.Length > 253) throw new ArgumentException("Geçersiz alan adı.", nameof(host));

        var labels = ascii.Split('.');
        int nameLen = 1;
        foreach (var l in labels)
        {
            if (l.Length == 0 || l.Length > 63) throw new ArgumentException("Geçersiz alan adı.", nameof(host));
            nameLen += 1 + l.Length;
        }

        var q = new byte[12 + nameLen + 4];
        q[2] = 0x01;                 // RD
        q[5] = 0x01;                 // QDCOUNT = 1
        int w = 12;
        foreach (var l in labels)
        {
            q[w++] = (byte)l.Length;
            for (int i = 0; i < l.Length; i++) q[w++] = (byte)l[i];
        }
        q[w++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(q.AsSpan(w), qtype);
        BinaryPrimitives.WriteUInt16BigEndian(q.AsSpan(w + 2), 1); // IN
        return q;
    }

    /// <summary>Advances <paramref name="pos"/> past an encoded (possibly compressed) name.</summary>
    public static bool TrySkipName(ReadOnlySpan<byte> m, ref int pos)
    {
        for (int guard = 0; guard < 128; guard++)
        {
            if (pos >= m.Length) return false;
            byte b = m[pos];
            if (b == 0) { pos++; return true; }
            if ((b & 0xC0) == 0xC0) { pos += 2; return pos <= m.Length; }
            if ((b & 0xC0) != 0) return false;
            pos += 1 + b;
        }
        return false;
    }

    /// <summary>Offset just past the question section, or false if malformed.</summary>
    public static bool TryGetQuestionEnd(ReadOnlySpan<byte> m, out int end)
    {
        end = 0;
        if (m.Length < 12) return false;
        int qd = BinaryPrimitives.ReadUInt16BigEndian(m[4..]);
        int pos = 12;
        for (int i = 0; i < qd; i++)
        {
            if (!TrySkipName(m, ref pos)) return false;
            pos += 4;
            if (pos > m.Length) return false;
        }
        end = pos;
        return true;
    }

    /// <summary>Extracts A/AAAA records from the answer section. Returns false if malformed.</summary>
    public static bool TryParseAddresses(ReadOnlySpan<byte> m, List<IPAddress> into, ref uint minTtl)
    {
        if (!TryGetQuestionEnd(m, out int pos)) return false;
        int rcode = m[3] & 0x0F;
        if (rcode != 0) return true; // NXDOMAIN etc.: no addresses
        int an = BinaryPrimitives.ReadUInt16BigEndian(m[6..]);
        for (int i = 0; i < an; i++)
        {
            if (!TrySkipName(m, ref pos) || pos + 10 > m.Length) return false;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(m[pos..]);
            ushort cls = BinaryPrimitives.ReadUInt16BigEndian(m[(pos + 2)..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(m[(pos + 4)..]);
            int rdLen = BinaryPrimitives.ReadUInt16BigEndian(m[(pos + 8)..]);
            pos += 10;
            if (pos + rdLen > m.Length) return false;
            if (cls == 1 && ((type == TypeA && rdLen == 4) || (type == TypeAaaa && rdLen == 16)))
            {
                into.Add(new IPAddress(m.Slice(pos, rdLen)));
                if (ttl < minTtl) minTtl = ttl;
            }
            pos += rdLen;
        }
        return true;
    }

    /// <summary>
    /// Builds a truncated (TC=1) answer from a query so the client retries over TCP.
    /// Returns null if the query is malformed.
    /// </summary>
    public static byte[]? BuildTruncatedReply(ReadOnlySpan<byte> query)
    {
        if (!TryGetQuestionEnd(query, out int end)) return null;
        var r = query[..end].ToArray();
        r[2] = (byte)((r[2] & 0x79) | 0x80 | 0x02);  // QR=1, keep opcode/RD, TC=1, AA=0
        r[3] = 0x80;                                 // RA=1, RCODE=0
        r[6] = r[7] = r[8] = r[9] = r[10] = r[11] = 0;
        return r;
    }
}
