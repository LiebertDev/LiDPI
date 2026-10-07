using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using LiDPI.Dns;

namespace LiDPI.Engines.Divert;

/// <summary>
/// Answers DNS-over-TCP (port 53, RFC 1035 4.2.2 / RFC 7766) entirely at packet level: outbound packets to
/// TCP/53 are consumed and the server side of the connection is emulated with injected inbound segments
/// (SYN-ACK, ACKs, length-prefixed responses, FIN). Queries are resolved through DoH, so neither the
/// connection nor the query ever reaches the (possibly poisoned) network resolver. No local listener is
/// used, so no firewall rule is needed and no port can collide.
/// <para>
/// Deliberately small: no window scaling / SACK / timestamps are offered, the peer's receive window is
/// honoured, unacknowledged data is retransmitted on a timer, idle or broken flows are reset (RST) and dropped.
/// Connections that were already open before the engine started are reset, so the client reconnects through us
/// instead of sending plaintext queries to the network resolver.
/// </para>
/// </summary>
internal sealed class TcpDnsResponder : IDisposable
{
    /// <summary>Sends a fully built inbound packet (checksums still to be computed); <paramref name="template"/> is the address of the client packet the flow was created from.</summary>
    public delegate bool PacketSink(Span<byte> packet, in WinDivertAddress template);

    private const int MaxFlows = 512;
    private const int MaxInbox = 64 * 1024 + 2;
    private const ushort OurWindow = 65535;
    private const int DefaultMss = 536;
    private static readonly long IdleTimeoutMs = 30_000;
    private static readonly long RetransmitMs = 800;
    private const int MaxRetransmits = 5;
    /// <summary>Queries resolving at once per connection; more are answered with SERVFAIL right away.</summary>
    internal const int MaxPendingPerFlow = 16;
    /// <summary>Queued (unsent) answer bytes per connection; beyond that answers become SERVFAIL, then the flow is reset.</summary>
    internal const int MaxOutbox = 128 * 1024;

    private readonly Func<byte[], CancellationToken, Task<byte[]>> _resolve;
    private readonly PacketSink _send;
    private readonly Action<string>? _warn;
    private readonly Action? _answered;
    private readonly CancellationToken _ct;
    private readonly ConcurrentDictionary<FlowKey, Flow> _flows = new();
    private readonly Timer _timer;
    private volatile bool _disposed;

    public TcpDnsResponder(Func<byte[], CancellationToken, Task<byte[]>> resolve, PacketSink send, CancellationToken ct,
        Action<string>? warn = null, Action? answered = null, bool startTimer = true)
    {
        _resolve = resolve;
        _send = send;
        _ct = ct;
        _warn = warn;
        _answered = answered;
        _timer = new Timer(_ => Tick(), null, startTimer ? 250 : Timeout.Infinite, startTimer ? 250 : Timeout.Infinite);
    }

    /// <summary>Number of tracked connections (diagnostics / tests).</summary>
    public int FlowCount => _flows.Count;

    private readonly record struct FlowKey(UInt128 Client, UInt128 Server, ushort ClientPort, ushort ServerPort, bool V6);

    private sealed class Flow
    {
        public required byte[] ClientAddr, ServerAddr;
        public required ushort ClientPort, ServerPort;
        public required WinDivertAddress Addr;
        public uint Irs, Iss, RcvNxt, SndUna, SndNxt;
        public int Mss, SndWnd = OurWindow;
        public readonly List<byte> Inbox = new();
        public readonly List<byte> Unacked = new();   // bytes [SndUna, SndNxt) excluding our FIN
        public readonly List<byte> Outbox = new();    // queued, not yet sent
        public int Pending;
        public bool ClientFin, FinSent, Established;
        /// <summary>Reset by us (RST sent); removed by the next packet or timer tick.</summary>
        public bool Dead;
        public long LastActive, LastSend;
        public int Retransmits;
    }

