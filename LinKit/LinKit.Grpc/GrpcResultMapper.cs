using Grpc.Core;
using LinKit.Core.Results;

namespace LinKit.Grpc;

/// <summary>
/// Translates a <see cref="RpcException"/> into the LinKit <see cref="Result"/> shape, so a generated gRPC client can
/// answer with a failed result instead of letting a transport exception escape the application boundary.
/// </summary>
public static class GrpcResultMapper
{
    /// <summary>
    /// Maps <paramref name="exception"/> to a failed <see cref="Result{T}"/>. The gRPC status code is preserved
    /// verbatim in <see cref="Result.ErrorCode"/> so callers can branch on it without re-checking the transport.
    /// </summary>
    public static Result<T> ToResult<T>(RpcException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = GetErrorMessage(exception);
        var errorCode = exception.StatusCode.ToString();

        return exception.StatusCode switch
        {
            StatusCode.NotFound => Result<T>.NotFound(message, errorCode),
            StatusCode.InvalidArgument or StatusCode.OutOfRange => Result<T>.BadRequest(
                message,
                errorCode
            ),
            StatusCode.AlreadyExists or StatusCode.Aborted or StatusCode.FailedPrecondition =>
                Result<T>.Conflict(message, errorCode),
            StatusCode.PermissionDenied => Result<T>.Forbidden(message, errorCode),
            StatusCode.Unauthenticated => Result<T>.Unauthorized(message, errorCode),
            _ => Result<T>.Failure(message, errorCode),
        };
    }

    /// <summary>
    /// Maps <paramref name="exception"/> to a failed non-generic <see cref="Result"/>.
    /// </summary>
    public static Result ToResult(RpcException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = GetErrorMessage(exception);
        var errorCode = exception.StatusCode.ToString();

        return exception.StatusCode switch
        {
            StatusCode.NotFound => Result.NotFound(message, errorCode),
            StatusCode.InvalidArgument or StatusCode.OutOfRange => Result.BadRequest(
                message,
                errorCode
            ),
            StatusCode.AlreadyExists or StatusCode.Aborted or StatusCode.FailedPrecondition =>
                Result.Conflict(message, errorCode),
            StatusCode.PermissionDenied => Result.Forbidden(message, errorCode),
            StatusCode.Unauthenticated => Result.Unauthorized(message, errorCode),
            _ => Result.Failure(message, errorCode),
        };
    }

    /// <summary>
    /// Returns the server-provided detail when present, falling back to the exception message.
    /// </summary>
    public static string GetErrorMessage(RpcException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var detail = exception.Status.Detail;
        return string.IsNullOrWhiteSpace(detail) ? exception.Message : detail;
    }
}
