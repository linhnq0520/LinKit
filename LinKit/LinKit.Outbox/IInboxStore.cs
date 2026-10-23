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
}

public class InMemoryInboxStore : IInboxStore
{
    private class InboxRecord
    {
        public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? ExpiresAtUtc { get; set; }
    }

    private readonly ConcurrentDictionary<string, InboxRecord> _processed = new();

    public Task<bool> HasBeenProcessedAsync(string messageId, string consumerName, CancellationToken cancellationToken = default)
    {
        string key = $"{consumerName}:{messageId}";
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
        string key = $"{consumerName}:{messageId}";
        _processed[key] = new InboxRecord
        {
            ProcessedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiry.HasValue ? DateTime.UtcNow.Add(expiry.Value) : null
        };
        return Task.CompletedTask;
    }
}
