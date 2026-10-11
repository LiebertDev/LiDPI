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
        if (ascii.Length == 0 || ascii.Length > 253) throw new ArgumentException(LiDPI.UI.Strings.ErrInvalidHostName, nameof(host));

        var labels = ascii.Split('.');
        int nameLen = 1;
        foreach (var l in labels)
        {
            if (l.Length == 0 || l.Length > 63) throw new ArgumentException(LiDPI.UI.Strings.ErrInvalidHostName, nameof(host));
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
    public const ushort TypeOpt = 41;
    /// <summary>Classic DNS-over-UDP payload limit without EDNS (RFC 1035 4.2.1).</summary>
    public const int ClassicUdpLimit = 512;

    /// <summary>
    /// Largest UDP response the client accepts: the EDNS(0) OPT payload size from the query's additional
    /// section (clamped to 512..<paramref name="cap"/>), or 512 when the query carries no OPT record.
    /// </summary>
    public static int GetClientUdpLimit(ReadOnlySpan<byte> query, int cap = 4096)
    {
        if (!TryGetQuestionEnd(query, out int pos)) return ClassicUdpLimit;
        int skip = BinaryPrimitives.ReadUInt16BigEndian(query[6..]) + BinaryPrimitives.ReadUInt16BigEndian(query[8..]);
        int ar = BinaryPrimitives.ReadUInt16BigEndian(query[10..]);
        for (int i = 0; i < skip + ar; i++)
        {
            if (!TrySkipName(query, ref pos) || pos + 10 > query.Length) return ClassicUdpLimit;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(query[pos..]);
            int size = BinaryPrimitives.ReadUInt16BigEndian(query[(pos + 2)..]);
            int rdLen = BinaryPrimitives.ReadUInt16BigEndian(query[(pos + 8)..]);
            if (i >= skip && type == TypeOpt) return Math.Clamp(size, ClassicUdpLimit, Math.Max(cap, ClassicUdpLimit));
            pos += 10 + rdLen;
        }
        return ClassicUdpLimit;
    }

    /// <summary>
    /// Makes a response fit a UDP limit without losing the answer when possible: drops the authority and
    /// additional sections (keeping an OPT record), and only if the answers alone still do not fit keeps as
    /// many whole answer records as fit and sets TC=1 (RFC 2181 9). Records are only dropped from the end,
    /// so compression pointers (which always point backwards) stay valid. Returns the input when it fits,
    /// null when the message is malformed.
    /// </summary>
    public static byte[]? FitToUdp(byte[] resp, int limit)
    {
        if (resp.Length <= limit) return resp;
        var m = resp.AsSpan();
        if (!TryGetQuestionEnd(m, out int qEnd) || qEnd > limit) return null;
        int an = BinaryPrimitives.ReadUInt16BigEndian(m[6..]);
        int ns = BinaryPrimitives.ReadUInt16BigEndian(m[8..]);
        int ar = BinaryPrimitives.ReadUInt16BigEndian(m[10..]);

        // record boundaries of the answer section, and the raw OPT record (if any)
        var ends = new List<int>(an);
        int pos = qEnd;
        int optStart = -1, optEnd = -1;
        for (int i = 0; i < an + ns + ar; i++)
        {
            int start = pos;
            if (!TrySkipName(m, ref pos) || pos + 10 > m.Length) return null;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(m[pos..]);
            pos += 10 + BinaryPrimitives.ReadUInt16BigEndian(m[(pos + 8)..]);
            if (pos > m.Length) return null;
            if (i < an) ends.Add(pos);
            else if (i >= an + ns && type == TypeOpt && optStart < 0) { optStart = start; optEnd = pos; }
        }
        int optLen = optStart < 0 ? 0 : optEnd - optStart;

        int keep = an, bodyEnd = an == 0 ? qEnd : ends[^1];
        bool truncated = false;
        while (bodyEnd + optLen > limit && keep > 0)
        {
            truncated = true;
            keep--;
            bodyEnd = keep == 0 ? qEnd : ends[keep - 1];
        }
        if (bodyEnd + optLen > limit) optLen = 0;   // question alone fits (checked above)

        var r = new byte[bodyEnd + optLen];
        m[..bodyEnd].CopyTo(r);
        if (optLen > 0) m.Slice(optStart, optLen).CopyTo(r.AsSpan(bodyEnd));
        if (truncated) r[2] |= 0x02;
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(6), (ushort)keep);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(10), (ushort)(optLen > 0 ? 1 : 0));
        return r;
    }

    public const ushort TypeSoa = 6;

    /// <summary>
    /// Reads the single question of a message: the name as lowercase dotted ASCII (uncompressed labels only),
    /// QTYPE and QCLASS. False for QDCOUNT != 1, compression in the question or malformed input.
    /// </summary>
    public static bool TryReadQuestion(ReadOnlySpan<byte> m, out string name, out ushort qtype, out ushort qclass, out int end)
    {
        name = "";
        qtype = qclass = 0;
        end = 0;
        if (m.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(m[4..]) != 1) return false;
        int pos = 12;
        Span<char> buf = stackalloc char[256];
        int w = 0;
        while (true)
        {
            if (pos >= m.Length) return false;
            int len = m[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xC0) != 0 || pos + 1 + len > m.Length || w + len + 1 > buf.Length) return false;
            if (w > 0) buf[w++] = '.';
            for (int i = 0; i < len; i++)
            {
                byte b = m[pos + 1 + i];
                if (b >= 'A' && b <= 'Z') b = (byte)(b + 32);
                buf[w++] = (char)b;
            }
            pos += 1 + len;
        }
        if (pos + 4 > m.Length) return false;
        qtype = BinaryPrimitives.ReadUInt16BigEndian(m[pos..]);
        qclass = BinaryPrimitives.ReadUInt16BigEndian(m[(pos + 2)..]);
        name = w == 0 ? "." : new string(buf[..w]);
        end = pos + 4;
        return true;
    }

    /// <summary>
    /// EDNS(0) state of a message: whether its additional section has an OPT record, and the DO bit of that record.
    /// False if the record sections are malformed.
    /// </summary>
    public static bool TryGetEdns(ReadOnlySpan<byte> m, out bool hasOpt, out bool doBit)
    {
        hasOpt = doBit = false;
        if (!TryGetQuestionEnd(m, out int pos)) return false;
        int skip = BinaryPrimitives.ReadUInt16BigEndian(m[6..]) + BinaryPrimitives.ReadUInt16BigEndian(m[8..]);
        int ar = BinaryPrimitives.ReadUInt16BigEndian(m[10..]);
        for (int i = 0; i < skip + ar; i++)
        {
            if (!TrySkipName(m, ref pos) || pos + 10 > m.Length) return false;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(m[pos..]);
            int rdLen = BinaryPrimitives.ReadUInt16BigEndian(m[(pos + 8)..]);
            if (i >= skip && type == TypeOpt)
            {
                hasOpt = true;
                doBit = (m[pos + 6] & 0x80) != 0;   // TTL field = ext-RCODE, version, flags (DO = top bit)
                return true;
            }
            pos += 10 + rdLen;
            if (pos > m.Length) return false;
        }
        return true;
    }

    /// <summary>
    /// How long a response may be cached, in seconds: the smallest answer TTL for an answer (NOERROR with records),
    /// or for NXDOMAIN / NODATA the SOA negative TTL (min of the SOA record TTL and its MINIMUM field, RFC 2308).
    /// Returns null when the response must not be cached (other RCODEs, TC set, malformed). <paramref name="negative"/>
    /// tells which rule was used; when no TTL is present the result is 0 (the caller clamps).
    /// </summary>
    public static uint? GetCacheTtl(ReadOnlySpan<byte> m, out bool negative)
    {
        negative = false;
        if (m.Length < 12 || (m[2] & 0x80) == 0 || (m[2] & 0x02) != 0) return null;   // not a response / truncated
        int rcode = m[3] & 0x0F;
        if (rcode is not (0 or 3)) return null;
        if (!TryGetQuestionEnd(m, out int pos)) return null;
        int an = BinaryPrimitives.ReadUInt16BigEndian(m[6..]);
        int ns = BinaryPrimitives.ReadUInt16BigEndian(m[8..]);
        negative = rcode == 3 || an == 0;
        uint min = uint.MaxValue;
        for (int i = 0; i < an + ns; i++)
        {
            if (!TrySkipName(m, ref pos) || pos + 10 > m.Length) return null;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(m[pos..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(m[(pos + 4)..]);
            int rdLen = BinaryPrimitives.ReadUInt16BigEndian(m[(pos + 8)..]);
            int rd = pos + 10;
            if (rd + rdLen > m.Length) return null;
            if (!negative && i < an)
            {
                if (ttl < min) min = ttl;
            }
            else if (negative && i >= an && type == TypeSoa)
            {
                // RDATA: MNAME, RNAME, SERIAL, REFRESH, RETRY, EXPIRE, MINIMUM
                int p = rd;
                if (TrySkipName(m, ref p) && TrySkipName(m, ref p) && p + 20 <= rd + rdLen)
                {
                    uint minimum = BinaryPrimitives.ReadUInt32BigEndian(m[(p + 16)..]);
                    uint neg = Math.Min(ttl, minimum);
                    if (neg < min) min = neg;
                }
            }
            pos = rd + rdLen;
        }
        return min == uint.MaxValue ? 0 : min;
    }

    /// <summary>
    /// Rewrites the TTL of every record except OPT: to <paramref name="fixedTtl"/> when given, else to
    /// (TTL - <paramref name="elapsed"/>), at least 1. Leaves the message unchanged where it is malformed.
    /// </summary>
    public static void RewriteTtls(Span<byte> m, uint elapsed, uint? fixedTtl)
    {
        if (!TryGetQuestionEnd(m, out int pos)) return;
        int count = BinaryPrimitives.ReadUInt16BigEndian(m[6..]) + BinaryPrimitives.ReadUInt16BigEndian(m[8..])
                    + BinaryPrimitives.ReadUInt16BigEndian(m[10..]);
        for (int i = 0; i < count; i++)
        {
            if (!TrySkipName(m, ref pos) || pos + 10 > m.Length) return;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(m[pos..]);
            if (type != TypeOpt)
            {
                var t = m[(pos + 4)..];
                uint ttl = BinaryPrimitives.ReadUInt32BigEndian(t);
                uint v = fixedTtl ?? (ttl > elapsed ? ttl - elapsed : 1u);
                if (v == 0) v = 1;
                BinaryPrimitives.WriteUInt32BigEndian(t, v);
            }
            pos += 10 + BinaryPrimitives.ReadUInt16BigEndian(m[(pos + 8)..]);
            if (pos > m.Length) return;
        }
    }

    /// <summary>Builds a SERVFAIL reply (question echoed, no records). Returns null if the query is malformed.</summary>
    public static byte[]? BuildServFail(ReadOnlySpan<byte> query)
    {
        if (!TryGetQuestionEnd(query, out int end)) return null;
        var r = query[..end].ToArray();
        r[2] = (byte)((r[2] & 0x79) | 0x80);         // QR=1, keep opcode/RD, TC=0, AA=0
        r[3] = 0x82;                                 // RA=1, RCODE=2 (SERVFAIL)
        r[6] = r[7] = r[8] = r[9] = r[10] = r[11] = 0;
        return r;
    }
}
