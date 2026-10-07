using System.Buffers.Binary;

namespace LiDPI.Engines.Divert;

/// <summary>
/// Pure packet transforms used by <see cref="DivertEngine"/> (no I/O, no WinDivert calls). Checksums are
/// left to the sender (WinDivertHelperCalcChecksums) unless stated otherwise. IPv4 and IPv6 (including
/// extension headers already present in a template packet) are supported.
/// </summary>
internal static class PacketBuilder
{
    public const byte TcpFin = 0x01, TcpSyn = 0x02, TcpRst = 0x04, TcpPsh = 0x08, TcpAck = 0x10;
    public const byte ReplyTtl = 64;

    /// <summary>
    /// Writes payload[start..start+len] of <paramref name="pkt"/> with the original headers into
    /// <paramref name="dst"/>, fixing IP/UDP lengths, the TCP sequence number and the IPv4 ID. Returns the length.
    /// </summary>
    public static int BuildSegment(ReadOnlySpan<byte> pkt, in PacketInfo pi, int start, int len, int ipIdDelta, Span<byte> dst)
    {
        int hdr = pi.PayloadOffset;
        var seg = dst[..(hdr + len)];
        pkt[..hdr].CopyTo(seg);
        pkt.Slice(hdr + start, len).CopyTo(seg[hdr..]);
        pi.SetTotalLength(seg, seg.Length);
        if (pi.Protocol == PacketInfo.ProtoTcp) pi.SetSeq(seg, pi.GetSeq(pkt) + (uint)start);
        pi.BumpIpId(seg, ipIdDelta);
        return seg.Length;
    }

    /// <summary>
    /// Writes a decoy ClientHello with the headers of <paramref name="pkt"/>, the given TTL / hop limit and
    /// (optionally) a sequence number far behind the real one. Returns the length, or 0 if it cannot be built.
    /// </summary>
    public static int BuildFake(ReadOnlySpan<byte> pkt, in PacketInfo pi, byte ttl, bool wrongSeq, Span<byte> dst)
    {
        int hdr = pi.PayloadOffset;
        pkt[..hdr].CopyTo(dst);
        int fakeLen = TlsClientHello.BuildFake(dst[hdr..], pi.PayloadLength);
        if (fakeLen <= 0) return 0;
        var fake = dst[..(hdr + fakeLen)];
        pi.SetTotalLength(fake, fake.Length);
        pi.SetTtl(fake, ttl);
        if (wrongSeq) pi.SetSeq(fake, pi.GetSeq(pkt) - 10000u);
        return fake.Length;
    }

    /// <summary>Flips bits of an already computed TCP checksum so the receiver discards the segment.</summary>
    public static void CorruptTcpChecksum(Span<byte> pkt, in PacketInfo pi)
    {
        var cs = pkt.Slice(pi.L4Offset + 16, 2);
        BinaryPrimitives.WriteUInt16BigEndian(cs, (ushort)(BinaryPrimitives.ReadUInt16BigEndian(cs) ^ 0xA5A5));
    }

    /// <summary>Source address bytes (4 or 16) of an IPv4/IPv6 packet.</summary>
    public static ReadOnlySpan<byte> SrcAddr(ReadOnlySpan<byte> p) => (p[0] >> 4) == 6 ? p.Slice(8, 16) : p.Slice(12, 4);

    /// <summary>Destination address bytes (4 or 16) of an IPv4/IPv6 packet.</summary>
    public static ReadOnlySpan<byte> DstAddr(ReadOnlySpan<byte> p) => (p[0] >> 4) == 6 ? p.Slice(24, 16) : p.Slice(16, 4);