    private static bool SeqLt(uint a, uint b) => (int)(a - b) < 0;
    private static bool SeqLe(uint a, uint b) => (int)(a - b) <= 0;

    /// <summary>
    /// Handles an outbound TCP packet to port 53. Returns true if it was consumed (must not be reinjected).
    /// Segments of connections this responder does not own (e.g. opened before the engine started) are answered
    /// with RST instead of being passed to the network resolver in plaintext; the client then reconnects to us.
    /// </summary>
    public bool Handle(ReadOnlySpan<byte> pkt, in PacketInfo pi, in WinDivertAddress addr)
    {
        if (_disposed || pi.Protocol != PacketInfo.ProtoTcp) return false;
        var t = pkt[pi.L4Offset..];
        byte flags = t[13];
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(t[4..]);
        uint ack = BinaryPrimitives.ReadUInt32BigEndian(t[8..]);
        ushort wnd = BinaryPrimitives.ReadUInt16BigEndian(t[14..]);
        var key = new FlowKey(PacketInfo.SrcKey(pkt), PacketInfo.DstKey(pkt), pi.SrcPort, pi.DstPort, pi.IsV6);
        long now = Environment.TickCount64;

        if ((flags & PacketBuilder.TcpSyn) != 0 && (flags & PacketBuilder.TcpAck) == 0)
        {
            if (_flows.TryGetValue(key, out var existing))
            {
                lock (existing)
                {
                    if (existing.Irs == seq && !existing.Dead)
                    {
                        // retransmitted SYN: our SYN-ACK was lost (or is late); answer again, never fork the flow
                        if (!existing.Established) SendCtl(existing, existing.Iss, PacketBuilder.TcpSyn | PacketBuilder.TcpAck, withMss: true);
                        return true;
                    }
                }
                _flows.TryRemove(key, out _);   // new connection reusing the 4-tuple
            }
            if (_flows.Count >= MaxFlows)
            {
                Purge(now, force: true);
                if (_flows.Count >= MaxFlows)
                {
                    // full: refuse politely (the client retries), never leak the query to the network resolver
                    SendReset(pkt, pi, addr, seq, ack, flags);
                    return true;
                }
            }
            int mss = PacketBuilder.GetMssOption(pkt, pi);
            var f = new Flow
            {
                ClientAddr = PacketBuilder.SrcAddr(pkt).ToArray(),
                ServerAddr = PacketBuilder.DstAddr(pkt).ToArray(),
                ClientPort = pi.SrcPort,
                ServerPort = pi.DstPort,
                Addr = addr,
                Irs = seq,
                Iss = (uint)RandomNumberGenerator.GetInt32(int.MaxValue),
                Mss = Math.Clamp(mss == 0 ? DefaultMss : mss, 64, 65495),
                LastActive = now,
            };
            f.RcvNxt = seq + 1;
            f.SndUna = f.SndNxt = f.Iss + 1;
            f.SndWnd = wnd;
            _flows[key] = f;
            lock (f) SendCtl(f, f.Iss, PacketBuilder.TcpSyn | PacketBuilder.TcpAck, withMss: true);
            return true;
        }

        if (!_flows.TryGetValue(key, out var flow))
        {
            // unknown connection (pre-existing, already dropped, or reset by us): reset it, never reinject
            if ((flags & PacketBuilder.TcpRst) == 0) SendReset(pkt, pi, addr, seq, ack, flags);
            return true;
        }
        bool remove = false;
        lock (flow)
        {
            flow.LastActive = now;
            if ((flags & PacketBuilder.TcpRst) != 0)
            {
                remove = true;
            }
            else if (flow.Dead)
            {
                SendReset(pkt, pi, addr, seq, ack, flags);
                remove = true;
            }
            else
            {
                if ((flags & PacketBuilder.TcpAck) != 0) OnAck(flow, ack, wnd);

                var payload = pkt.Slice(pi.PayloadOffset, pi.PayloadLength);
                bool fin = (flags & PacketBuilder.TcpFin) != 0;
                bool needAck = false;
                if (payload.Length > 0 || fin)
                {
                    if (SeqLt(seq, flow.RcvNxt))
                    {
                        // (partial) retransmission: keep only the new part
                        uint skip = flow.RcvNxt - seq;
                        if (skip < (uint)payload.Length)
                        {
                            payload = payload[(int)skip..];
                            seq = flow.RcvNxt;
                        }
                        else
                        {
                            // pure duplicate; a FIN right behind the duplicated data is still new
                            bool newFin = fin && skip == (uint)payload.Length && !flow.ClientFin;
                            payload = default;
                            fin = newFin;
                            if (newFin) seq = flow.RcvNxt;
                            else if (flow.Unacked.Count > 0) Retransmit(flow);
                        }
                        needAck = true;
                    }
                    if (seq == flow.RcvNxt)
                    {
                        if (payload.Length > 0)
                        {
                            if (flow.Inbox.Count + payload.Length > MaxInbox) { Abort(flow); remove = true; }
                            else
                            {
                                foreach (byte b in payload) flow.Inbox.Add(b);
                                flow.RcvNxt += (uint)payload.Length;
                                DispatchQueries(flow);
                            }
                        }
                        if (!remove && fin && !flow.ClientFin)
                        {
                            flow.ClientFin = true;
                            flow.RcvNxt++;
                        }
                        needAck = true;
                    }
                    else if (!SeqLt(seq, flow.RcvNxt))
                    {
                        needAck = true;   // out of order (gap): duplicate ACK, client retransmits
                    }
                }
                if (flow.Dead) remove = true;
                if (!remove)
                {
                    Pump(flow);
                    if (!MaybeFinish(flow) && needAck) SendCtl(flow, flow.SndNxt, PacketBuilder.TcpAck, withMss: false);
                    remove = flow.ClientFin && flow.FinSent && flow.SndUna == flow.SndNxt;
                }
            }
        }
        if (remove) _flows.TryRemove(key, out _);
        return true;
    }

