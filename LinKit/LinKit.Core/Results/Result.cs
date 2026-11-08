using System;
using System.Collections.Generic;
using System.Linq;

namespace LinKit.Core.Results;

/// <summary>
/// Outcome category of a <see cref="Result"/>. The value is mapped to an HTTP status code by the
/// generated endpoint layer, so one Result type can serve both domain and transport concerns.
/// </summary>
public enum ResultStatus
{
    /// <summary>The operation completed successfully.</summary>
    Ok,

    /// <summary>The requested resource does not exist. Maps to HTTP 404.</summary>
    NotFound,

    /// <summary>The request was rejected because it is malformed or invalid. Maps to HTTP 400.</summary>
    BadRequest,

    /// <summary>The request conflicts with the current state of the resource. Maps to HTTP 409.</summary>
    Conflict,

    /// <summary>The caller is not authenticated. Maps to HTTP 401.</summary>
    Unauthorized,

    /// <summary>The caller is authenticated but not allowed to perform the operation. Maps to HTTP 403.</summary>
    Forbidden,

    /// <summary>The operation failed for an unexpected reason. Maps to HTTP 500.</summary>
    Error,
}

/// <summary>
/// A single validation failure attached to a <see cref="Result"/>.
/// </summary>
/// <param name="PropertyName">Name of the property or field that failed validation.</param>
/// <param name="ErrorMessage">Human readable description of the failure.</param>
/// <param name="ErrorCode">Optional machine readable code, for example <c>"Required"</c> or <c>"TooLong"</c>.</param>
public record ValidationError(string PropertyName, string ErrorMessage, string? ErrorCode = null);

/// <summary>
/// Non-generic operation outcome without a payload. It carries success state, a <see cref="ResultStatus"/>, an error
/// message, an optional error code and any validation errors, which makes it suitable for both domain code and API
/// responses without depending on ASP.NET Core types.
/// </summary>
public class Result
{
    /// <summary>
    /// Gets a value indicating whether the operation succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets the outcome category of the operation.
    /// </summary>
    public ResultStatus Status { get; }

    /// <summary>
    /// Gets the human readable error message, or <see langword="null"/> when the operation succeeded.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Gets the optional machine readable error code, for example <c>"NotFound"</c>.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Gets the validation failures. The collection is empty when the operation succeeded or failed for a reason
    /// that is not validation related.
    /// </summary>
    public IReadOnlyList<ValidationError> ValidationErrors { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Result"/> class. Call one of the static factories
    /// (<see cref="Success()"/>, <see cref="Failure(string, string?)"/>, <see cref="NotFound(string, string?)"/>, …)
    /// instead of calling this constructor directly.
    /// </summary>
    /// <param name="isSuccess">Whether the operation succeeded.</param>
    /// <param name="status">Outcome category.</param>
    /// <param name="errorMessage">Human readable error message.</param>
    /// <param name="errorCode">Machine readable error code.</param>
    /// <param name="validationErrors">Validation failures, if any.</param>
    protected Result(
        bool isSuccess,
        ResultStatus status,
        string? errorMessage = null,
        string? errorCode = null,
        IEnumerable<ValidationError>? validationErrors = null
    )
    {
        IsSuccess = isSuccess;
        Status = status;
        ErrorMessage = errorMessage;
        ErrorCode = errorCode;
        ValidationErrors =
            validationErrors?.ToList().AsReadOnly()
            ?? (IReadOnlyList<ValidationError>)Array.Empty<ValidationError>();
    }

    /// <summary>
    /// Returns the payload of the result when there is one, otherwise <see langword="null"/>.
    /// Derived types override this to avoid boxing the value and to let generic endpoint code treat
    /// <see cref="Result"/> and <see cref="Result{T}"/> uniformly.
    /// </summary>
    /// <returns>The underlying value, or <see langword="null"/>.</returns>
    public virtual object? GetRawValue() => null;

    /// <summary>
    /// Creates a successful result without a payload.
    /// </summary>
    /// <returns>A successful <see cref="Result"/>.</returns>
    public static Result Success() => new(true, ResultStatus.Ok);

    /// <summary>
    /// Creates a successful generic result carrying <paramref name="value"/>.
    /// </summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="value">The payload to return.</param>
    /// <returns>A successful <see cref="Result{T}"/>.</returns>
    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    /// <summary>
    /// Creates a failed result with <see cref="ResultStatus.Error"/>.
    /// </summary>
    /// <param name="errorMessage">Human readable description of the failure.</param>
    /// <param name="errorCode">Optional machine readable code.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    public static Result Failure(string errorMessage, string? errorCode = null) =>
        new(false, ResultStatus.Error, errorMessage, errorCode);

    /// <summary>
    /// Creates a failed result with <see cref="ResultStatus.NotFound"/>.
    /// </summary>
    /// <param name="errorMessage">Human readable description of the failure.</param>
    /// <param name="errorCode">Optional machine readable code.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    public static Result NotFound(
        string errorMessage = "Resource not found",
        string? errorCode = "NotFound"
    ) => new(false, ResultStatus.NotFound, errorMessage, errorCode);

