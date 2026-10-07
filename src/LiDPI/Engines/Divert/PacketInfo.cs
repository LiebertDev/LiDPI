using System.Buffers.Binary;

namespace LiDPI.Engines.Divert;

/// <summary>Parsed offsets of an IPv4/IPv6 + TCP/UDP packet. Pure value type, no allocations.</summary>
internal struct PacketInfo
{
    public const byte ProtoTcp = 6;
    public const byte ProtoUdp = 17;

    public bool IsV6;
    public byte Protocol;
    /// <summary>Offset of the TCP/UDP header (= IP header incl. options / extension headers).</summary>
    public int L4Offset;
    /// <summary>Offset of the transport payload.</summary>
    public int PayloadOffset;
    public int PayloadLength;
    /// <summary>Length according to the IP header (may be less than the captured buffer).</summary>
    public int TotalLength;
    public ushort SrcPort;
    public ushort DstPort;

    /// <summary>Parses the packet; false for fragments, unknown protocols or malformed headers.</summary>
    public static bool TryParse(ReadOnlySpan<byte> p, out PacketInfo info)
    {
        info = default;
        if (p.Length < 20) return false;
        int version = p[0] >> 4;
        int l4, total;
        byte proto;

        if (version == 4)
        {
            int ihl = (p[0] & 0x0F) * 4;
            total = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
            if (ihl < 20 || total < ihl || total > p.Length) return false;
            ushort frag = BinaryPrimitives.ReadUInt16BigEndian(p[6..]);
            if ((frag & 0x3FFF) != 0) return false;           // MF set or offset != 0
            proto = p[9];
            l4 = ihl;
        }
        else if (version == 6)
        {
            if (p.Length < 40) return false;
            total = 40 + BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
            if (total > p.Length) return false;
            proto = p[6];
            l4 = 40;
            for (int guard = 0; guard < 8; guard++)
            {
                if (proto is 0 or 43 or 60)                    // hop-by-hop, routing, destination options
                {
                    if (l4 + 8 > total) return false;
                    proto = p[l4];
                    l4 += (p[l4 + 1] + 1) * 8;
                }
                else if (proto == 51)                          // AH
                {
                    if (l4 + 8 > total) return false;
                    proto = p[l4];
                    l4 += (p[l4 + 1] + 2) * 4;
                }
                else break;                                    // includes 44 (fragment) -> rejected below
            }
            if (l4 > total) return false;
            info.IsV6 = true;
        }
        else return false;

        int payloadOffset;
        if (proto == ProtoTcp)
        {
            if (l4 + 20 > total) return false;
            int doff = (p[l4 + 12] >> 4) * 4;
            if (doff < 20 || l4 + doff > total) return false;
            payloadOffset = l4 + doff;
        }
        else if (proto == ProtoUdp)
        {
            if (l4 + 8 > total) return false;
            payloadOffset = l4 + 8;
        }
        else return false;

        info.Protocol = proto;
        info.L4Offset = l4;
        info.PayloadOffset = payloadOffset;
        info.PayloadLength = total - payloadOffset;
        info.TotalLength = total;
        info.SrcPort = BinaryPrimitives.ReadUInt16BigEndian(p[l4..]);
        info.DstPort = BinaryPrimitives.ReadUInt16BigEndian(p[(l4 + 2)..]);
        return true;
    }

    /// <summary>Rewrites the IP length field(s) for a packet of <paramref name="newTotal"/> bytes.</summary>
    public readonly void SetTotalLength(Span<byte> p, int newTotal)
    {
        if (IsV6) BinaryPrimitives.WriteUInt16BigEndian(p[4..], (ushort)(newTotal - 40));
        else BinaryPrimitives.WriteUInt16BigEndian(p[2..], (ushort)newTotal);
        if (Protocol == ProtoUdp)
            BinaryPrimitives.WriteUInt16BigEndian(p[(L4Offset + 4)..], (ushort)(newTotal - L4Offset));
    }

    public readonly uint GetSeq(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt32BigEndian(p[(L4Offset + 4)..]);
    public readonly void SetSeq(Span<byte> p, uint seq) => BinaryPrimitives.WriteUInt32BigEndian(p[(L4Offset + 4)..], seq);

    /// <summary>Sets IPv4 TTL or IPv6 hop limit.</summary>
    public readonly void SetTtl(Span<byte> p, byte ttl)
    {
        if (IsV6) p[7] = ttl; else p[8] = ttl;
    }

    public static byte GetTtl(ReadOnlySpan<byte> p) => (p[0] >> 4) == 6 ? p[7] : p[8];

    /// <summary>Adds <paramref name="delta"/> to the IPv4 identification field (no-op for IPv6).</summary>
    public readonly void BumpIpId(Span<byte> p, int delta)
    {
        if (IsV6 || delta == 0) return;
        ushort id = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
        BinaryPrimitives.WriteUInt16BigEndian(p[4..], (ushort)(id + delta));
    }

    /// <summary>Destination address as a 128-bit key (IPv4 addresses occupy the low 32 bits).</summary>
    public static UInt128 DstKey(ReadOnlySpan<byte> p) => (p[0] >> 4) == 6
        ? BinaryPrimitives.ReadUInt128BigEndian(p.Slice(24, 16))
        : BinaryPrimitives.ReadUInt32BigEndian(p.Slice(16, 4));

    /// <summary>Source address as a 128-bit key (IPv4 addresses occupy the low 32 bits).</summary>
    public static UInt128 SrcKey(ReadOnlySpan<byte> p) => (p[0] >> 4) == 6
        ? BinaryPrimitives.ReadUInt128BigEndian(p.Slice(8, 16))
        : BinaryPrimitives.ReadUInt32BigEndian(p.Slice(12, 4));
}