    private static void OnAck(Flow f, uint ack, ushort wnd)
    {
        if (!f.Established && ack == f.Iss + 1) f.Established = true;
        if (SeqLt(f.SndUna, ack) && SeqLe(ack, f.SndNxt))
        {
            int acked = (int)(ack - f.SndUna);
            int data = Math.Min(acked, f.Unacked.Count);
            f.Unacked.RemoveRange(0, data);
            f.SndUna = ack;
            f.Retransmits = 0;
        }
        f.SndWnd = wnd;
    }

    private void DispatchQueries(Flow f)
    {
        while (f.Inbox.Count >= 2)
        {
            int len = (f.Inbox[0] << 8) | f.Inbox[1];
            if (f.Inbox.Count < 2 + len) break;
            byte[] q = f.Inbox.GetRange(2, len).ToArray();
            f.Inbox.RemoveRange(0, 2 + len);
            if (len < 12) continue;
            if (f.Pending >= MaxPendingPerFlow)
            {
                // too many queries in flight on one connection: answer this one with SERVFAIL immediately
                if (!Enqueue(f, DnsWire.BuildServFail(q), q)) return;
                continue;
            }
            f.Pending++;
            _ = ResolveAsync(f, q);
        }
    }

    /// <summary>
    /// Queues one length-prefixed answer (under the flow lock). If the outbox is full the answer is replaced by a
    /// (small) SERVFAIL; if even that does not fit, the connection is reset. Returns false once the flow is dead.
    /// </summary>
    private bool Enqueue(Flow f, byte[]? resp, byte[]? query = null)
    {
        if (f.Dead) return false;
        if (resp is null || resp.Length > 0xFFFF) return true;
        if (f.Outbox.Count + 2 + resp.Length > MaxOutbox)
        {
            var servFail = query is null ? null : DnsWire.BuildServFail(query);
            if (servFail is not null && f.Outbox.Count + 2 + servFail.Length <= MaxOutbox)
            {
                resp = servFail;
            }
            else
            {
                _warn?.Invoke(LiDPI.UI.Strings.LogTcpDnsOverflow);
                Abort(f);
                f.Dead = true;
                f.Outbox.Clear();
                return false;
            }
        }
        f.Outbox.Add((byte)(resp.Length >> 8));
        f.Outbox.Add((byte)resp.Length);
        f.Outbox.AddRange(resp);
        return true;
    }

