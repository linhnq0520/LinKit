using System.Text.Json;
using LinKit.Core.Messaging;
using LinKit.Messaging.Kafka;
using LinKit.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinKit.Messaging.Tests;

public sealed class OutboxPublisherServiceTests
{
    [Fact]
    public async Task PublishesJsonPayloadWithoutDoubleEncodingAndPreservesMessageType()
    {
        const string payload = """{"orderId":"abc","quantity":2}""";
        const string messageType = "Orders.OrderCreated";
        IOutboxStore store = new InMemoryOutboxStore();
        await store.SaveMessageAsync(new OutboxMessage
        {
            MessageType = messageType,
            Payload = payload
        });
        var messageId = (await store.GetPendingAsync()).Single().Id;

        var producer = new CapturingProducer();
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton<IBrokerProducer>(producer);
        using var provider = services.BuildServiceProvider();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var publisher = new OutboxPublisherService(
            provider,
            NullLogger<OutboxPublisherService>.Instance);

        try
        {
            await publisher.StartAsync(timeout.Token);
            var published = await producer.Published.Task.WaitAsync(timeout.Token);

            Assert.Equal(payload, published.Json);
            Assert.Equal(messageType, published.Headers.MessageType);
            Assert.Equal("OrderCreated", published.Topic);
            Assert.Equal("OrderCreated", published.RoutingKey);
            Assert.Equal(messageId.ToString("N"), published.Headers[MessageHeaderNames.PartitionKey]);
            Assert.Empty(await store.GetPendingAsync());
        }
        finally
        {
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task KafkaRecordProcessorCommitsOnlyAfterSuccessfulHandler()
    {
        var commits = 0;
        var seeks = 0;

        await KafkaRecordProcessor.ProcessAsync(
            () => Task.CompletedTask,
            () => commits++,
            () => seeks++,
            "orders",
            "order-1",
            TimeSpan.Zero,
            CancellationToken.None,
            NullLogger.Instance);

        Assert.Equal(1, commits);
        Assert.Equal(0, seeks);
    }

    [Fact]
    public async Task KafkaRecordProcessorSeeksFailedHandlerWithoutCommitting()
    {
        var commits = 0;
        var seeks = 0;

        await KafkaRecordProcessor.ProcessAsync(
            () => Task.FromException(new InvalidOperationException("handler failed")),
            () => commits++,
            () => seeks++,
            "orders",
            "order-1",
            TimeSpan.Zero,
            CancellationToken.None,
            NullLogger.Instance);

        Assert.Equal(0, commits);
        Assert.Equal(1, seeks);
    }

    [Fact]
    public async Task KafkaRecordProcessorDoesNotCommitOrSeekWhenHostCancels()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var commits = 0;
        var seeks = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            KafkaRecordProcessor.ProcessAsync(
                () => Task.FromCanceled(cancellation.Token),
                () => commits++,
                () => seeks++,
                "orders",
                "order-1",
                TimeSpan.Zero,
                cancellation.Token,
                NullLogger.Instance));

        Assert.Equal(0, commits);
        Assert.Equal(0, seeks);
    }

    [Fact]
    public async Task FullBatchesAreDrainedWithoutWaitingForPollInterval()
    {
        IOutboxStore store = new InMemoryOutboxStore();
        for (var i = 0; i < 3; i++)
            await store.SaveMessageAsync(new OutboxMessage { MessageType = $"Orders.Created{i}", Payload = "{}" });

        var producer = new CountingProducer();
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton<IBrokerProducer>(producer);
        using var provider = services.BuildServiceProvider();
        var publisher = new OutboxPublisherService(
            provider,
            NullLogger<OutboxPublisherService>.Instance,
            new OutboxOptions { BatchSize = 1, PollInterval = TimeSpan.FromMinutes(10) });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await publisher.StartAsync(timeout.Token);
            await producer.AllPublished.Task.WaitAsync(timeout.Token);
            Assert.Empty(await store.GetPendingAsync());
        }
        finally
        {
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    private sealed class CountingProducer : IBrokerProducer
    {
        private int _count;
        public TaskCompletionSource AllPublished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ProduceAsync(string topicOrExchange, string routingKey, object message, MessageHeaders? headers, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _count) == 3) AllPublished.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingProducer : IBrokerProducer
    {
        public TaskCompletionSource<PublishedMessage> Published { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ProduceAsync(
            string topicOrExchange,
            string routingKey,
            object message,
            MessageHeaders? headers,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = JsonSerializer.Serialize(message);
            Published.TrySetResult(new PublishedMessage(topicOrExchange, routingKey, json, headers!));
            return Task.CompletedTask;
        }
    }

    private sealed record PublishedMessage(string Topic, string RoutingKey, string Json, MessageHeaders Headers);
}
