namespace Snaply.Core.Common;

public enum ErrorCode
{
    Unknown,
    Cancelled,
    PermissionDenied,
    Unsupported,
    Failed,
    NotFound,
    InvalidInput,
    StorageUnavailable,
    Conflict,
    EngineUnavailable,
    LanguageUnavailable,
}

/// <summary>An expected failure with a user-presentable message. Technical detail goes to logs.</summary>
public sealed record Error(ErrorCode Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }
    public bool IsSuccess => Error is null;
    public bool IsCancelled => Error?.Code == ErrorCode.Cancelled;

    public static Result Ok() => new(null);
    public static Result Fail(Error error) => new(error);
    public static Result Fail(ErrorCode code, string message) => new(new Error(code, message));
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value) : base(null) => _value = value;
    private Result(Error error) : base(error) { }

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result failed: {Error}");

    public static Result<T> Ok(T value) => new(value);
    public new static Result<T> Fail(Error error) => new(error);
    public new static Result<T> Fail(ErrorCode code, string message) => new(new Error(code, message));
}
