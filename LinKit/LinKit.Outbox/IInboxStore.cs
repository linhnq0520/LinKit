using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Outbox;

/// <summary>
/// Deduplication port for the consumer side of message handling (the inbox pattern). Records which messages a named
/// consumer has already handled so a redelivered broker message is processed at most once.
/// </summary>
/// <remarks>
/// Prefer <see cref="TryMarkProcessedAsync"/> over a <see cref="HasBeenProcessedAsync"/> plus
/// <see cref="MarkProcessedAsync(string, string, TimeSpan?, CancellationToken)"/> pair: only the claim variant is
/// race-free when two consumers receive the same message concurrently.
/// </remarks>
public interface IInboxStore
{
    /// <summary>
    /// Determines whether <paramref name="consumerName"/> has already recorded <paramref name="messageId"/>. Expired
    /// records are treated as absent and are removed.
    /// </summary>
    /// <param name="messageId">Broker message id (or business key) being checked.</param>
    /// <param name="consumerName">Consumer identity; each consumer keeps an independent ledger.</param>
    /// <param name="cancellationToken">Token used to abort the check.</param>
    /// <returns><see langword="true"/> when a live record exists.</returns>
    Task<bool> HasBeenProcessedAsync(
        string messageId,
        string consumerName,
        CancellationToken cancellationToken = default
    );

    /// <summary>Convenience overload that queries the <c>DefaultConsumer</c> ledger.</summary>
    /// <param name="messageId">Broker message id being checked.</param>
    /// <param name="cancellationToken">Token used to abort the check.</param>
    Task<bool> HasProcessedAsync(string messageId, CancellationToken cancellationToken = default) =>
        HasBeenProcessedAsync(messageId, "DefaultConsumer", cancellationToken);

    /// <summary>
    /// Unconditionally records a message as processed, replacing any existing record.
    /// </summary>
    /// <param name="messageId">Broker message id being recorded.</param>
    /// <param name="consumerName">Consumer identity; each consumer keeps an independent ledger.</param>
    /// <param name="expiry">Optional time-to-live after which the record may be re-claimed.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    Task MarkProcessedAsync(
        string messageId,
        string consumerName,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>Convenience overload that writes to the <c>DefaultConsumer</c> ledger with no expiry.</summary>
    /// <param name="messageId">Broker message id being recorded.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
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
    Task<bool> TryMarkProcessedAsync(
        string messageId,
        string consumerName,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// Volatile inbox ledger used for development, tests and samples. Records live in process memory and are lost on
/// restart.
/// </summary>
/// <remarks>
/// The claim logic is fully atomic through <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>,
/// so it is a usable stand-in wherever a durable ledger is not required.
/// </remarks>
public class InMemoryInboxStore : IInboxStore
{
    private class InboxRecord
    {
        public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? ExpiresAtUtc { get; set; }
    }

    private readonly ConcurrentDictionary<
        (string Consumer, string MessageId),
        InboxRecord
    > _processed = new();

    /// <inheritdoc/>
    public Task<bool> HasBeenProcessedAsync(
        string messageId,
        string consumerName,
        CancellationToken cancellationToken = default
    )
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

    /// <inheritdoc/>
    public Task MarkProcessedAsync(
        string messageId,
        string consumerName,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default
    )
    {
        var key = (consumerName, messageId);
        _processed[key] = new InboxRecord
        {
            ProcessedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = expiry.HasValue ? DateTime.UtcNow.Add(expiry.Value) : null,
        };
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> TryMarkProcessedAsync(
        string messageId,
        string consumerName,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default
    )
    {
        var key = (consumerName, messageId);
        var now = DateTime.UtcNow;
        var record = new InboxRecord
        {
            ProcessedAtUtc = now,
            ExpiresAtUtc = expiry.HasValue ? now.Add(expiry.Value) : null,
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
