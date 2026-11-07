using System.Data;
using System.Data.Common;
using System.Globalization;
using LinKit.Data;
using LinKit.Outbox;

namespace LinKit.Outbox.Data;

public sealed class AdoNetOutboxStore : IOutboxStore, IAdoNetSchemaStore
{
    private static readonly string[] Columns =
    {
        "Id",
        "MessageType",
        "Payload",
        "Headers",
        "CreatedAtUtc",
        "ProcessedAtUtc",
        "RetryCount",
        "LastError",
        "LockedUntilUtc",
        "NextAttemptAtUtc",
        "Status",
    };

    private readonly IDbConnectionFactory _factory;
    private readonly IUnitOfWork _unitOfWork;
    private readonly OutboxOptions _options;
    private readonly DatabaseDialect _dialect;
    private readonly TimeProvider _time;

    public AdoNetOutboxStore(
        IDbConnectionFactory factory,
        IUnitOfWork unitOfWork,
        OutboxOptions options,
        TimeProvider? timeProvider = null
    )
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _dialect = factory.Dialect;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        var dateType = AdoNetStoreSupport.GetDateTimeType(_dialect);
        var textType = AdoNetStoreSupport.GetTextType(_dialect);
        var idType = AdoNetStoreSupport.GetStringType(_dialect, 36);
        var columns = string.Join(
            ", ",
            $"{Q("Id")} {idType} NOT NULL PRIMARY KEY",
            $"{Q("MessageType")} {AdoNetStoreSupport.GetStringType(_dialect, 512)} NOT NULL",
            $"{Q("Payload")} {textType} NOT NULL",
            $"{Q("Headers")} {textType} NULL",
            $"{Q("CreatedAtUtc")} {dateType} NOT NULL",
            $"{Q("ProcessedAtUtc")} {dateType} NULL",
            $"{Q("RetryCount")} INTEGER NOT NULL",
            $"{Q("LastError")} {textType} NULL",
            $"{Q("LockedUntilUtc")} {dateType} NULL",
            $"{Q("NextAttemptAtUtc")} {dateType} NULL",
            $"{Q("Status")} INTEGER NOT NULL DEFAULT {(int)OutboxMessageStatus.Pending}"
        );
        var indexColumns = string.Join(
            ", ",
            Q("ProcessedAtUtc"),
            Q("NextAttemptAtUtc"),
            Q("LockedUntilUtc"),
            Q("CreatedAtUtc")
        );

