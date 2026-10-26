using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

namespace PcChecker.Core.Logging;

/// <summary>
/// An <see cref="ILoggerFactory"/> whose loggers keep working after the Serilog pipeline is rebuilt. Each logger it hands
/// out is a <see cref="SwappableLogger"/> that re-resolves its inner logger whenever <see cref="Swap"/> was called.
/// </summary>
public sealed class ReconfigurableLoggerFactory : ILoggerFactory
{
    private readonly object _sync = new();
    private SerilogLoggerFactory _current;
    private int _version;
    private bool _disposed;

    public ReconfigurableLoggerFactory(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _current = new SerilogLoggerFactory(logger, dispose: false);
    }

    /// <summary>Incremented on every swap; loggers compare it to know when to refresh.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>The factory currently backing new and existing loggers.</summary>
    public SerilogLoggerFactory Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>Points every logger at <paramref name="logger"/>. The previous Serilog logger is not disposed here.</summary>
    public void Swap(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        SerilogLoggerFactory old;
        lock (_sync)
        {
            old = _current;
            _current = new SerilogLoggerFactory(logger, dispose: false);
            Interlocked.Increment(ref _version);
        }

        old.Dispose();
    }

    public ILogger CreateLogger(string categoryName) => new SwappableLogger(this, categoryName);

    public ILogger<T> CreateLogger<T>() => new SwappableLogger<T>(this);

    /// <summary>Providers are not supported: Serilog is the only backend.</summary>
    public void AddProvider(ILoggerProvider provider)
    {
        // Intentionally ignored; every log line goes through the Serilog pipeline owned by SerilogLogService.
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
            _current.Dispose();
        }
    }

    /// <summary>Delegates to the current factory's logger and refreshes it after a swap. Cheap: one volatile read per call.</summary>
    public class SwappableLogger : ILogger
    {
        private readonly ReconfigurableLoggerFactory _owner;
        private readonly string _category;
        private ILogger? _inner;
        private int _innerVersion = -1;

        public SwappableLogger(ReconfigurableLoggerFactory owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public string Category => _category;

        private ILogger Inner
        {
            get
            {
                var version = _owner.Version;
                var inner = _inner;
                if (inner is null || _innerVersion != version)
                {
                    inner = _owner.Current.CreateLogger(_category);
                    _inner = inner;
                    _innerVersion = version;
                }

                return inner;
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => Inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Inner.Log(logLevel, eventId, state, exception, formatter);
    }

    public sealed class SwappableLogger<T> : SwappableLogger, ILogger<T>
    {
        public SwappableLogger(ReconfigurableLoggerFactory owner)
            : base(owner, typeof(T).FullName ?? typeof(T).Name)
        {
        }
    }
}