    /// <summary>
    /// Creates a failed result with <see cref="ResultStatus.BadRequest"/>.
    /// </summary>
    /// <param name="errorMessage">Human readable description of the failure.</param>
    /// <param name="errorCode">Optional machine readable code.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    public static Result BadRequest(string errorMessage, string? errorCode = "BadRequest") =>
        new(false, ResultStatus.BadRequest, errorMessage, errorCode);

    /// <summary>
    /// Creates a failed result with <see cref="ResultStatus.Conflict"/>.
    /// </summary>
    /// <param name="errorMessage">Human readable description of the conflict.</param>
    /// <param name="errorCode">Optional machine readable code.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    public static Result Conflict(string errorMessage, string? errorCode = "Conflict") =>
        new(false, ResultStatus.Conflict, errorMessage, errorCode);

    /// <summary>
    /// Creates a failed result with <see cref="ResultStatus.Unauthorized"/>.
    /// </summary>
    /// <param name="errorMessage">Human readable description of the failure.</param>
    /// <param name="errorCode">Optional machine readable code.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    public static Result Unauthorized(
        string errorMessage = "Unauthorized",
        string? errorCode = "Unauthorized"
    ) => new(false, ResultStatus.Unauthorized, errorMessage, errorCode);

    /// <summary>
    /// Creates a failed result with <see cref="ResultStatus.Forbidden"/>.
    /// </summary>
    /// <param name="errorMessage">Human readable description of the failure.</param>
    /// <param name="errorCode">Optional machine readable code.</param>
    /// <returns>A failed <see cref="Result"/>.</returns>
    public static Result Forbidden(
        string errorMessage = "Forbidden",
        string? errorCode = "Forbidden"
    ) => new(false, ResultStatus.Forbidden, errorMessage, errorCode);

    /// <summary>
    /// Creates a validation failure result with <see cref="ResultStatus.BadRequest"/> and the supplied errors.
    /// </summary>
    /// <param name="validationErrors">One entry per failed property.</param>
    /// <param name="errorMessage">Summary message returned to the caller.</param>
    /// <returns>A failed <see cref="Result"/> whose <see cref="ValidationErrors"/> contains the details.</returns>
    public static Result Invalid(
        IEnumerable<ValidationError> validationErrors,
        string errorMessage = "Validation failed"
    ) => new(false, ResultStatus.BadRequest, errorMessage, "ValidationError", validationErrors);

    /// <summary>
    /// Creates a validation failure result for a single property.
    /// </summary>
    /// <param name="propertyName">Name of the property that failed validation.</param>
    /// <param name="errorMessage">Human readable description of the failure.</param>
    /// <returns>A failed <see cref="Result"/> whose <see cref="ValidationErrors"/> contains one entry.</returns>
    public static Result Invalid(string propertyName, string errorMessage) =>
        new(
            false,
            ResultStatus.BadRequest,
            errorMessage,
            "ValidationError",
            new[] { new ValidationError(propertyName, errorMessage) }
        );
}

/// <summary>
/// Operation outcome that carries a payload of type <typeparamref name="T"/> on success.
/// A value can be converted implicitly, so a handler may return <c>return value;</c> instead of
/// <c>return Result&lt;T&gt;.Success(value);</c>.
/// </summary>
/// <typeparam name="T">Payload type.</typeparam>
public class Result<T> : Result
{
    private readonly T? _value;

    /// <summary>
    /// Gets the payload. Accessing it on a failed result throws <see cref="InvalidOperationException"/>;
    /// use <see cref="ValueOrDefault"/> when the outcome is not known to be successful.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result is not successful.</exception>
    public T Value =>
        IsSuccess
            ? _value!
            : throw new InvalidOperationException(
                $"Cannot access Value on a failed result: {ErrorMessage}"
            );

    /// <summary>
    /// Gets the payload without throwing, or <see langword="default"/> when the result failed.
    /// </summary>
    public T? ValueOrDefault => _value;

    /// <inheritdoc />
    public override object? GetRawValue() => _value;

    private Result(T value)
        : base(true, ResultStatus.Ok)
    {
        _value = value;
    }

