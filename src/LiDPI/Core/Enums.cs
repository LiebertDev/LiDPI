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

/// <summary>How DNS queries are resolved. Persisted by name; new members go at the end (numeric values stay stable).</summary>
public enum DnsMode
{
    /// <summary>Untouched system resolver.</summary>
    System,
    /// <summary>DNS-over-HTTPS (encrypted).</summary>
    DoH,
    /// <summary>
    /// "Hızlı DNS": the packet engine NATs plain UDP/53 queries to a resolver on a non-standard port
    /// (<see cref="FastDns"/>) — fast, but unencrypted. The proxy engine treats it as DoH.
    /// </summary>
    UdpRedirect,
}

/// <summary>What game protection does when a game with a kernel anti-cheat starts while the packet engine runs.</summary>
public enum GameAction
{
    /// <summary>Stop the engine (the driver is unloaded).</summary>
    Pause,
    /// <summary>Switch to the browser-only proxy mode (no driver).</summary>
    SwitchToBrowser,
    /// <summary>Only tell the user.</summary>
    Nothing,
}
