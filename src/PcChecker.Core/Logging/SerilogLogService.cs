using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Json;

namespace PcChecker.Core.Logging;

/// <summary>
/// Builds a Serilog pipeline from <see cref="LoggingSettings"/>: rolling file sink, an in-memory ring buffer for the
/// Logs page, and a bridge so services can inject <see cref="ILogger{T}"/>. Can be reconfigured at runtime.
/// </summary>
public sealed class SerilogLogService : ILogService, IDisposable
{
    private readonly object _sync = new();
    private readonly InMemorySink _memorySink;
    private LoggingSettings _settings;
    private Logger _serilog;
    private SerilogLoggerFactory _factory;
    private bool _disposed;

    public SerilogLogService()
        : this(new LoggingSettings())
    {
    }

    public SerilogLogService(LoggingSettings settings)
    {
        _settings = settings ?? new LoggingSettings();
        _memorySink = new InMemorySink(Math.Max(100, _settings.InAppBufferSize));
        _memorySink.EntryLogged += (_, e) => EntryLogged?.Invoke(this, e);
        _serilog = Build(_settings, _memorySink);
        _factory = new SerilogLoggerFactory(_serilog, dispose: false);
        LogDirectory = _settings.Directory;
    }

    public string LogDirectory { get; private set; }

    public event EventHandler<LogEntry>? EntryLogged;

    public void Reconfigure(LoggingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            var old = _serilog;
            _settings = settings;
            _memorySink.Capacity = Math.Max(100, settings.InAppBufferSize);
            _serilog = Build(settings, _memorySink);
            _factory = new SerilogLoggerFactory(_serilog, dispose: false);
            LogDirectory = settings.Directory;
            old.Dispose();
        }
    }

    public IReadOnlyList<LogEntry> Snapshot() => _memorySink.Snapshot();

    public void Clear() => _memorySink.Clear();

    public Microsoft.Extensions.Logging.ILogger<T> CreateLogger<T>() => new ForwardingLogger<T>(this);

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string category) => new ForwardingLogger(this, category);

    public async Task<string> ExportZipAsync(string targetPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var dir = LogDirectory;
        await Task.Run(() =>
        {
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            using var zip = ZipFile.Open(targetPath, ZipArchiveMode.Create);
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                    using var target = entry.Open();
                    source.CopyTo(target);
                }
            }

            var snapshotEntry = zip.CreateEntry("in-app-buffer.txt");
            using var writer = new StreamWriter(snapshotEntry.Open());
            foreach (var e in _memorySink.Snapshot())
            {
                writer.WriteLine($"{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{e.Level}] [{e.Category}] {e.Message}");
                if (e.Exception is not null)
                {
                    writer.WriteLine(e.Exception);
                }
            }
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

    /// <summary>Returns the effective minimum level for a category, honouring per-category overrides.</summary>
    public static LogLevelSetting EffectiveLevel(LoggingSettings settings, string category)
    {
        if (settings.CategoryLevels is not null && settings.CategoryLevels.TryGetValue(category, out var level))
        {
            return level;
        }

        return settings.MinimumLevel;
    }

    private static Logger Build(LoggingSettings settings, InMemorySink memorySink)
    {
        // The pipeline minimum is the lowest level any category wants; the category filter does the rest.
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

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(ToSerilogLevel(floor))
            .Enrich.FromLogContext()
            .Filter.ByIncludingOnly(evt =>
            {
                var category = LogCategories.FromSourceContext(GetSourceContext(evt));
                return evt.Level >= ToSerilogLevel(EffectiveLevel(settings, category));
            })
            .Enrich.With(new CategoryEnricher())
            .WriteTo.Sink(memorySink);

        if (settings.FileEnabled && !string.IsNullOrWhiteSpace(settings.Directory))
        {
            try
            {
                Directory.CreateDirectory(settings.Directory);
                var interval = settings.RollingInterval switch
                {
                    LogRollingInterval.Hour => RollingInterval.Hour,
                    LogRollingInterval.Day => RollingInterval.Day,
                    _ => RollingInterval.Infinite,
                };
                long sizeLimit = Math.Max(1, settings.FileSizeLimitMb) * 1024L * 1024L;
                int retained = Math.Max(1, settings.RetainedFileCount);
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
            }
            catch
            {
                // If the directory cannot be created we still keep the in-memory sink.
            }
        }

        config = config.WriteTo.Debug();
        return config.CreateLogger();
    }

    private static string? GetSourceContext(LogEvent evt) =>
        evt.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string s } ? s : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            _serilog.Dispose();
        }
    }

    /// <summary>Adds a short "Category" property derived from SourceContext so output templates can print it.</summary>
    private sealed class CategoryEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var category = LogCategories.FromSourceContext(GetSourceContext(logEvent));
            logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty("Category", category));
        }
    }

    /// <summary>Ring buffer sink that feeds the Logs page.</summary>
    private sealed class InMemorySink : ILogEventSink
    {
        private readonly object _lock = new();
        private readonly Queue<LogEntry> _entries = new();

        public InMemorySink(int capacity) => Capacity = capacity;

        public int Capacity { get; set; }

        public event EventHandler<LogEntry>? EntryLogged;

        public void Emit(LogEvent logEvent)
        {
            var category = logEvent.Properties.TryGetValue("Category", out var c) && c is ScalarValue { Value: string s }
                ? s
                : LogCategories.FromSourceContext(GetSourceContext(logEvent));
            var entry = new LogEntry(logEvent.Timestamp, FromSerilogLevel(logEvent.Level), category,
                logEvent.RenderMessage(), logEvent.Exception?.ToString());
            lock (_lock)
            {
                _entries.Enqueue(entry);
                while (_entries.Count > Capacity)
                {
                    _entries.Dequeue();
                }
            }

            EntryLogged?.Invoke(this, entry);
        }

        public IReadOnlyList<LogEntry> Snapshot()
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
            }
        }
    }

    /// <summary>Resolves the current factory on every call so reconfiguration is picked up by existing loggers.</summary>
    private class ForwardingLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly SerilogLogService _owner;
        private readonly string _category;

        public ForwardingLogger(SerilogLogService owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        private Microsoft.Extensions.Logging.ILogger Inner
        {
            get
            {
                lock (_owner._sync)
                {
                    return _owner._factory.CreateLogger(_category);
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Inner.BeginScope(state);

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => Inner.IsEnabled(logLevel);

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class ForwardingLogger<T> : ForwardingLogger, Microsoft.Extensions.Logging.ILogger<T>
    {
        public ForwardingLogger(SerilogLogService owner)
            : base(owner, typeof(T).FullName ?? typeof(T).Name)
        {
        }
    }
}
