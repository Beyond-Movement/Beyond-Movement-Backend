namespace BeyondMovement.SharedKernel;

/// <summary>
/// An expected failure. <see cref="Code"/> is the stable string the mobile app switches on
/// (CLAUDE.md section 7) — renaming one is a contract change.
/// </summary>
/// <param name="Code">The stable string the mobile app switches on. Renaming one is a contract change.</param>
/// <param name="Message">Human-readable text. Clients must never branch on this.</param>
/// <param name="StatusCode">HTTP status to return.</param>
/// <param name="RetryAfterSeconds">
/// How long until the caller may retry, when that is knowable — a lockout, for example.
/// Surfaces both as a <c>Retry-After</c> header and in the problem body.
/// </param>
public sealed record Error(string Code, string Message, int StatusCode, int? RetryAfterSeconds = null);

/// <summary>
/// The outcome of a use-case handler. Handlers return this for expected failures;
/// exceptions are reserved for genuinely exceptional conditions.
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error? Error { get; }

    public static Result Success() => new(true, null);
    public static Result Failure(Error error) => new(false, error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(bool isSuccess, T? value, Error? error) : base(isSuccess, error) => _value = value;

    /// <summary>The value. Only valid when <see cref="Result.IsSuccess"/> is true.</summary>
    public T Value => IsSuccess
        ? _value!   // guarded by IsSuccess; Success() never stores null
        : throw new InvalidOperationException("Cannot read the value of a failed Result.");

    public static Result<T> Success(T value) => new(true, value, null);
    public static new Result<T> Failure(Error error) => new(false, default, error);
}
