using Serilog.Core;
using Serilog.Events;

namespace PcChecker.Core.Logging;

/// <summary>
/// Serilog sink that keeps the most recent entries in a fixed-size ring buffer for the Logs page. Every operation is
/// O(1) under a short lock so logging threads never wait on the UI; <see cref="EntryLogged"/> is raised outside the lock.
/// </summary>
public sealed class InMemoryLogSink : ILogEventSink
{
    public const int MinimumCapacity = 100;

    private readonly object _lock = new();
    private readonly Queue<LogEntry> _entries;
    private int _capacity;
    private bool _redactSerials;

    public InMemoryLogSink(int capacity, bool redactSerials = false)
    {
        _capacity = Math.Max(MinimumCapacity, capacity);
        _entries = new Queue<LogEntry>(_capacity);
        _redactSerials = redactSerials;
    }

    /// <summary>Maximum entries kept; shrinking drops the oldest entries immediately.</summary>
    public int Capacity
    {
        get => _capacity;
        set
        {
            lock (_lock)
            {
                _capacity = Math.Max(MinimumCapacity, value);
                Trim();
            }
        }
    }

    /// <summary>When true, serial-number-like tokens are masked before an entry is stored.</summary>
    public bool RedactSerials
    {
        get => _redactSerials;
        set => _redactSerials = value;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public event EventHandler<LogEntry>? EntryLogged;

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var entry = ToEntry(logEvent, _redactSerials);
        Push(entry);
    }

    /// <summary>Adds an entry that did not come through Serilog (used by tests and demo seeding).</summary>
    public void Push(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            _entries.Enqueue(entry);
            Trim();
        }

        EntryLogged?.Invoke(this, entry);
    }

    /// <summary>Oldest first.</summary>
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

    /// <summary>Converts a Serilog event to the app's entry record, applying redaction when asked.</summary>
    public static LogEntry ToEntry(LogEvent logEvent, bool redactSerials)
    {
        var category = logEvent.Properties.TryGetValue("Category", out var c) && c is ScalarValue { Value: string s }
            ? s
            : LogCategoryResolver.FromSourceContext(SourceContextOf(logEvent));
        var message = logEvent.RenderMessage();
        var exception = logEvent.Exception?.ToString();
        if (redactSerials)
        {
            message = LogRedaction.RedactSerials(message);
            exception = exception is null ? null : LogRedaction.RedactSerials(exception);
        }

        return new LogEntry(logEvent.Timestamp, SerilogLogService.FromSerilogLevel(logEvent.Level), category, message, exception);
    }

    internal static string? SourceContextOf(LogEvent evt) =>
        evt.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string s } ? s : null;

    private void Trim()
    {
        while (_entries.Count > _capacity)
        {
            _entries.Dequeue();
        }
    }
}
