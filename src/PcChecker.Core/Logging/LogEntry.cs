namespace PcChecker.Core.Logging;

/// <summary>One log line kept in the in-app buffer.</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevelSetting Level, string Category, string Message, string? Exception);
