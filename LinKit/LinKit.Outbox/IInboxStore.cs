using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Outbox;

public interface IInboxStore
{
    Task<bool> HasBeenProcessedAsync(string messageId, string consumerName, CancellationToken cancellationToken = default);
    Task<bool> HasProcessedAsync(string messageId, CancellationToken cancellationToken = default) =>
        HasBeenProcessedAsync(messageId, "DefaultConsumer", cancellationToken);

    Task MarkProcessedAsync(string messageId, string consumerName, TimeSpan? expiry = null, CancellationToken cancellationToken = default);
    Task MarkProcessedAsync(string messageId, CancellationToken cancellationToken = default) =>
        MarkProcessedAsync(messageId, "DefaultConsumer", null, cancellationToken);

    /// <summary>
    /// Atomically claims a message for processing by recording it as processed (insert-only).
    /// Returns <c>true</c> if this call recorded the message and the caller may process it;
    /// <c>false</c> if a live (non-expired) record already exists and the caller must skip it.
    /// This is the race-free alternative to <see cref="HasBeenProcessedAsync"/> followed by
    /// <see cref="MarkProcessedAsync(string, string, TimeSpan?, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// This is a claim-before-processing operation: a consumer that crashes after a successful
    /// claim leaves the message marked as processed (at-most-once tradeoff). Supply an
    /// <paramref name="expiry"/> so an abandoned claim can be re-claimed once it elapses;
    /// expired records are treated exactly as in <see cref="HasBeenProcessedAsync"/>.
    /// </remarks>
    Task<bool> TryMarkProcessedAsync(string messageId, string consumerName, TimeSpan? expiry = null, CancellationToken cancellationToken = default);
}

public class InMemoryInboxStore : IInboxStore
{
    private class InboxRecord
    {
        public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? ExpiresAtUtc { get; set; }
    }

    private readonly ConcurrentDictionary<(string Consumer, string MessageId), InboxRecord> _processed = new();

    public Task<bool> HasBeenProcessedAsync(string messageId, string consumerName, CancellationToken cancellationToken = default)
    {
        var key = (consumerName, messageId);
        if (_processed.TryGetValue(key, out var record))
        {
            if (record.ExpiresAtUtc == null || record.ExpiresAtUtc > DateTime.UtcNow)
            {
                return Task.FromResult(true);
            }
            _processed.TryRemove(key, out _);
        }
        return Task.FromResult(false);
    }

    public Task MarkProcessedAsync(string messageId, string consumerName, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        var key = (consumerName, messageId);
        _processed[key] = new InboxRecord
        {
            ProcessedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiry.HasValue ? DateTime.UtcNow.Add(expiry.Value) : null
        };
        return Task.CompletedTask;
    }

    public Task<bool> TryMarkProcessedAsync(string messageId, string consumerName, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        var key = (consumerName, messageId);
        var now = DateTime.UtcNow;
        var record = new InboxRecord
        {
            ProcessedAtUtc = now,
            ExpiresAtUtc = expiry.HasValue ? now.Add(expiry.Value) : null
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_processed.TryAdd(key, record))
                return Task.FromResult(true);

            if (!_processed.TryGetValue(key, out var existing))
                continue;

            if (existing.ExpiresAtUtc == null || existing.ExpiresAtUtc > now)
                return Task.FromResult(false);

            // The existing claim expired; replace it only if it is still the same record.
            if (_processed.TryUpdate(key, record, existing))
                return Task.FromResult(true);
        }
    }
}
