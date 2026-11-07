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

public interface IOutboxMessage
{
    Guid Id { get; }
    string MessageType { get; }
    string Payload { get; }
    MessageHeaders? Headers { get; }
    DateTime CreatedAtUtc { get; }
    DateTime? ProcessedAtUtc { get; }
    int RetryCount { get; }
    string? LastError { get; }

    /// <summary>
    /// Lifecycle state. Stores must persist this instead of inferring delivery from <see cref="ProcessedAtUtc"/>,
    /// otherwise dead-lettered messages reappear as pending once their retry budget changes.
    /// </summary>
    OutboxMessageStatus Status =>
        ProcessedAtUtc is not null ? OutboxMessageStatus.Processed : OutboxMessageStatus.Pending;
}

public class OutboxMessage : IOutboxMessage
{
    private static readonly ConcurrentDictionary<
        Type,
        (string? Topic, string? RoutingKey)
    > MessageMetadata = new();

    public Guid Id { get; set; } = Guid.NewGuid();
    public string MessageType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public MessageHeaders? Headers { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
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