    private async Task ResolveAsync(Flow f, byte[] query)
    {
        byte[]? resp;
        try
        {
            resp = await _resolve(query, _ct).ConfigureAwait(false);
            resp[0] = query[0];
            resp[1] = query[1];
            _answered?.Invoke();
        }
        catch (Exception ex)
        {
            if (_disposed || _ct.IsCancellationRequested) { lock (f) f.Pending--; return; }
            _warn?.Invoke(LiDPI.UI.Strings.LogTcpDnsDohFailed(ex.Message));
            resp = DnsWire.BuildServFail(query);
        }
        lock (f)
        {
            f.Pending--;
            if (_disposed || f.Dead) return;
            if (!Enqueue(f, resp, query)) return;   // reset: the next packet / tick removes the flow
            Pump(f);
            MaybeFinish(f);
        }
    }

    /// <summary>Sends queued data as far as the peer's window allows (called under the flow lock).</summary>
    private void Pump(Flow f)
    {
        if (!f.Established || f.Dead) return;
        while (f.Outbox.Count > 0)
        {
            int room = f.SndWnd - f.Unacked.Count;
            int n = Math.Min(Math.Min(f.Mss, f.Outbox.Count), room);
            if (n <= 0) break;
            var chunk = f.Outbox.GetRange(0, n).ToArray();
            f.Outbox.RemoveRange(0, n);
            SendData(f, f.SndNxt, chunk);
            f.Unacked.AddRange(chunk);
            f.SndNxt += (uint)n;
        }
    }

    /// <summary>After the client's FIN and once every answer is out, closes our side. Returns true if a segment was sent.</summary>
    private bool MaybeFinish(Flow f)
    {
        if (f.Dead || !f.ClientFin || f.FinSent || f.Pending > 0 || f.Outbox.Count > 0) return false;
        f.FinSent = true;
        SendCtl(f, f.SndNxt, PacketBuilder.TcpFin | PacketBuilder.TcpAck, withMss: false);
        f.SndNxt++;
        return true;
    }

    private void Retransmit(Flow f)
    {
        f.LastSend = Environment.TickCount64;
        int off = 0;
        uint seq = f.SndUna;
        while (off < f.Unacked.Count)
        {
            int n = Math.Min(f.Mss, f.Unacked.Count - off);
            SendData(f, seq, f.Unacked.GetRange(off, n).ToArray(), track: false);
            off += n;
            seq += (uint)n;
        }
        if (f.FinSent && f.SndUna != f.SndNxt && off == f.Unacked.Count)
            SendCtl(f, f.SndNxt - 1, PacketBuilder.TcpFin | PacketBuilder.TcpAck, withMss: false);
    }

    private void SendData(Flow f, uint seq, byte[] data, bool track = true)
    {
        if (track) f.LastSend = Environment.TickCount64;
        Emit(f, seq, PacketBuilder.TcpAck | PacketBuilder.TcpPsh, mss: 0, data);
    }

    private void SendCtl(Flow f, uint seq, byte flags, bool withMss)
    {
        if ((flags & (PacketBuilder.TcpSyn | PacketBuilder.TcpFin)) != 0) f.LastSend = Environment.TickCount64;
        ushort mss = withMss ? (ushort)(f.ClientAddr.Length == 16 ? 1440 : 1460) : (ushort)0;
        Emit(f, seq, flags, mss, default);
    }

