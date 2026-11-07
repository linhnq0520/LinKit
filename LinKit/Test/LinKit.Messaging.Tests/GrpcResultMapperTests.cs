using Grpc.Core;
using LinKit.Core.Results;
using LinKit.Grpc;
using Xunit;

namespace LinKit.Messaging.Tests;

/// <summary>
/// Covers the Phase 1.7 gRPC contract: transport failures become failed <see cref="Result"/> values that keep the
/// gRPC status code, so a generated client never leaks an <see cref="RpcException"/> into the application.
/// </summary>
public sealed class GrpcResultMapperTests
{
    private static RpcException Error(StatusCode code, string detail = "boom") =>
        new(new Status(code, detail));

    [Theory]
    [InlineData(StatusCode.NotFound, ResultStatus.NotFound)]
    [InlineData(StatusCode.InvalidArgument, ResultStatus.BadRequest)]
    [InlineData(StatusCode.OutOfRange, ResultStatus.BadRequest)]
    [InlineData(StatusCode.AlreadyExists, ResultStatus.Conflict)]
    [InlineData(StatusCode.Aborted, ResultStatus.Conflict)]
    [InlineData(StatusCode.FailedPrecondition, ResultStatus.Conflict)]
    [InlineData(StatusCode.PermissionDenied, ResultStatus.Forbidden)]
    [InlineData(StatusCode.Unauthenticated, ResultStatus.Unauthorized)]
    [InlineData(StatusCode.DeadlineExceeded, ResultStatus.Error)]
    [InlineData(StatusCode.Unavailable, ResultStatus.Error)]
    [InlineData(StatusCode.Internal, ResultStatus.Error)]
    public void StatusCodeMapsToTheMatchingResultStatus(StatusCode code, ResultStatus expected)
    {
        var generic = GrpcResultMapper.ToResult<int>(Error(code));
        var plain = GrpcResultMapper.ToResult(Error(code));

        Assert.False(generic.IsSuccess);
        Assert.Equal(expected, generic.Status);
        Assert.Equal(code.ToString(), generic.ErrorCode);
        Assert.Equal(expected, plain.Status);
        Assert.Equal(code.ToString(), plain.ErrorCode);
    }

    [Fact]
    public void ServerDetailIsPreferredOverTheTransportMessage()
    {
        var result = GrpcResultMapper.ToResult<string>(
            Error(StatusCode.NotFound, "order 42 does not exist")
        );

        Assert.Equal("order 42 does not exist", result.ErrorMessage);
        Assert.Contains(
            "order 42 does not exist",
            GrpcResultMapper.GetErrorMessage(Error(StatusCode.NotFound, "order 42 does not exist"))
        );
    }

    [Fact]
    public void BlankDetailFallsBackToTheExceptionMessage()
    {
        var exception = Error(StatusCode.Unknown, detail: "");

        Assert.False(string.IsNullOrWhiteSpace(GrpcResultMapper.GetErrorMessage(exception)));
        Assert.Equal(
            GrpcResultMapper.GetErrorMessage(exception),
            GrpcResultMapper.ToResult<Guid>(exception).ErrorMessage
        );
    }

    [Fact]
    public void FailedResultDoesNotExposeAValue()
    {
        var result = GrpcResultMapper.ToResult<string>(Error(StatusCode.Unavailable));

        Assert.False(result.IsSuccess);
        Assert.Null(result.ValueOrDefault);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }
}
