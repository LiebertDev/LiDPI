using LiDPI.Engines.Divert;
using LiDPI.Engines.Proxy;

namespace LiDPI.Core;

/// <summary>Creates the concrete engine for a mode.</summary>
public static class EngineFactory
{
    public static IBypassEngine Create(EngineMode m) => m == EngineMode.Divert ? new DivertEngine() : new ProxyEngine();
}
