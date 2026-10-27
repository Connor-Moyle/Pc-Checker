using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace PcChecker.Core.Logging;

/// <summary>
/// Builds a Serilog pipeline from <see cref="LoggingSettings"/>: a rolling file sink, a debug sink and the
/// <see cref="InMemoryLogSink"/> that feeds the Logs page, bridged to <see cref="ILogger{T}"/> through a
/// <see cref="ReconfigurableLoggerFactory"/> so existing loggers follow every <see cref="Reconfigure"/>.
/// </summary>
public sealed class SerilogLogService : ILogService, IDisposable
{
    private readonly object _sync = new();
    private readonly InMemoryLogSink _memorySink;
    private readonly ReconfigurableLoggerFactory _factory;
    private LoggingSettings _settings;
    private Logger _serilog;
    private IReadOnlyList<IDisposable> _childLoggers;
    private bool _disposed;

    public SerilogLogService()
        : this(new LoggingSettings())
    {
    }

    public SerilogLogService(LoggingSettings settings)
    {
        _settings = settings ?? new LoggingSettings();
        _memorySink = new InMemoryLogSink(_settings.InAppBufferSize, _settings.RedactSerialNumbers);
        _memorySink.EntryLogged += (_, e) => EntryLogged?.Invoke(this, e);
        (_serilog, _childLoggers) = Build(_settings, _memorySink);
        _factory = new ReconfigurableLoggerFactory(_serilog);
        LogDirectory = _settings.Directory;
        Export = new DiagnosticsExportOptions();
    }

    public string LogDirectory { get; private set; }

    /// <summary>The settings the pipeline was last built with.</summary>
    public LoggingSettings CurrentSettings
    {
        get
        {
            lock (_sync)
            {
                return _settings;
            }
        }
    }

    /// <summary>Extra files and facts bundled by <see cref="ExportZipAsync"/>; the app fills these at startup.</summary>
    public DiagnosticsExportOptions Export { get; }

    /// <summary>The factory to register as <see cref="ILoggerFactory"/> so third-party code logs through the same pipeline.</summary>
    public ILoggerFactory LoggerFactory => _factory;

    /// <summary>Number of entries currently held by the in-app buffer.</summary>
    public int BufferedCount => _memorySink.Count;

    public event EventHandler<LogEntry>? EntryLogged;

    public void Reconfigure(LoggingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Logger old;
        IReadOnlyList<IDisposable> oldChildren;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            old = _serilog;
            oldChildren = _childLoggers;
            _settings = settings;
            _memorySink.Capacity = settings.InAppBufferSize;
            _memorySink.RedactSerials = settings.RedactSerialNumbers;
            (_serilog, _childLoggers) = Build(settings, _memorySink);
            _factory.Swap(_serilog);
            LogDirectory = settings.Directory;
        }

