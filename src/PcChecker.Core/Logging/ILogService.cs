using Microsoft.Extensions.Logging;

namespace PcChecker.Core.Logging;

/// <summary>Owns the logging pipeline: file sink, in-memory buffer for the Logs page, export.</summary>
public interface ILogService
{
    /// <summary>Rebuilds the logger with new settings (levels, file options).</summary>
    void Reconfigure(LoggingSettings settings);

    /// <summary>Copy of the recent in-memory entries.</summary>
    IReadOnlyList<LogEntry> Snapshot();

    event EventHandler<LogEntry>? EntryLogged;

    /// <summary>Clears the in-memory buffer (files are untouched).</summary>
    void Clear();

    string LogDirectory { get; }

    /// <summary>Zips the log directory to <paramref name="targetPath"/> and returns the written path.</summary>
    Task<string> ExportZipAsync(string targetPath, CancellationToken cancellationToken);

    ILogger<T> CreateLogger<T>();

    /// <summary>Creates a logger with an explicit category name (one of <see cref="LogCategories"/>).</summary>
    ILogger CreateLogger(string category);
}
