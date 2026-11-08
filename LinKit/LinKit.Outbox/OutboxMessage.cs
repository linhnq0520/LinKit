using System;
using System.Collections.Concurrent;
using System.Reflection;
using LinKit.Core.Messaging;
using LinKit.Tenancy;

namespace LinKit.Outbox;

/// <summary>Explicit lifecycle state of a persisted outbox message.</summary>
public enum OutboxMessageStatus
{
    /// <summary>Eligible for delivery once its schedule and lease allow it.</summary>
    Pending = 0,

    /// <summary>Leased by a publisher instance and currently being dispatched.</summary>
    Processing = 1,

    /// <summary>Successfully handed to the broker.</summary>
    Processed = 2,

    /// <summary>Exhausted its retry budget and is no longer delivered.</summary>
    DeadLettered = 3,
}

/// <summary>
/// Read-only view of a persisted outbox message. Implement this interface instead of deriving from
/// <see cref="OutboxMessage"/> when a durable store projects rows into its own type.
/// </summary>
public interface IOutboxMessage
{
    /// <summary>Stable identifier used as the broker message id, so redelivery stays idempotent.</summary>
    Guid Id { get; }

    /// <summary>Namespace-qualified contract name of the serialized payload, used to resolve subscribers.</summary>
    string MessageType { get; }

    /// <summary>Serialized message body. The publisher parses it as a JSON document before dispatching.</summary>
    string Payload { get; }

    /// <summary>
    /// Delivery headers captured at save time, including the destination topic/routing key and the ambient tenant.
    /// </summary>
    MessageHeaders? Headers { get; }

    /// <summary>UTC timestamp of when the message was enqueued; drives FIFO ordering within a batch.</summary>
    DateTime CreatedAtUtc { get; }

    /// <summary>UTC timestamp of successful dispatch, or <see langword="null"/> while still pending.</summary>
    DateTime? ProcessedAtUtc { get; }

    /// <summary>Number of failed delivery attempts recorded so far.</summary>
    int RetryCount { get; }

    /// <summary>Error text from the most recent failed attempt, or <see langword="null"/> when none failed.</summary>
    string? LastError { get; }

    /// <summary>
    /// Lifecycle state. Stores must persist this instead of inferring delivery from <see cref="ProcessedAtUtc"/>,
    /// otherwise dead-lettered messages reappear as pending once their retry budget changes.
    /// </summary>
    OutboxMessageStatus Status =>
        ProcessedAtUtc is not null ? OutboxMessageStatus.Processed : OutboxMessageStatus.Pending;
}

/// <summary>
/// Default in-memory representation of an outbox row, including the delivery state that
/// <see cref="IOutboxMessage"/> does not expose (lease, retry schedule and lifecycle status).
/// </summary>
/// <remarks>
/// Instances are mutable records owned by the store: <see cref="InMemoryOutboxStore"/> copies incoming values and
/// then mutates its own copy while claiming, failing and completing messages.
/// </remarks>
public class OutboxMessage : IOutboxMessage
{
    private static readonly ConcurrentDictionary<
        Type,
        (string? Topic, string? RoutingKey)
    > MessageMetadata = new();

    /// <inheritdoc/>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <inheritdoc/>
    public string MessageType { get; set; } = string.Empty;

    /// <inheritdoc/>
    public string Payload { get; set; } = string.Empty;

    /// <inheritdoc/>
    public MessageHeaders? Headers { get; set; } = new();

    /// <inheritdoc/>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <inheritdoc/>
    public DateTime? ProcessedAtUtc { get; set; }

    /// <inheritdoc/>
    public int RetryCount { get; set; }

    /// <inheritdoc/>
    public string? LastError { get; set; }

    /// <summary>
    /// End of the current delivery lease. While this is in the future other publisher instances must skip the
    /// message; once it elapses the row is claimable again so a crashed publisher cannot stall the queue.
    /// </summary>
    public DateTime? LockedUntilUtc { get; set; }

    /// <summary>
    /// Earliest UTC time for the next delivery attempt after a failure. The exponential backoff schedule is
    /// produced by <see cref="OutboxOptions.GetRetryDelay"/>.
    /// </summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>
    /// Lifecycle state. Stores persist this value; <see cref="IOutboxMessage.Status"/> defaults to <see cref="ProcessedAtUtc"/>-based
    /// inference, but durable stores should persist the explicit status so dead-lettered rows stay out of the pending set.
    /// </summary>
    public OutboxMessageStatus Status { get; set; } = OutboxMessageStatus.Pending;

    /// <summary>
    /// Creates a pending message for <typeparamref name="TMessage"/>. <see cref="MessageType"/> is the
    /// namespace-qualified type name so consumers can resolve the contract without guessing; when the type carries
    /// <see cref="MessageAttribute"/> its topic/routing key become the destination headers, otherwise the publisher
    /// falls back to a sanitized type name. The ambient tenant (when one is active) is captured into the headers so
    /// the owning tenant survives the transaction boundary and the publisher can restore it.
    /// </summary>
    public static OutboxMessage Create<TMessage>(
        TMessage message,
        string payload,
        MessageHeaders? headers = null
    )
    {
        ArgumentNullException.ThrowIfNull(payload);
        var type = typeof(TMessage);
        var resolvedHeaders = headers is null ? new MessageHeaders() : new MessageHeaders(headers);

        var metadata = ResolveMessageMetadata(type);
        if (metadata.Topic is not null)
            resolvedHeaders.DestinationTopic = metadata.Topic;
        if (metadata.RoutingKey is not null)
            resolvedHeaders.RoutingKey = metadata.RoutingKey;
        TenantPropagation.InjectAmbientTenant(resolvedHeaders);

        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            MessageType = type.FullName ?? type.Name,
            Payload = payload,
            Headers = resolvedHeaders,
            CreatedAtUtc = DateTime.UtcNow,
            Status = OutboxMessageStatus.Pending,
        };
    }

    private static (string? Topic, string? RoutingKey) ResolveMessageMetadata(Type type) =>
        MessageMetadata.GetOrAdd(
            type,
            static candidate =>
            {
                try
                {
                    var attribute = candidate.GetCustomAttribute<MessageAttribute>(inherit: false);
                    return attribute is null
                        ? default
                        : (attribute.TopicOrExchange, attribute.RoutingKey);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    // Trimming can strip the attribute; a missing override is not fatal because the publisher
                    // falls back to the sanitized type name.
                    return default;
                }
            }
        );
}