        // Dispose the previous pipeline after the swap so no line is lost in between.
        old.Dispose();
        foreach (var child in oldChildren)
        {
            SafeDispose(child);
        }
    }

    public IReadOnlyList<LogEntry> Snapshot() => _memorySink.Snapshot();

    public void Clear() => _memorySink.Clear();

    public ILogger<T> CreateLogger<T>() => _factory.CreateLogger<T>();

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string category) => _factory.CreateLogger(category);

    /// <summary>Path and size of the newest log file, or null when nothing has been written yet.</summary>
    public (string Path, long SizeBytes)? CurrentLogFile()
    {
        try
        {
            var dir = LogDirectory;
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                return null;
            }

            var newest = new DirectoryInfo(dir)
                .EnumerateFiles("pcchecker-*.*")
                .Where(f => f.Extension is ".log" or ".json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return newest is null ? null : (newest.FullName, newest.Length);
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> ExportZipAsync(string targetPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var dir = LogDirectory;
        var settings = CurrentSettings;
        var export = Export;
        await Task.Run(() =>
        {
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // The file sink is unbuffered, so the files on disk already hold every line written so far.
            // FileMode.Create replaces a file the user chose to overwrite in the save dialog (ZipFile.Open would refuse it).
            using var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddFile(zip, file, DiagnosticsExport.LogsFolder + "/" + Path.GetFileName(file), settings.RedactSerialNumbers);
                }
            }

            if (!string.IsNullOrWhiteSpace(export.JournalPath) && File.Exists(export.JournalPath))
            {
                AddFile(zip, export.JournalPath, DiagnosticsExport.JournalEntry, settings.RedactSerialNumbers);
            }

            if (!string.IsNullOrWhiteSpace(export.SettingsFilePath) && File.Exists(export.SettingsFilePath))
            {
                var json = ReadShared(export.SettingsFilePath);
                WriteText(zip, DiagnosticsExport.SettingsEntry, DiagnosticsExport.StripSecrets(json));
            }

            var buffer = new System.Text.StringBuilder();
            foreach (var e in _memorySink.Snapshot())
            {
                buffer.Append(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(e.Level).Append("] [").Append(e.Category).Append("] ").AppendLine(e.Message);
                if (e.Exception is not null)
                {
                    buffer.AppendLine(e.Exception);
                }
            }

            WriteText(zip, DiagnosticsExport.BufferEntry, buffer.ToString());
            WriteText(zip, DiagnosticsExport.SummaryEntry,
                DiagnosticsExport.BuildSystemSummary(export.AppVersion ?? "unknown", settings, DateTimeOffset.Now, export.ExtraNote));
        }, cancellationToken).ConfigureAwait(false);

        return targetPath;
    }

    /// <summary>Maps a settings level to the Serilog level.</summary>
    public static LogEventLevel ToSerilogLevel(LogLevelSetting level) => level switch
    {
        LogLevelSetting.Verbose => LogEventLevel.Verbose,
        LogLevelSetting.Debug => LogEventLevel.Debug,
        LogLevelSetting.Information => LogEventLevel.Information,
        LogLevelSetting.Warning => LogEventLevel.Warning,
        LogLevelSetting.Error => LogEventLevel.Error,
        _ => LogEventLevel.Fatal,
    };

    public static LogLevelSetting FromSerilogLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevelSetting.Verbose,
        LogEventLevel.Debug => LogLevelSetting.Debug,
        LogEventLevel.Information => LogLevelSetting.Information,
        LogEventLevel.Warning => LogLevelSetting.Warning,
        LogEventLevel.Error => LogLevelSetting.Error,
        _ => LogLevelSetting.Fatal,
    };

    /// <summary>Returns the effective minimum level for a category, honouring per-category overrides (by category name or namespace prefix).</summary>
    public static LogLevelSetting EffectiveLevel(LoggingSettings settings, string category)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.CategoryLevels is { Count: > 0 })
        {
            if (settings.CategoryLevels.TryGetValue(category, out var level))
            {
                return level;
            }

            var prefix = LogCategoryResolver.ToNamespacePrefix(category);
            foreach (var pair in settings.CategoryLevels)
            {
                if (pair.Key.Contains('.') && (prefix.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)
                    || prefix.StartsWith(pair.Key + ".", StringComparison.OrdinalIgnoreCase)))
                {
                    return pair.Value;
                }
            }
        }

        return settings.MinimumLevel;
    }

    /// <summary>The lowest level any category wants: the pipeline minimum, so category overrides can go below the global level.</summary>
    public static LogLevelSetting PipelineFloor(LoggingSettings settings)
    {
        var floor = settings.MinimumLevel;
        if (settings.CategoryLevels is not null)
        {
            foreach (var level in settings.CategoryLevels.Values)
            {
                if (level < floor)
                {
                    floor = level;
                }
            }
        }

        return floor;
    }

    private static (Logger Main, IReadOnlyList<IDisposable> Children) Build(LoggingSettings settings, InMemoryLogSink memorySink)
    {
        var children = new List<IDisposable>();
        var targets = new List<ILogEventSink> { memorySink };

        if (settings.FileEnabled && !string.IsNullOrWhiteSpace(settings.Directory))
        {
            try
            {
                Directory.CreateDirectory(settings.Directory);
                var fileLogger = BuildFileLogger(settings);
                children.Add(fileLogger);
                targets.Add(fileLogger);
            }
            catch
            {
                // If the directory cannot be created we still keep the in-memory sink.
            }
        }

        try
        {
            var debugLogger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Debug().CreateLogger();
            children.Add(debugLogger);
            targets.Add(debugLogger);
        }
        catch
        {
            // The debug sink is a convenience only.
        }

        var main = new LoggerConfiguration()
            .MinimumLevel.Is(ToSerilogLevel(PipelineFloor(settings)))
            .Enrich.FromLogContext()
            .Enrich.With(new CategoryEnricher())
            .Filter.ByIncludingOnly(evt => IsEnabled(settings, evt))
            .WriteTo.Sink(new RedactingFanOutSink(targets, settings.RedactSerialNumbers))
            .CreateLogger();
        return (main, children);
    }

    private static Logger BuildFileLogger(LoggingSettings settings)
    {
        var interval = settings.RollingInterval switch
        {
            LogRollingInterval.Hour => RollingInterval.Hour,
            LogRollingInterval.Day => RollingInterval.Day,
            _ => RollingInterval.Infinite,
        };
        long sizeLimit = Math.Max(1, settings.FileSizeLimitMb) * 1024L * 1024L;
        int retained = Math.Max(1, settings.RetainedFileCount);
        var config = new LoggerConfiguration().MinimumLevel.Verbose();
        if (settings.JsonFormat)
        {
            config = config.WriteTo.File(new JsonFormatter(), Path.Combine(settings.Directory, "pcchecker-.json"),
                rollingInterval: interval, fileSizeLimitBytes: sizeLimit, retainedFileCountLimit: retained,
                rollOnFileSizeLimit: true, shared: true);
        }
        else
        {
            config = config.WriteTo.File(Path.Combine(settings.Directory, "pcchecker-.log"),
                rollingInterval: interval, fileSizeLimitBytes: sizeLimit, retainedFileCountLimit: retained,
                rollOnFileSizeLimit: true, shared: true,
                outputTemplate: string.IsNullOrWhiteSpace(settings.OutputTemplate) ? LoggingSettings.DefaultOutputTemplate : settings.OutputTemplate);
        }

        return config.CreateLogger();
    }

    private static bool IsEnabled(LoggingSettings settings, LogEvent evt)
    {
        var category = evt.Properties.TryGetValue("Category", out var c) && c is ScalarValue { Value: string s }
            ? s
            : LogCategoryResolver.FromSourceContext(InMemoryLogSink.SourceContextOf(evt));
        return evt.Level >= ToSerilogLevel(EffectiveLevel(settings, category));
    }

    private static void AddFile(ZipArchive zip, string file, string entryName, bool redact)
    {
        try
        {
            if (redact)
            {
                WriteText(zip, entryName, LogRedaction.RedactSerials(ReadShared(file)));
                return;
            }

            using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var target = entry.Open();
            source.CopyTo(target);
        }
        catch (IOException)
        {
            // A file locked by another process is skipped rather than failing the whole export.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WriteText(ZipArchive zip, string entryName, string text)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }

    private static void SafeDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch
        {
            // Disposing a sink must never fail the app.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            _factory.Dispose();
            _serilog.Dispose();
            foreach (var child in _childLoggers)
            {
                SafeDispose(child);
            }
        }
    }

    /// <summary>Adds a short "Category" property derived from SourceContext so output templates can print it.</summary>
    private sealed class CategoryEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var category = LogCategoryResolver.FromSourceContext(InMemoryLogSink.SourceContextOf(logEvent));
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("Category", category));
        }
    }
}

/// <summary>What the diagnostics zip bundles besides the log folder. Filled by the app at startup.</summary>
public sealed class DiagnosticsExportOptions
{
    /// <summary>Path of the action journal to include.</summary>
    public string? JournalPath { get; set; }

    /// <summary>Path of settings.json; secrets are stripped before it is added.</summary>
    public string? SettingsFilePath { get; set; }

    /// <summary>Version written to the summary.</summary>
    public string? AppVersion { get; set; }

    /// <summary>Optional note appended to the summary (for example the app mode).</summary>
    public string? ExtraNote { get; set; }
}
