using System;
using System.Collections.Generic;
using System.Linq;

namespace LinKit.Core.Results;

/// <summary>
/// Status representation of an operation result.
/// </summary>
public enum ResultStatus
{
    Ok,
    NotFound,
    BadRequest,
    Conflict,
    Unauthorized,
    Forbidden,
    Error
}

/// <summary>
/// Detailed validation error entry.
/// </summary>
public record ValidationError(string PropertyName, string ErrorMessage, string? ErrorCode = null);

/// <summary>
/// Non-generic Result type representing the outcome of an operation without return data.
/// Clean-Architecture and Native AOT friendly.
/// </summary>
public class Result
{
    public bool IsSuccess { get; }
    public ResultStatus Status { get; }
    public string? ErrorMessage { get; }
    public string? ErrorCode { get; }
    public IReadOnlyList<ValidationError> ValidationErrors { get; }

    protected Result(
        bool isSuccess,
        ResultStatus status,
        string? errorMessage = null,
        string? errorCode = null,
        IEnumerable<ValidationError>? validationErrors = null)
    {
        IsSuccess = isSuccess;
        Status = status;
        ErrorMessage = errorMessage;
        ErrorCode = errorCode;
        ValidationErrors = validationErrors?.ToList().AsReadOnly() ?? (IReadOnlyList<ValidationError>)Array.Empty<ValidationError>();
    }

    public virtual object? GetRawValue() => null;

    public static Result Success() => new(true, ResultStatus.Ok);
    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result Failure(string errorMessage, string? errorCode = null)
        => new(false, ResultStatus.Error, errorMessage, errorCode);

    public static Result NotFound(string errorMessage = "Resource not found", string? errorCode = "NotFound")
        => new(false, ResultStatus.NotFound, errorMessage, errorCode);

    public static Result BadRequest(string errorMessage, string? errorCode = "BadRequest")
        => new(false, ResultStatus.BadRequest, errorMessage, errorCode);

    public static Result Conflict(string errorMessage, string? errorCode = "Conflict")
        => new(false, ResultStatus.Conflict, errorMessage, errorCode);

    public static Result Unauthorized(string errorMessage = "Unauthorized", string? errorCode = "Unauthorized")
        => new(false, ResultStatus.Unauthorized, errorMessage, errorCode);

    public static Result Forbidden(string errorMessage = "Forbidden", string? errorCode = "Forbidden")
        => new(false, ResultStatus.Forbidden, errorMessage, errorCode);

    public static Result Invalid(IEnumerable<ValidationError> validationErrors, string errorMessage = "Validation failed")
        => new(false, ResultStatus.BadRequest, errorMessage, "ValidationError", validationErrors);

    public static Result Invalid(string propertyName, string errorMessage)
        => new(false, ResultStatus.BadRequest, errorMessage, "ValidationError", new[] { new ValidationError(propertyName, errorMessage) });
}

/// <summary>
/// Generic Result type representing the outcome of an operation returning data of type <typeparamref name="T"/>.
/// </summary>
public class Result<T> : Result
{
    private readonly T? _value;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot access Value on a failed result: {ErrorMessage}");

    public T? ValueOrDefault => _value;

    public override object? GetRawValue() => _value;

    private Result(T value) : base(true, ResultStatus.Ok)
    {
        _value = value;
    }

    private Result(ResultStatus status, string? errorMessage, string? errorCode, IEnumerable<ValidationError>? validationErrors)
        : base(false, status, errorMessage, errorCode, validationErrors)
    {
        _value = default;
    }

    public static Result<T> Success(T value) => new(value);

    public static new Result<T> Failure(string errorMessage, string? errorCode = null)
        => new(ResultStatus.Error, errorMessage, errorCode, null);

    public static new Result<T> NotFound(string errorMessage = "Resource not found", string? errorCode = "NotFound")
        => new(ResultStatus.NotFound, errorMessage, errorCode, null);

    public static new Result<T> BadRequest(string errorMessage, string? errorCode = "BadRequest")
        => new(ResultStatus.BadRequest, errorMessage, errorCode, null);

    public static new Result<T> Conflict(string errorMessage, string? errorCode = "Conflict")
        => new(ResultStatus.Conflict, errorMessage, errorCode, null);

    public static new Result<T> Unauthorized(string errorMessage = "Unauthorized", string? errorCode = "Unauthorized")
        => new(ResultStatus.Unauthorized, errorMessage, errorCode, null);

    public static new Result<T> Forbidden(string errorMessage = "Forbidden", string? errorCode = "Forbidden")
        => new(ResultStatus.Forbidden, errorMessage, errorCode, null);

    public static new Result<T> Invalid(IEnumerable<ValidationError> validationErrors, string errorMessage = "Validation failed")
        => new(ResultStatus.BadRequest, errorMessage, "ValidationError", validationErrors);

    public static new Result<T> Invalid(string propertyName, string errorMessage)
        => new(ResultStatus.BadRequest, errorMessage, "ValidationError", new[] { new ValidationError(propertyName, errorMessage) });

    public static implicit operator Result<T>(T value) => Success(value);
}
