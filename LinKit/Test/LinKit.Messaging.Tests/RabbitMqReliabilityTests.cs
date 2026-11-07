using System.Text;
using LinKit.Messaging.RabbitMQ;
using Xunit;

namespace LinKit.Messaging.Tests;

public sealed class RabbitMqReliabilityTests
{
    [Fact]
    public void OptionsHaveBoundedRetryDefaults()
    {
        var options = new RabbitMqOptions();

        Assert.True(options.RequeueOnFailure);
        Assert.Equal(3, options.MaxRetryAttempts);
        Assert.Null(options.DeadLetterExchange);
    }

    [Fact]
    public void AttemptCountIsZeroWhenHeaderMissingOrUnparsable()
    {
        Assert.Equal(0, RabbitMqRetryPolicy.GetAttemptCount(null));
        Assert.Equal(0, RabbitMqRetryPolicy.GetAttemptCount(new Dictionary<string, object>()));
        Assert.Equal(0, RabbitMqRetryPolicy.GetAttemptCount(new Dictionary<string, object>
        {
            [RabbitMqRetryPolicy.AttemptHeader] = "not-a-number"
        }));
        Assert.Equal(0, RabbitMqRetryPolicy.GetAttemptCount(new Dictionary<string, object>
        {
            [RabbitMqRetryPolicy.AttemptHeader] = new object()
        }));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(2L)]
    [InlineData((short)2)]
    [InlineData((byte)2)]
    [InlineData("2")]
    public void AttemptCountParsesBrokerHeaderRepresentations(object value)
    {
        var headers = new Dictionary<string, object>
        {
            [RabbitMqRetryPolicy.AttemptHeader] = value
        };

        Assert.Equal(2, RabbitMqRetryPolicy.GetAttemptCount(headers));
    }

    [Fact]
    public void AttemptCountParsesUtf8ByteArrayHeader()
    {
        var headers = new Dictionary<string, object>
        {
            [RabbitMqRetryPolicy.AttemptHeader] = Encoding.UTF8.GetBytes("3")
        };

        Assert.Equal(3, RabbitMqRetryPolicy.GetAttemptCount(headers));
    }

    [Fact]
    public void AttemptCountClampsNegativeValuesToZero()
    {
        var headers = new Dictionary<string, object>
        {
            [RabbitMqRetryPolicy.AttemptHeader] = -5
        };

        Assert.Equal(0, RabbitMqRetryPolicy.GetAttemptCount(headers));
    }

    [Fact]
    public void AttemptHeaderLookupIsCaseInsensitive()
    {
        var headers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Linkit-Attempt"] = 1
        };

        Assert.Equal(1, RabbitMqRetryPolicy.GetAttemptCount(headers));
    }

    [Fact]
    public void FailuresRetryUntilAttemptCapIsReached()
    {
        Assert.Equal(
            RabbitMqFailureAction.Retry,
            RabbitMqRetryPolicy.DecideFailureAction(0, maxRetryAttempts: 3, requeueOnFailure: true, hasDeadLetterExchange: false));
        Assert.Equal(
            RabbitMqFailureAction.Retry,
            RabbitMqRetryPolicy.DecideFailureAction(2, maxRetryAttempts: 3, requeueOnFailure: true, hasDeadLetterExchange: false));
        Assert.Equal(
            RabbitMqFailureAction.Drop,
            RabbitMqRetryPolicy.DecideFailureAction(3, maxRetryAttempts: 3, requeueOnFailure: true, hasDeadLetterExchange: false));
    }

    [Fact]
    public void ExhaustedRetriesGoToDeadLetterExchangeWhenConfigured()
    {
        Assert.Equal(
            RabbitMqFailureAction.DeadLetter,
            RabbitMqRetryPolicy.DecideFailureAction(3, maxRetryAttempts: 3, requeueOnFailure: true, hasDeadLetterExchange: true));
        Assert.Equal(
            RabbitMqFailureAction.Retry,
            RabbitMqRetryPolicy.DecideFailureAction(0, maxRetryAttempts: 3, requeueOnFailure: true, hasDeadLetterExchange: true));
    }

    [Fact]
    public void RequeueOnFailureFalseStopsRequeueingImmediately()
    {
        Assert.Equal(
            RabbitMqFailureAction.Drop,
            RabbitMqRetryPolicy.DecideFailureAction(0, maxRetryAttempts: 3, requeueOnFailure: false, hasDeadLetterExchange: false));
        Assert.Equal(
            RabbitMqFailureAction.DeadLetter,
            RabbitMqRetryPolicy.DecideFailureAction(0, maxRetryAttempts: 3, requeueOnFailure: false, hasDeadLetterExchange: true));
    }

    [Fact]
    public void ZeroMaxRetryAttemptsDisablesRetries()
    {
        Assert.Equal(
            RabbitMqFailureAction.Drop,
            RabbitMqRetryPolicy.DecideFailureAction(0, maxRetryAttempts: 0, requeueOnFailure: true, hasDeadLetterExchange: false));
    }

    [Fact]
    public void ExchangeCacheTracksNameAndTypeIndependently()
    {
        var cache = new ExchangeDeclarationCache();

        Assert.False(cache.IsDeclared("orders", "topic"));
        cache.MarkDeclared("orders", "topic");

        Assert.True(cache.IsDeclared("orders", "topic"));
        Assert.False(cache.IsDeclared("orders", "direct"));
        Assert.False(cache.IsDeclared("other", "topic"));

        cache.MarkDeclared("orders", "direct");
        Assert.True(cache.IsDeclared("orders", "direct"));
        Assert.True(cache.IsDeclared("orders", "topic"));
    }

    [Fact]
    public void ExchangeCacheMarkDeclaredIsIdempotent()
    {
        var cache = new ExchangeDeclarationCache();

        cache.MarkDeclared("orders", "topic");
        cache.MarkDeclared("orders", "topic");

        Assert.True(cache.IsDeclared("orders", "topic"));
    }
}