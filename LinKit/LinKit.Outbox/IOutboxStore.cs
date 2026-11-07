using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Outbox;

public interface IOutboxStore
{
    Task SaveAsync(
        IEnumerable<IOutboxMessage> messages,
        CancellationToken cancellationToken = default
    );
    Task SaveMessageAsync(IOutboxMessage message, CancellationToken cancellationToken = default) =>
        SaveAsync(new[] { message }, cancellationToken);
    Task<IReadOnlyList<IOutboxMessage>> GetPendingAsync(
        int batchSize = 100,
        CancellationToken cancellationToken = default
    );
    Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed delivery attempt. <paramref name="nextAttemptAtUtc"/> schedules the next attempt; when the
    /// message has exhausted <see cref="OutboxOptions.MaxRetries"/> it must be moved to
    /// <see cref="OutboxMessageStatus.DeadLettered"/> and stop being returned by <see cref="GetPendingAsync"/>.
    /// The parameter order is (id, error, schedule, cancellation) so a bare <c>default</c> argument is never
    /// ambiguous between the schedule and the token.
    /// </summary>
    Task MarkFailedAsync(
        Guid messageId,
        string error,
        DateTime? nextAttemptAtUtc,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Atomically claims pending messages for <paramref name="lease"/> so other publisher instances skip them.
    /// Durable stores implement this with UPDATE ... SKIP LOCKED / READPAST; the default falls back to
    /// <see cref="GetPendingAsync"/> and therefore offers no cross-instance exclusion guarantee.
    /// </summary>
    Task<IReadOnlyList<IOutboxMessage>> ClaimPendingAsync(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken = default
    ) => GetPendingAsync(batchSize, cancellationToken);

    /// <summary>
    /// Messages that exhausted their retry budget. Durable stores must override this; the default returns none so
    /// that existing custom stores keep compiling.
    /// </summary>
    Task<IReadOnlyList<IOutboxMessage>> GetDeadLetteredAsync(
        int batchSize = 100,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IReadOnlyList<IOutboxMessage>>(Array.Empty<IOutboxMessage>());
}

public class InMemoryOutboxStore : IOutboxStore
{
    private readonly ConcurrentDictionary<Guid, OutboxMessage> _messages = new();
    private readonly OutboxOptions _options;
    private readonly TimeProvider _time;

    public InMemoryOutboxStore()
        : this(new OutboxOptions(), null) { }

    public InMemoryOutboxStore(OutboxOptions options, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task SaveAsync(
        IEnumerable<IOutboxMessage> messages,
        CancellationToken cancellationToken = default
    )
    {
        foreach (var msg in messages)
        {
            var concrete = msg as OutboxMessage ?? new OutboxMessage();
            _messages[msg.Id] = new OutboxMessage
            {
                Id = msg.Id,
                MessageType = msg.MessageType,
                Payload = msg.Payload,
                Headers = msg.Headers,
                CreatedAtUtc = msg.CreatedAtUtc,
                ProcessedAtUtc = msg.ProcessedAtUtc,
                RetryCount = msg.RetryCount,
                LastError = msg.LastError,
                LockedUntilUtc = concrete.LockedUntilUtc,
                NextAttemptAtUtc = concrete.NextAttemptAtUtc,
                Status = msg.Status,
            };
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IOutboxMessage>> GetPendingAsync(
        int batchSize = 100,
        CancellationToken cancellationToken = default
    )
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var pending = _messages
            .Values.Where(m => IsDue(m, now))
            .OrderBy(m => m.CreatedAtUtc)
            .Take(batchSize)
            .Cast<IOutboxMessage>()
            .ToList();

        return Task.FromResult<IReadOnlyList<IOutboxMessage>>(pending);
    }

    // Claimable states: a Pending row, or a Processing row whose lease already expired (crash recovery).
    private bool IsDue(OutboxMessage message, DateTime now) =>
        message.Status is OutboxMessageStatus.Pending or OutboxMessageStatus.Processing
        && (message.NextAttemptAtUtc == null || message.NextAttemptAtUtc <= now)
        && (message.LockedUntilUtc == null || message.LockedUntilUtc <= now);

    public Task<IReadOnlyList<IOutboxMessage>> ClaimPendingAsync(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken = default
    )
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var claimed = new List<IOutboxMessage>();
        foreach (var message in _messages.Values.OrderBy(x => x.CreatedAtUtc))
        {
            if (claimed.Count >= batchSize)
                break;
            lock (message)
            {
                if (!IsDue(message, now))
                    continue;
                message.LockedUntilUtc = now + lease;
                message.Status = OutboxMessageStatus.Processing;
            }
            claimed.Add(message);
        }
        return Task.FromResult<IReadOnlyList<IOutboxMessage>>(claimed);
    }

    /// <summary>Messages that exhausted retries and are no longer delivered.</summary>
    public IReadOnlyList<IOutboxMessage> GetDeadLettered() =>
        _messages
            .Values.Where(m => m.Status == OutboxMessageStatus.DeadLettered)
            .OrderBy(m => m.CreatedAtUtc)
            .Cast<IOutboxMessage>()
            .ToList();

    public Task<IReadOnlyList<IOutboxMessage>> GetDeadLetteredAsync(
        int batchSize = 100,
        CancellationToken cancellationToken = default
    )
    {
        var dead = GetDeadLettered().Take(batchSize).ToList();
        return Task.FromResult<IReadOnlyList<IOutboxMessage>>(dead);
    }

    public Task MarkFailedAsync(
        Guid messageId,
        string error,
        DateTime? nextAttemptAtUtc,
        CancellationToken cancellationToken = default
    )
    {
        if (_messages.TryGetValue(messageId, out var message))
        {
            lock (message)
            {
                message.RetryCount++;
                message.LastError = error;
                message.NextAttemptAtUtc = nextAttemptAtUtc;
                message.LockedUntilUtc = null;
                message.Status =
                    message.RetryCount >= _options.MaxRetries
                        ? OutboxMessageStatus.DeadLettered
                        : OutboxMessageStatus.Pending;
            }
        }
        return Task.CompletedTask;
    }

    public Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        if (_messages.TryGetValue(messageId, out var message))
        {
            lock (message)
            {
                message.ProcessedAtUtc = _time.GetUtcNow().UtcDateTime;
                message.LastError = null;
                message.LockedUntilUtc = null;
                message.Status = OutboxMessageStatus.Processed;
            }
        }
        return Task.CompletedTask;
    }
}
