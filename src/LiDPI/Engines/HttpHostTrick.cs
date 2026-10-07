namespace LiDPI.Engines;

/// <summary>
/// Length-preserving Host header obfuscation for plain HTTP requests:
/// <c>"Host: example.com\r\n"</c> becomes <c>"hoSt:example.com \r\n"</c> (header names are
/// case-insensitive and trailing whitespace is optional whitespace per RFC 9110/9112).
/// </summary>
internal static class HttpHostTrick
{
    private static readonly byte[][] Methods =
    [
        "GET "u8.ToArray(), "POST "u8.ToArray(), "HEAD "u8.ToArray(), "PUT "u8.ToArray(),
        "DELETE "u8.ToArray(), "OPTIONS "u8.ToArray(), "PATCH "u8.ToArray(),
    ];

    /// <summary>True if the payload starts with a common HTTP request method.</summary>
    public static bool LooksLikeRequest(ReadOnlySpan<byte> p)
    {
        foreach (var m in Methods)
            if (p.StartsWith(m)) return true;
        return false;
    }

    /// <summary>
    /// Applies the trick in place. <paramref name="splitAt"/> receives an offset inside the host value
    /// (in [1, len-1]) suitable for segmenting, or -1. Returns false if no Host header was found.
    /// </summary>
    public static bool TryApply(Span<byte> p, out int splitAt)
    {
        splitAt = -1;
        int name = FindHostHeader(p);
        if (name < 0) return false;

        p[name] = (byte)'h'; p[name + 1] = (byte)'o'; p[name + 2] = (byte)'S'; p[name + 3] = (byte)'t';
        int v = name + 5;                         // first byte after ':'
        if (v >= p.Length) return true;

        int cr = p[v..].IndexOf((byte)'\r');
        int lineEnd = cr < 0 ? -1 : v + cr;
        int valueStart = v, valueEnd = lineEnd < 0 ? p.Length : lineEnd;

        if (p[v] == (byte)' ' && lineEnd > v + 1)
        {
            // shift the value one byte left and put the space before CR
            p.Slice(v + 1, lineEnd - v - 1).CopyTo(p.Slice(v));
            p[lineEnd - 1] = (byte)' ';
            valueEnd = lineEnd - 1;
        }

        int mid = valueStart + (valueEnd - valueStart) / 2;
        if (mid > 0 && mid < p.Length) splitAt = mid;
        return true;
    }

    /// <summary>Index of the 'H' of a "\r\nHost:" header (case-insensitive) within the head, or -1.</summary>
    private static int FindHostHeader(ReadOnlySpan<byte> p)
    {
        int headEnd = p.IndexOf("\r\n\r\n"u8);
        int limit = headEnd < 0 ? p.Length : headEnd + 2;
        for (int i = 0; i + 7 <= limit; i++)
        {
            if (p[i] != '\r' || p[i + 1] != '\n') continue;
            if ((p[i + 2] | 0x20) == 'h' && (p[i + 3] | 0x20) == 'o' && (p[i + 4] | 0x20) == 's' &&
                (p[i + 5] | 0x20) == 't' && p[i + 6] == ':')
                return i + 2;
        }
        return -1;
    }
}
