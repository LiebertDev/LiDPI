namespace LiDPI.Core;

/// <summary>Lifecycle state of a bypass engine.</summary>
public enum EngineState { Stopped, Starting, Running, Stopping, Error }

/// <summary>Which engine implementation is used.</summary>
public enum EngineMode
{
    /// <summary>WinDivert packet engine (all apps).</summary>
    Divert,
    /// <summary>Local HTTP proxy (browsers).</summary>
    Proxy,
}

/// <summary>How DNS queries are resolved.</summary>
public enum DnsMode { System, DoH }
