namespace PcChecker.Core.Common;

/// <summary>Outcome of an operation that does not return a value.</summary>
public sealed record OperationResult(bool Success, string Message, Exception? Error = null, bool RebootRequired = false)
{
    public static OperationResult Ok(string message = "Done.", bool rebootRequired = false) =>
        new(true, message, null, rebootRequired);

    public static OperationResult Fail(string message, Exception? error = null) =>
        new(false, message, error);

    public static OperationResult FromException(Exception error, string? message = null) =>
        new(false, message ?? error.Message, error);

    /// <summary>Converts to a typed result with no value.</summary>
    public OperationResult<T> WithValue<T>(T? value) => new(Success, Message, value, Error, RebootRequired);
}

/// <summary>Outcome of an operation that returns a value when it succeeds.</summary>
public sealed record OperationResult<T>(bool Success, string Message, T? Value, Exception? Error = null, bool RebootRequired = false)
{
    public static OperationResult<T> Ok(T value, string message = "Done.", bool rebootRequired = false) =>
        new(true, message, value, null, rebootRequired);

    public static OperationResult<T> Fail(string message, Exception? error = null) =>
        new(false, message, default, error);

    /// <summary>Drops the value and keeps the outcome.</summary>
    public OperationResult AsUntyped() => new(Success, Message, Error, RebootRequired);
}
