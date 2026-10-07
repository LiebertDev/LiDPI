namespace LiDPI.Core;

/// <summary>A single log line emitted by an engine or the controller.</summary>
/// <param name="Time">Local timestamp.</param>
/// <param name="Level">"Info", "Warn", "Error" or "Debug".</param>
/// <param name="Message">Text.</param>
public sealed record LogEntry(DateTime Time, string Level, string Message);