        await using var connection = await AdoNetStoreSupport
            .OpenIndependentConnectionAsync(_factory, cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = AdoNetStoreSupport.CreateTableSql(
            _dialect,
            AdoNetStoreSupport.OutboxTable,
            columns
        );
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Upgrade tables created before the explicit status column existed; CREATE TABLE IF NOT EXISTS is a no-op there.
        await EnsureStatusColumnAsync(connection, cancellationToken).ConfigureAwait(false);

        if (_dialect == DatabaseDialect.MySql)
        {
            await EnsureMySqlIndexAsync(
                    connection,
                    null,
                    AdoNetStoreSupport.OutboxTable,
                    "IX_LinKitOutbox_Pending",
                    indexColumns,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        else
        {
            command.CommandText = AdoNetStoreSupport.CreateIndexSql(
                _dialect,
                "IX_LinKitOutbox_Pending",
                AdoNetStoreSupport.OutboxTable,
                indexColumns
            );
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureStatusColumnAsync(
        DbConnection connection,
        CancellationToken cancellationToken
    )
    {
        await using var probe = connection.CreateCommand();
        probe.CommandText =
            $"SELECT {Q("Status")} FROM {AdoNetStoreSupport.Quote(_dialect, AdoNetStoreSupport.OutboxTable)} WHERE 1 = 0";
        try
        {
            await probe.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (DbException)
        {
            // Column is missing on a table created by an earlier version.
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText =
            $"ALTER TABLE {AdoNetStoreSupport.Quote(_dialect, AdoNetStoreSupport.OutboxTable)} "
            + $"ADD {Q("Status")} INTEGER NOT NULL DEFAULT {(int)OutboxMessageStatus.Pending}";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        IEnumerable<IOutboxMessage> messages,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(messages);
        var materialized = messages.ToArray();
        if (materialized.Length == 0)
            return;
        foreach (var message in materialized)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (message.Id == Guid.Empty)
                throw new ArgumentException(
                    "Outbox message ID must not be empty.",
                    nameof(messages)
                );
            ArgumentException.ThrowIfNullOrWhiteSpace(message.MessageType);
            ArgumentNullException.ThrowIfNull(message.Payload);
            if (message.RetryCount < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(messages),
                    "Outbox retry count cannot be negative."
                );
        }

        await AdoNetStoreSupport
            .WithTransactionAsync(
                _factory,
                _unitOfWork,
                async (connection, transaction) =>
                {
                    foreach (var message in materialized)
                    {
                        await using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText =
                            $"INSERT INTO {AdoNetStoreSupport.Quote(_dialect, AdoNetStoreSupport.OutboxTable)} "
                            + $"({string.Join(", ", Columns.Select(Q))}) "
                            + $"VALUES ({P("id")}, {P("messageType")}, {P("payload")}, {P("headers")}, {P("createdAtUtc")}, {P("processedAtUtc")}, {P("retryCount")}, {P("lastError")}, {P("lockedUntilUtc")}, {P("nextAttemptAtUtc")}, {P("status")})";
                        Add(command, "id", message.Id.ToString("D"));
                        Add(command, "messageType", message.MessageType);
                        Add(command, "payload", message.Payload);
                        Add(
                            command,
                            "headers",
                            AdoNetStoreSupport.SerializeHeaders(message.Headers)
                        );
                        Add(
                            command,
                            "createdAtUtc",
                            AdoNetStoreSupport.NormalizeUtc(message.CreatedAtUtc),
                            DbType.DateTime
                        );
                        Add(
                            command,
                            "processedAtUtc",
                            message.ProcessedAtUtc.HasValue
                                ? AdoNetStoreSupport.NormalizeUtc(message.ProcessedAtUtc.Value)
                                : null,
                            DbType.DateTime
                        );
                        Add(command, "retryCount", message.RetryCount);
                        Add(command, "lastError", message.LastError, DbType.String);
                        Add(
                            command,
                            "lockedUntilUtc",
                            message is OutboxMessage concrete && concrete.LockedUntilUtc.HasValue
                                ? AdoNetStoreSupport.NormalizeUtc(concrete.LockedUntilUtc.Value)
                                : null,
                            DbType.DateTime
                        );
                        Add(
                            command,
                            "nextAttemptAtUtc",
                            message is OutboxMessage withSchedule
                            && withSchedule.NextAttemptAtUtc.HasValue
                                ? AdoNetStoreSupport.NormalizeUtc(
                                    withSchedule.NextAttemptAtUtc.Value
                                )
                                : null,
                            DbType.DateTime
                        );
                        Add(command, "status", (int)message.Status);
                        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public Task SaveMessageAsync(
        IOutboxMessage message,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(message);
        return SaveAsync(new[] { message }, cancellationToken);
    }

    public async Task<IReadOnlyList<IOutboxMessage>> GetPendingAsync(
        int batchSize = 100,
        CancellationToken cancellationToken = default
    )
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        await using var session = await AdoNetStoreSupport
            .OpenAsync(_factory, _unitOfWork, cancellationToken)
            .ConfigureAwait(false);
        await using var command = session.Connection.CreateCommand();
        command.Transaction = session.Transaction;
        command.CommandText = BuildPendingSelect(batchSize);
        AddPendingParameters(command, _time.GetUtcNow().UtcDateTime);
        var messages = new List<IOutboxMessage>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            messages.Add(ReadMessage(reader));
        return messages;
    }

    public async Task<IReadOnlyList<IOutboxMessage>> GetDeadLetteredAsync(
        int batchSize = 100,
        CancellationToken cancellationToken = default
    )
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        await using var session = await AdoNetStoreSupport
            .OpenAsync(_factory, _unitOfWork, cancellationToken)
            .ConfigureAwait(false);
        await using var command = session.Connection.CreateCommand();
        command.Transaction = session.Transaction;
        command.CommandText =
            $"SELECT {SelectColumns()} FROM {Table} WHERE {Q("Status")} = {(int)OutboxMessageStatus.DeadLettered} "
            + $"ORDER BY {Q("CreatedAtUtc")} ASC {LimitClause(batchSize)}";
        var messages = new List<IOutboxMessage>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            messages.Add(ReadMessage(reader));
        return messages;
    }

    /// <summary>
    /// Claims a batch atomically. PostgreSQL, SQL Server and MySQL use a single lock-and-return statement so two
    /// publisher instances can never receive the same message; SQLite and Oracle keep the two-step
    /// select-then-lock inside one transaction and therefore assume a single writer per table.
    /// </summary>
    public Task<IReadOnlyList<IOutboxMessage>> ClaimPendingAsync(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken = default
    )
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (lease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lease));

        return _dialect switch
        {
            DatabaseDialect.PostgreSql => ClaimWithReturningAsync(
                batchSize,
                lease,
                cancellationToken
            ),
            DatabaseDialect.SqlServer => ClaimWithOutputAsync(batchSize, lease, cancellationToken),
            DatabaseDialect.MySql => ClaimWithSkipLockedAsync(batchSize, lease, cancellationToken),
            _ => ClaimWithTransactionAsync(batchSize, lease, cancellationToken),
        };
    }

    private async Task<IReadOnlyList<IOutboxMessage>> ClaimWithReturningAsync(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken
    )
    {
        var claimed = new List<IOutboxMessage>();
        await AdoNetStoreSupport
            .WithTransactionAsync(
                _factory,
                _unitOfWork,
                async (connection, transaction) =>
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        $"UPDATE {Table} SET {Q("LockedUntilUtc")} = {P("lockedUntilUtc")}, {Q("Status")} = {(int)OutboxMessageStatus.Processing} "
                        + $"WHERE {Q("Id")} IN (SELECT {Q("Id")} FROM {Table} WHERE {PendingPredicate()} "
                        + $"ORDER BY {Q("CreatedAtUtc")} ASC LIMIT {batchSize} FOR UPDATE SKIP LOCKED) "
                        + $"RETURNING {SelectColumns()}";
                    var now = _time.GetUtcNow().UtcDateTime;
                    Add(command, "lockedUntilUtc", now + lease, DbType.DateTime);
                    AddPendingParameters(command, now);
                    await using var reader = await command
                        .ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        claimed.Add(ReadMessage(reader));
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return claimed;
    }

    private async Task<IReadOnlyList<IOutboxMessage>> ClaimWithOutputAsync(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken
    )
    {
        var output = string.Join(", ", Columns.Select(column => $"INSERTED.{Q(column)}"));
        var claimed = new List<IOutboxMessage>();
        await AdoNetStoreSupport
            .WithTransactionAsync(
                _factory,
                _unitOfWork,
                async (connection, transaction) =>
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        $"UPDATE TOP ({batchSize}) {Table} WITH (ROWLOCK, READPAST) "
                        + $"SET {Q("LockedUntilUtc")} = {P("lockedUntilUtc")}, {Q("Status")} = {(int)OutboxMessageStatus.Processing} "
                        + $"OUTPUT {output} WHERE {PendingPredicate()}";
                    var now = _time.GetUtcNow().UtcDateTime;
                    Add(command, "lockedUntilUtc", now + lease, DbType.DateTime);
                    AddPendingParameters(command, now);
                    await using var reader = await command
                        .ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        claimed.Add(ReadMessage(reader));
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return claimed;
    }

    private async Task<IReadOnlyList<IOutboxMessage>> ClaimWithSkipLockedAsync(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken
    )
    {
        var claimed = new List<OutboxMessage>();
        await AdoNetStoreSupport
            .WithTransactionAsync(
                _factory,
                _unitOfWork,
                async (connection, transaction) =>
                {
                    var now = _time.GetUtcNow().UtcDateTime;
                    await using (var select = connection.CreateCommand())
                    {
                        select.Transaction = transaction;
                        select.CommandText =
                            $"SELECT {SelectColumns()} FROM {Table} WHERE {PendingPredicate()} "
                            + $"ORDER BY {Q("CreatedAtUtc")} ASC LIMIT {batchSize} FOR UPDATE SKIP LOCKED";
                        AddPendingParameters(select, now);
                        await using var reader = await select
                            .ExecuteReaderAsync(cancellationToken)
                            .ConfigureAwait(false);
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            claimed.Add(ReadMessage(reader));
                    }

                    if (claimed.Count == 0)
                        return;

                    var names = new List<string>(claimed.Count);
                    await using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    for (var index = 0; index < claimed.Count; index++)
                    {
                        var name = "id" + index.ToString(CultureInfo.InvariantCulture);
                        names.Add(P(name));
                        Add(update, name, claimed[index].Id.ToString("D"));
                    }
                    update.CommandText =
                        $"UPDATE {Table} SET {Q("LockedUntilUtc")} = {P("lockedUntilUtc")}, {Q("Status")} = {(int)OutboxMessageStatus.Processing} "
                        + $"WHERE {Q("Id")} IN ({string.Join(", ", names)})";
                    Add(update, "lockedUntilUtc", now + lease, DbType.DateTime);
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    foreach (var message in claimed)
                    {
                        message.LockedUntilUtc = now + lease;
                        message.Status = OutboxMessageStatus.Processing;
                    }
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return claimed;
    }

    /// <summary>
    /// Fallback for SQLite and Oracle, where <c>SKIP LOCKED</c> is unavailable. The select and the per-row lock run in
    /// one transaction, so correctness depends on a single writer against the outbox table.
    /// </summary>
    private async Task<IReadOnlyList<IOutboxMessage>> ClaimWithTransactionAsync(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken
    )
    {
        var claimed = new List<IOutboxMessage>();
        await AdoNetStoreSupport
            .WithTransactionAsync(
                _factory,
                _unitOfWork,
                async (connection, transaction) =>
                {
                    await using var select = connection.CreateCommand();
                    select.Transaction = transaction;
                    select.CommandText = BuildPendingSelect(batchSize);
                    var selectionTime = _time.GetUtcNow().UtcDateTime;
                    AddPendingParameters(select, selectionTime);
                    var candidates = new List<OutboxMessage>();
                    await using (
                        var reader = await select
                            .ExecuteReaderAsync(cancellationToken)
                            .ConfigureAwait(false)
                    )
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            candidates.Add(ReadMessage(reader));
                    }

                    var now = _time.GetUtcNow().UtcDateTime;
                    foreach (var message in candidates)
                    {
                        await using var update = connection.CreateCommand();
                        update.Transaction = transaction;
                        update.CommandText =
                            $"UPDATE {Table} SET {Q("LockedUntilUtc")} = {P("lockedUntilUtc")}, {Q("Status")} = {(int)OutboxMessageStatus.Processing} "
                            + $"WHERE {Q("Id")} = {P("id")} AND {PendingPredicate()}";
                        Add(update, "lockedUntilUtc", now + lease, DbType.DateTime);
                        Add(update, "id", message.Id.ToString("D"));
                        AddPendingParameters(update, now);
                        if (
                            await update
                                .ExecuteNonQueryAsync(cancellationToken)
                                .ConfigureAwait(false) == 1
                        )
                        {
                            message.LockedUntilUtc = now + lease;
                            message.Status = OutboxMessageStatus.Processing;
                            claimed.Add(message);
                        }
                    }
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return claimed;
    }

    public Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(
            messageId,
            processed: true,
            error: null,
            nextAttemptAtUtc: null,
            cancellationToken
        );

    public Task MarkFailedAsync(
        Guid messageId,
        string error,
        DateTime? nextAttemptAtUtc,
        CancellationToken cancellationToken = default
    ) => UpdateStatusAsync(messageId, processed: false, error, nextAttemptAtUtc, cancellationToken);

    private async Task UpdateStatusAsync(
        Guid messageId,
        bool processed,
        string? error,
        DateTime? nextAttemptAtUtc,
        CancellationToken cancellationToken
    )
    {
        await using var session = await AdoNetStoreSupport
            .OpenAsync(_factory, _unitOfWork, cancellationToken)
            .ConfigureAwait(false);
        await using var command = session.Connection.CreateCommand();
        command.Transaction = session.Transaction;
        if (processed)
        {
            command.CommandText =
                $"UPDATE {Table} SET {Q("ProcessedAtUtc")} = {P("processedAtUtc")}, {Q("LastError")} = NULL, {Q("LockedUntilUtc")} = NULL, "
                + $"{Q("Status")} = {(int)OutboxMessageStatus.Processed} WHERE {Q("Id")} = {P("id")}";
            Add(command, "processedAtUtc", _time.GetUtcNow().UtcDateTime, DbType.DateTime);
        }
        else
        {
            // Dead-letter as soon as the new retry count reaches the budget; the CASE reads the pre-update value.
            command.CommandText =
                $"UPDATE {Table} SET {Q("RetryCount")} = {Q("RetryCount")} + 1, {Q("LastError")} = {P("lastError")}, "
                + $"{Q("NextAttemptAtUtc")} = {P("nextAttemptAtUtc")}, {Q("LockedUntilUtc")} = NULL, "
                + $"{Q("Status")} = CASE WHEN {Q("RetryCount")} + 1 >= {P("maxRetries")} THEN {(int)OutboxMessageStatus.DeadLettered} ELSE {(int)OutboxMessageStatus.Pending} END "
                + $"WHERE {Q("Id")} = {P("id")}";
            Add(command, "lastError", error, DbType.String);
            Add(
                command,
                "nextAttemptAtUtc",
                nextAttemptAtUtc.HasValue
                    ? AdoNetStoreSupport.NormalizeUtc(nextAttemptAtUtc.Value)
                    : null,
                DbType.DateTime
            );
            Add(command, "maxRetries", _options.MaxRetries);
        }
        Add(command, "id", messageId.ToString("D"));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
            throw new KeyNotFoundException($"Outbox message '{messageId}' does not exist.");
    }

    private string Table => AdoNetStoreSupport.Quote(_dialect, AdoNetStoreSupport.OutboxTable);

    private string SelectColumns() => string.Join(", ", Columns.Select(Q));

    private string LimitClause(int batchSize) =>
        _dialect switch
        {
            DatabaseDialect.SqlServer => string.Empty,
            DatabaseDialect.Oracle => $"FETCH FIRST {batchSize} ROWS ONLY",
            _ => $"LIMIT {batchSize}",
        };

    /// <summary>
    /// Rows a publisher may take: still Pending, or Processing with an expired lease (publisher crash recovery),
    /// never Processed or DeadLettered.
    /// </summary>
    private string PendingPredicate()
    {
        var claimable =
            $"{(int)OutboxMessageStatus.Pending}, {(int)OutboxMessageStatus.Processing}";
        return $"{Q("Status")} IN ({claimable}) AND {Q("RetryCount")} < {P("maxRetries")} "
            + $"AND ({Q("NextAttemptAtUtc")} IS NULL OR {Q("NextAttemptAtUtc")} <= {P("nextAttemptNow")}) "
            + $"AND ({Q("LockedUntilUtc")} IS NULL OR {Q("LockedUntilUtc")} <= {P("leaseNow")})";
    }

    private string BuildPendingSelect(int batchSize)
    {
        var order = $"ORDER BY {Q("CreatedAtUtc")} ASC";
        return _dialect switch
        {
            DatabaseDialect.SqlServer =>
                $"SELECT TOP ({batchSize}) {SelectColumns()} FROM {Table} WHERE {PendingPredicate()} {order}",
            _ =>
                $"SELECT {SelectColumns()} FROM {Table} WHERE {PendingPredicate()} {order} {LimitClause(batchSize)}",
        };
    }

    private void AddPendingParameters(DbCommand command, DateTime now)
    {
        Add(command, "maxRetries", _options.MaxRetries);
        Add(command, "nextAttemptNow", now, DbType.DateTime);
        Add(command, "leaseNow", now, DbType.DateTime);
    }

    private static OutboxMessage ReadMessage(DbDataReader reader) =>
        new()
        {
            Id = Guid.Parse(AdoNetStoreSupport.ReadNullableString(reader, "Id")!),
            MessageType = AdoNetStoreSupport.ReadNullableString(reader, "MessageType")!,
            Payload = AdoNetStoreSupport.ReadNullableString(reader, "Payload")!,
            Headers = AdoNetStoreSupport.DeserializeHeaders(
                AdoNetStoreSupport.ReadNullableString(reader, "Headers")
            ),
            CreatedAtUtc = AdoNetStoreSupport.ReadUtcDateTime(reader, "CreatedAtUtc"),
            ProcessedAtUtc = AdoNetStoreSupport.ReadNullableUtcDateTime(reader, "ProcessedAtUtc"),
            RetryCount = Convert.ToInt32(
                reader.GetValue(reader.GetOrdinal("RetryCount")),
                CultureInfo.InvariantCulture
            ),
            LastError = AdoNetStoreSupport.ReadNullableString(reader, "LastError"),
            LockedUntilUtc = AdoNetStoreSupport.ReadNullableUtcDateTime(reader, "LockedUntilUtc"),
            NextAttemptAtUtc = AdoNetStoreSupport.ReadNullableUtcDateTime(
                reader,
                "NextAttemptAtUtc"
            ),
            Status = ReadStatus(reader),
        };

    private static OutboxMessageStatus ReadStatus(DbDataReader reader)
    {
        var ordinal = reader.GetOrdinal("Status");
        return reader.IsDBNull(ordinal)
            ? OutboxMessageStatus.Pending
            : (OutboxMessageStatus)
                Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private async Task EnsureMySqlIndexAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string tableName,
        string indexName,
        string columns,
        CancellationToken cancellationToken
    )
    {
        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText =
            "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = @tableName AND index_name = @indexName";
        Add(check, "tableName", tableName);
        Add(check, "indexName", indexName);
        if (
            Convert.ToInt64(
                await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture
            ) == 0
        )
        {
            await using var create = connection.CreateCommand();
            create.Transaction = transaction;
            create.CommandText = AdoNetStoreSupport.CreateIndexSql(
                _dialect,
                indexName,
                tableName,
                columns
            );
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private string Q(string name) => AdoNetStoreSupport.Quote(_dialect, name);

    private string P(string name) => AdoNetStoreSupport.ParameterPrefix(_dialect) + name;

    private void Add(DbCommand command, string name, object? value, DbType? dbType = null) =>
        AdoNetStoreSupport.AddParameter(command, _dialect, name, value, dbType);
}
