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
