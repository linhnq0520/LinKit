using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Outbox;

public interface IOutboxStore
{
    Task SaveAsync(IEnumerable<IOutboxMessage> messages, CancellationToken cancellationToken = default);
    Task SaveMessageAsync(IOutboxMessage message, CancellationToken cancellationToken = default) =>
        SaveAsync(new[] { message }, cancellationToken);
    Task<IReadOnlyList<IOutboxMessage>> GetPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default);
    Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken = default);
    Task MarkFailedAsync(Guid messageId, string error, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims pending messages for <paramref name="lease"/> so other publisher instances skip them.
    /// Durable stores should implement this with UPDATE ... SET LockedUntil / SKIP LOCKED. Default falls back to GetPendingAsync.
    /// </summary>
    Task<IReadOnlyList<IOutboxMessage>> ClaimPendingAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken = default) =>
        GetPendingAsync(batchSize, cancellationToken);

    /// <summary>Marks a failure and schedules the next attempt (backoff). Default ignores the schedule.</summary>
    Task MarkFailedAsync(Guid messageId, string error, DateTime? nextAttemptAtUtc, CancellationToken cancellationToken = default) =>
        MarkFailedAsync(messageId, error, cancellationToken);
}

public class InMemoryOutboxStore : IOutboxStore
{
    private readonly ConcurrentDictionary<Guid, OutboxMessage> _messages = new();
    private readonly OutboxOptions _options;
    private readonly TimeProvider _time;

    public InMemoryOutboxStore() : this(new OutboxOptions(), null) { }

    public InMemoryOutboxStore(OutboxOptions options, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task SaveAsync(IEnumerable<IOutboxMessage> messages, CancellationToken cancellationToken = default)
    {
        foreach (var msg in messages)
        {
            if (msg is OutboxMessage concrete)
            {
                _messages[concrete.Id] = concrete;
            }
            else
            {
                _messages[msg.Id] = new OutboxMessage
                {
                    Id = msg.Id,
                    MessageType = msg.MessageType,
                    Payload = msg.Payload,
                    Headers = msg.Headers,
                    CreatedAtUtc = msg.CreatedAtUtc,
                    ProcessedAtUtc = msg.ProcessedAtUtc,
                    RetryCount = msg.RetryCount,
                    LastError = msg.LastError
                };
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IOutboxMessage>> GetPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var pending = _messages.Values
            .Where(m => IsDue(m, now))
            .OrderBy(m => m.CreatedAtUtc)
            .Take(batchSize)
            .Cast<IOutboxMessage>()
            .ToList();

        return Task.FromResult<IReadOnlyList<IOutboxMessage>>(pending);
    }

    private bool IsDue(OutboxMessage m, DateTime now) =>
        m.ProcessedAtUtc == null
        && m.RetryCount < _options.MaxRetries
        && (m.NextAttemptAtUtc == null || m.NextAttemptAtUtc <= now)
        && (m.LockedUntilUtc == null || m.LockedUntilUtc <= now);

    public Task<IReadOnlyList<IOutboxMessage>> ClaimPendingAsync(int batchSize, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var claimed = new List<IOutboxMessage>();
        foreach (var m in _messages.Values.OrderBy(x => x.CreatedAtUtc))
        {
            if (claimed.Count >= batchSize) break;
            lock (m)
            {
                if (!IsDue(m, now)) continue;
                m.LockedUntilUtc = now + lease;
            }
            claimed.Add(m);
        }
        return Task.FromResult<IReadOnlyList<IOutboxMessage>>(claimed);
    }

    /// <summary>Messages that exhausted retries and are no longer delivered.</summary>
    public IReadOnlyList<IOutboxMessage> GetDeadLettered() =>
        _messages.Values.Where(m => m.ProcessedAtUtc == null && m.RetryCount >= _options.MaxRetries).Cast<IOutboxMessage>().ToList();

    public Task MarkFailedAsync(Guid messageId, string error, DateTime? nextAttemptAtUtc, CancellationToken cancellationToken = default)
    {
        if (_messages.TryGetValue(messageId, out var msg))
        {
            lock (msg)
            {
                msg.RetryCount++;
                msg.LastError = error;
                msg.NextAttemptAtUtc = nextAttemptAtUtc;
                msg.LockedUntilUtc = null;
            }
        }
        return Task.CompletedTask;
    }

    public Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        if (_messages.TryGetValue(messageId, out var msg))
        {
            lock (msg)
            {
                msg.ProcessedAtUtc = _time.GetUtcNow().UtcDateTime;
                msg.LastError = null;
                msg.LockedUntilUtc = null;
            }
        }
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(Guid messageId, string error, CancellationToken cancellationToken = default)
    {
        if (_messages.TryGetValue(messageId, out var msg))
        {
            lock (msg)
            {
                msg.RetryCount++;
                msg.LastError = error;
                msg.LockedUntilUtc = null;
            }
        }
        return Task.CompletedTask;
    }
}