    /// <summary>Writes a fresh IPv4 (20 B, DF) or IPv6 (40 B) header; returns its length.</summary>
    private static int WriteIpHeader(Span<byte> o, ReadOnlySpan<byte> src, ReadOnlySpan<byte> dst, byte proto, int total)
    {
        bool v6 = src.Length == 16;
        int ipLen = v6 ? 40 : 20;
        o[..ipLen].Clear();
        if (v6)
        {
            o[0] = 0x60;
            BinaryPrimitives.WriteUInt16BigEndian(o[4..], (ushort)(total - 40));
            o[6] = proto;
            o[7] = ReplyTtl;
            src.CopyTo(o[8..]);
            dst.CopyTo(o[24..]);
        }
        else
        {
            o[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(o[2..], (ushort)total);
            BinaryPrimitives.WriteUInt16BigEndian(o[4..], (ushort)Random.Shared.Next(0x10000));
            o[6] = 0x40;                                   // DF
            o[8] = ReplyTtl;
            o[9] = proto;
            src.CopyTo(o[12..]);
            dst.CopyTo(o[16..]);
        }
        return ipLen;
    }

    /// <summary>
    /// Builds a UDP datagram from <paramref name="src"/>:<paramref name="srcPort"/> to
    /// <paramref name="dst"/>:<paramref name="dstPort"/> (addresses of 4 or 16 bytes). Returns the length,
    /// or 0 if it does not fit.
    /// </summary>
    public static int BuildUdp(Span<byte> o, ReadOnlySpan<byte> src, ReadOnlySpan<byte> dst, ushort srcPort, ushort dstPort, ReadOnlySpan<byte> payload)
    {
        int ipLen = src.Length == 16 ? 40 : 20;
        int total = ipLen + 8 + payload.Length;
        if (total > o.Length || 8 + payload.Length > 0xFFFF || (ipLen == 20 && total > 0xFFFF)) return 0;
        WriteIpHeader(o, src, dst, PacketInfo.ProtoUdp, total);
        var udp = o[ipLen..];
        BinaryPrimitives.WriteUInt16BigEndian(udp, srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[2..], dstPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp[4..], (ushort)(8 + payload.Length));
        udp[6] = udp[7] = 0;
        payload.CopyTo(udp[8..]);
        return total;
    }

    /// <summary>Builds the reply datagram to a captured outbound UDP packet (addresses and ports swapped).</summary>
    public static int BuildUdpReply(ReadOnlySpan<byte> query, in PacketInfo pi, ReadOnlySpan<byte> payload, Span<byte> o) =>
        BuildUdp(o, DstAddr(query), SrcAddr(query), pi.DstPort, pi.SrcPort, payload);

    /// <summary>
    /// Builds a TCP segment (no options, or a single MSS option when <paramref name="mss"/> is non-zero).
    /// Returns the length, or 0 if it does not fit.
    /// </summary>
    public static int BuildTcp(Span<byte> o, ReadOnlySpan<byte> src, ReadOnlySpan<byte> dst, ushort srcPort, ushort dstPort,
        uint seq, uint ack, byte flags, ushort window, ushort mss, ReadOnlySpan<byte> payload)
    {
        int ipLen = src.Length == 16 ? 40 : 20;
        int tcpLen = mss != 0 ? 24 : 20;
        int total = ipLen + tcpLen + payload.Length;
        if (total > o.Length || total - (ipLen == 40 ? 40 : 0) > 0xFFFF) return 0;
        WriteIpHeader(o, src, dst, PacketInfo.ProtoTcp, total);
        var t = o[ipLen..];
        t[..tcpLen].Clear();
        BinaryPrimitives.WriteUInt16BigEndian(t, srcPort);
        BinaryPrimitives.WriteUInt16BigEndian(t[2..], dstPort);
        BinaryPrimitives.WriteUInt32BigEndian(t[4..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(t[8..], (flags & TcpAck) != 0 ? ack : 0);
        t[12] = (byte)((tcpLen / 4) << 4);
        t[13] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(t[14..], window);
        if (mss != 0)
        {
            t[20] = 2;
            t[21] = 4;
            BinaryPrimitives.WriteUInt16BigEndian(t[22..], mss);
        }
        payload.CopyTo(t[tcpLen..]);
        return total;
    }

    /// <summary>MSS option of a TCP SYN, or 0 if absent / malformed.</summary>
    public static int GetMssOption(ReadOnlySpan<byte> pkt, in PacketInfo pi)
    {
        var opts = pkt[(pi.L4Offset + 20)..pi.PayloadOffset];
        int i = 0;
        while (i < opts.Length)
        {
            byte kind = opts[i];
            if (kind == 0) break;
            if (kind == 1) { i++; continue; }
            if (i + 1 >= opts.Length) break;
            int len = opts[i + 1];
            if (len < 2 || i + len > opts.Length) break;
            if (kind == 2 && len == 4) return BinaryPrimitives.ReadUInt16BigEndian(opts[(i + 2)..]);
            i += len;
        }
        return 0;
    }
}
