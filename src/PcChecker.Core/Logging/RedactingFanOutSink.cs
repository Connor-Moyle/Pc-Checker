using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace PcChecker.Core.Logging;

/// <summary>
/// Forwards each event to several sinks (memory buffer, file logger, debug logger) and, when enabled, rewrites the event
/// first so serial numbers never reach any of them. A failing target never stops the others.
/// </summary>
public sealed class RedactingFanOutSink : ILogEventSink
{
    private readonly IReadOnlyList<ILogEventSink> _targets;

    public RedactingFanOutSink(IReadOnlyList<ILogEventSink> targets, bool redactSerials)
    {
        ArgumentNullException.ThrowIfNull(targets);
        _targets = targets;
        RedactSerials = redactSerials;
    }

    public bool RedactSerials { get; }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var evt = RedactSerials ? Redact(logEvent) : logEvent;
        foreach (var target in _targets)
        {
            try
            {
                target.Emit(evt);
            }
            catch
            {
                // A broken file sink must not take the in-memory viewer down with it.
            }
        }
    }

    /// <summary>
    /// Returns an equivalent event whose rendered message, scalar string properties and exception text have serial numbers
    /// masked. The message template becomes plain text (its original property tokens are already rendered).
    /// </summary>
    public static LogEvent Redact(LogEvent logEvent)
    {
        var rendered = logEvent.RenderMessage();
        var redactedMessage = LogRedaction.RedactSerials(rendered);
        bool propertiesChanged = false;
        var properties = new List<LogEventProperty>(logEvent.Properties.Count);
        foreach (var pair in logEvent.Properties)
        {
            if (pair.Value is ScalarValue { Value: string s } && LogRedaction.ContainsSensitiveToken(s))
            {
                properties.Add(new LogEventProperty(pair.Key, new ScalarValue(LogRedaction.RedactSerials(s))));
                propertiesChanged = true;
            }
            else
            {
                properties.Add(new LogEventProperty(pair.Key, pair.Value));
            }
        }

        bool messageChanged = !ReferenceEquals(rendered, redactedMessage) && rendered != redactedMessage;
        var exception = logEvent.Exception;
        bool exceptionSensitive = exception is not null && LogRedaction.ContainsSensitiveToken(exception.ToString());
        if (!messageChanged && !propertiesChanged && !exceptionSensitive)
        {
            return logEvent;
        }

        var template = messageChanged
            ? new MessageTemplate(redactedMessage, [new TextToken(redactedMessage)])
            : logEvent.MessageTemplate;
        var replacementException = exceptionSensitive ? new RedactedException(LogRedaction.RedactSerials(exception!.ToString())) : exception;
        return new LogEvent(logEvent.Timestamp, logEvent.Level, replacementException, template, properties);
    }

    /// <summary>Stands in for an exception whose text contained a serial number; <see cref="ToString"/> is the masked text.</summary>
    public sealed class RedactedException : Exception
    {
        private readonly string _text;

        public RedactedException(string text)
            : base(text.Split('\n')[0].Trim())
        {
            _text = text;
        }

        public override string ToString() => _text;
    }
}
