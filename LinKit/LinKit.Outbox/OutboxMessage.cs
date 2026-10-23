using System;
using LinKit.Core.Messaging;

namespace LinKit.Outbox;

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
}

public class OutboxMessage : IOutboxMessage
{
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

    public static OutboxMessage Create<TMessage>(TMessage message, string payload, MessageHeaders? headers = null)
    {
        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            MessageType = typeof(TMessage).Name,
            Payload = payload,
            Headers = headers ?? new MessageHeaders(),
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