    private Result(
        ResultStatus status,
        string? errorMessage,
        string? errorCode,
        IEnumerable<ValidationError>? validationErrors
    )
        : base(false, status, errorMessage, errorCode, validationErrors)
    {
        _value = default;
    }

    /// <summary>
    /// Creates a successful result carrying <paramref name="value"/>.
    /// </summary>
    /// <param name="value">The payload to return.</param>
    /// <returns>A successful <see cref="Result{T}"/>.</returns>
    public static Result<T> Success(T value) => new(value);

    /// <summary>
    /// Creates a failed typed result with <see cref="ResultStatus.Error"/>.
    /// </summary>
    /// <param name="errorMessage">Human readable description of the failure.</param>
    /// <param name="errorCode">Optional machine readable code.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    public static new Result<T> Failure(string errorMessage, string? errorCode = null) =>
        new(ResultStatus.Error, errorMessage, errorCode, null);

    /// <summary>
    /// Creates a typed result with <see cref="ResultStatus.NotFound"/>.
    /// </summary>
    /// <param name="errorMessage">Human readable description of the failure.</param>
    /// <param name="errorCode">Optional machine readable code.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    public static new Result<T> NotFound(
        string errorMessage = "Resource not found",
        string? errorCode = "NotFound"
    ) => new(ResultStatus.NotFound, errorMessage, errorCode, null);

    /// <summary>
    /// Creates a typed result with <see cref="ResultStatus.BadRequest"/>.
    /// </summary>
    /// <param name="errorMessage">Human readable description of the failure.</param>
    /// <param name="errorCode">Optional machine readable code.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    public static new Result<T> BadRequest(string errorMessage, string? errorCode = "BadRequest") =>
        new(ResultStatus.BadRequest, errorMessage, errorCode, null);

    /// <summary>
    /// Creates a typed result with <see cref="ResultStatus.Conflict"/>.
    /// </summary>
    /// <param name="errorMessage">Human readable description of the conflict.</param>
    /// <param name="errorCode">Optional machine readable code.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    public static new Result<T> Conflict(string errorMessage, string? errorCode = "Conflict") =>
        new(ResultStatus.Conflict, errorMessage, errorCode, null);

    /// <summary>
    /// Creates a typed result with <see cref="ResultStatus.Unauthorized"/>.
    /// </summary>
    /// <param name="errorMessage">Human readable description of the failure.</param>
    /// <param name="errorCode">Optional machine readable code.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    public static new Result<T> Unauthorized(
        string errorMessage = "Unauthorized",
        string? errorCode = "Unauthorized"
    ) => new(ResultStatus.Unauthorized, errorMessage, errorCode, null);

    /// <summary>
    /// Creates a typed result with <see cref="ResultStatus.Forbidden"/>.
    /// </summary>
    /// <param name="errorMessage">Human readable description of the failure.</param>
    /// <param name="errorCode">Optional machine readable code.</param>
    /// <returns>A failed <see cref="Result{T}"/>.</returns>
    public static new Result<T> Forbidden(
        string errorMessage = "Forbidden",
        string? errorCode = "Forbidden"
    ) => new(ResultStatus.Forbidden, errorMessage, errorCode, null);

    /// <summary>
    /// Creates a typed validation failure result from a set of errors.
    /// </summary>
    /// <param name="validationErrors">One entry per failed property.</param>
    /// <param name="errorMessage">Summary message returned to the caller.</param>
    /// <returns>A failed <see cref="Result{T}"/> whose <see cref="Result.ValidationErrors"/> contains the details.</returns>
    public static new Result<T> Invalid(
        IEnumerable<ValidationError> validationErrors,
        string errorMessage = "Validation failed"
    ) => new(ResultStatus.BadRequest, errorMessage, "ValidationError", validationErrors);

    /// <summary>
    /// Creates a typed validation failure result for a single property.
    /// </summary>
    /// <param name="propertyName">Name of the property that failed validation.</param>
    /// <param name="errorMessage">Human readable description of the failure.</param>
    /// <returns>A failed <see cref="Result{T}"/> whose <see cref="Result.ValidationErrors"/> contains one entry.</returns>
    public static new Result<T> Invalid(string propertyName, string errorMessage) =>
        new(
            ResultStatus.BadRequest,
            errorMessage,
            "ValidationError",
            new[] { new ValidationError(propertyName, errorMessage) }
        );

    /// <summary>
    /// Converts a payload into a successful <see cref="Result{T}"/>, allowing <c>return value;</c> in handlers.
    /// </summary>
    /// <param name="value">The payload to wrap.</param>
    public static implicit operator Result<T>(T value) => Success(value);
}