    /// <summary>Resets the connection towards the client (once) and marks the flow dead. Under the flow lock.</summary>
    private void Abort(Flow f)
    {
        if (f.Dead) return;
        f.Dead = true;
        Emit(f, f.SndNxt, PacketBuilder.TcpRst | PacketBuilder.TcpAck, 0, default);
    }

    /// <summary>
    /// RST for a client segment that has no (live) flow, per RFC 793 "reset generation": with ACK set the reset carries
    /// SEQ = SEG.ACK; otherwise SEQ = 0, ACK = SEG.SEQ + SEG.LEN (SYN/FIN count as one).
    /// </summary>
    private void SendReset(ReadOnlySpan<byte> pkt, in PacketInfo pi, in WinDivertAddress addr, uint seq, uint ack, byte flags)
    {
        uint rseq, rack;
        byte rflags;
        if ((flags & PacketBuilder.TcpAck) != 0)
        {
            rseq = ack;
            rack = 0;
            rflags = PacketBuilder.TcpRst;
        }
        else
        {
            rseq = 0;
            rack = seq + (uint)pi.PayloadLength
                   + ((flags & PacketBuilder.TcpSyn) != 0 ? 1u : 0u) + ((flags & PacketBuilder.TcpFin) != 0 ? 1u : 0u);
            rflags = PacketBuilder.TcpRst | PacketBuilder.TcpAck;
        }
        Span<byte> arr = stackalloc byte[40 + 24];
        int n = PacketBuilder.BuildTcp(arr, PacketBuilder.DstAddr(pkt), PacketBuilder.SrcAddr(pkt), pi.DstPort, pi.SrcPort,
            rseq, rack, rflags, 0, 0, default);
        if (n > 0) _send(arr[..n], addr);
    }

    /// <summary>Engine stop: resets every emulated connection while packets can still be sent, then forgets them.</summary>
    public void AbortAll()
    {
        foreach (var (key, f) in _flows)
        {
            lock (f) Abort(f);
            _flows.TryRemove(key, out _);
        }
    }

    private void Emit(Flow f, uint seq, byte flags, ushort mss, ReadOnlySpan<byte> payload)
    {
        byte[] arr = new byte[40 + 24 + payload.Length];
        int n = PacketBuilder.BuildTcp(arr, f.ServerAddr, f.ClientAddr, f.ServerPort, f.ClientPort,
            seq, f.RcvNxt, flags, OurWindow, mss, payload);
        if (n > 0) _send(arr.AsSpan(0, n), f.Addr);
    }

    /// <summary>Timer: retransmits unacknowledged data / SYN-ACK / FIN, drops idle flows.</summary>
    internal void Tick()
    {
        if (_disposed) return;
        long now = Environment.TickCount64;
        foreach (var (key, f) in _flows)
        {
            bool drop = false;
            lock (f)
            {
                if (f.Dead) drop = true;
                else if (now - f.LastActive > IdleTimeoutMs) { Abort(f); drop = true; }
                else if (now - f.LastSend > RetransmitMs)
                {
                    bool outstanding = !f.Established || f.SndUna != f.SndNxt;
                    if (outstanding)
                    {
                        if (++f.Retransmits > MaxRetransmits) { Abort(f); drop = true; }
                        else if (!f.Established) SendCtl(f, f.Iss, PacketBuilder.TcpSyn | PacketBuilder.TcpAck, withMss: true);
                        else Retransmit(f);
                    }
                }
            }
            if (drop) _flows.TryRemove(key, out _);
        }
    }

    private void Purge(long now, bool force)
    {
        foreach (var (key, f) in _flows)
        {
            if (!(f.Dead || (force ? now - f.LastActive > 5000 : now - f.LastActive > IdleTimeoutMs))) continue;
            lock (f) Abort(f);
            _flows.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        _flows.Clear();
    }
}
