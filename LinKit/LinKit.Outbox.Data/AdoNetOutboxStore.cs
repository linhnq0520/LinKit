using System.Data;
using System.Data.Common;
using LinKit.Data;
using LinKit.Outbox;

namespace LinKit.Outbox.Data;

public sealed class AdoNetOutboxStore : IOutboxStore, IAdoNetSchemaStore
{
    private readonly IDbConnectionFactory _factory;
    private readonly IUnitOfWork _unitOfWork;
    private readonly OutboxOptions _options;
    private readonly DatabaseDialect _dialect;
    private readonly TimeProvider _time;

    public AdoNetOutboxStore(
        IDbConnectionFactory factory,
        IUnitOfWork unitOfWork,
        OutboxOptions options,
        TimeProvider? timeProvider = null)
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
        var columns = string.Join(", ",
            $"{Q("Id")} {idType} NOT NULL PRIMARY KEY",
            $"{Q("MessageType")} {AdoNetStoreSupport.GetStringType(_dialect, 512)} NOT NULL",
            $"{Q("Payload")} {textType} NOT NULL",
            $"{Q("Headers")} {textType} NULL",
            $"{Q("CreatedAtUtc")} {dateType} NOT NULL",
            $"{Q("ProcessedAtUtc")} {dateType} NULL",
            $"{Q("RetryCount")} INTEGER NOT NULL",
            $"{Q("LastError")} {textType} NULL",
            $"{Q("LockedUntilUtc")} {dateType} NULL",
            $"{Q("NextAttemptAtUtc")} {dateType} NULL");
        var indexColumns = string.Join(", ", Q("ProcessedAtUtc"), Q("NextAttemptAtUtc"), Q("LockedUntilUtc"), Q("CreatedAtUtc"));

        await using var connection = await AdoNetStoreSupport.OpenIndependentConnectionAsync(_factory, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = AdoNetStoreSupport.CreateTableSql(_dialect, AdoNetStoreSupport.OutboxTable, columns);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (_dialect == DatabaseDialect.MySql)
        {
            await EnsureMySqlIndexAsync(connection, null, AdoNetStoreSupport.OutboxTable, "IX_LinKitOutbox_Pending", indexColumns, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            command.CommandText = AdoNetStoreSupport.CreateIndexSql(_dialect, "IX_LinKitOutbox_Pending", AdoNetStoreSupport.OutboxTable, indexColumns);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SaveAsync(IEnumerable<IOutboxMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var materialized = messages.ToArray();
        if (materialized.Length == 0)
            return;
        foreach (var message in materialized)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (message.Id == Guid.Empty)
                throw new ArgumentException("Outbox message ID must not be empty.", nameof(messages));
            ArgumentException.ThrowIfNullOrWhiteSpace(message.MessageType);
            ArgumentNullException.ThrowIfNull(message.Payload);
            if (message.RetryCount < 0)
                throw new ArgumentOutOfRangeException(nameof(messages), "Outbox retry count cannot be negative.");
        }

        await AdoNetStoreSupport.WithTransactionAsync(_factory, _unitOfWork, async (connection, transaction) =>
        {
            foreach (var message in materialized)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    $"INSERT INTO {AdoNetStoreSupport.Quote(_dialect, AdoNetStoreSupport.OutboxTable)} " +
                    $"({Q("Id")}, {Q("MessageType")}, {Q("Payload")}, {Q("Headers")}, {Q("CreatedAtUtc")}, {Q("ProcessedAtUtc")}, {Q("RetryCount")}, {Q("LastError")}, {Q("LockedUntilUtc")}, {Q("NextAttemptAtUtc")}) " +
                    $"VALUES ({P("id")}, {P("messageType")}, {P("payload")}, {P("headers")}, {P("createdAtUtc")}, {P("processedAtUtc")}, {P("retryCount")}, {P("lastError")}, {P("lockedUntilUtc")}, {P("nextAttemptAtUtc")})";
                Add(command, "id", message.Id.ToString("D"));
                Add(command, "messageType", message.MessageType);
                Add(command, "payload", message.Payload);
                Add(command, "headers", AdoNetStoreSupport.SerializeHeaders(message.Headers));
                Add(command, "createdAtUtc", AdoNetStoreSupport.NormalizeUtc(message.CreatedAtUtc), DbType.DateTime);
                Add(command, "processedAtUtc", message.ProcessedAtUtc.HasValue ? AdoNetStoreSupport.NormalizeUtc(message.ProcessedAtUtc.Value) : null, DbType.DateTime);
                Add(command, "retryCount", message.RetryCount);
                Add(command, "lastError", message.LastError, DbType.String);
                Add(command, "lockedUntilUtc", message is OutboxMessage concrete && concrete.LockedUntilUtc.HasValue ? AdoNetStoreSupport.NormalizeUtc(concrete.LockedUntilUtc.Value) : null, DbType.DateTime);
                Add(command, "nextAttemptAtUtc", message is OutboxMessage withSchedule && withSchedule.NextAttemptAtUtc.HasValue ? AdoNetStoreSupport.NormalizeUtc(withSchedule.NextAttemptAtUtc.Value) : null, DbType.DateTime);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task SaveMessageAsync(IOutboxMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SaveAsync(new[] { message }, cancellationToken);
    }

    public async Task<IReadOnlyList<IOutboxMessage>> GetPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        await using var session = await AdoNetStoreSupport.OpenAsync(_factory, _unitOfWork, cancellationToken).ConfigureAwait(false);
        await using var command = session.Connection.CreateCommand();
        command.Transaction = session.Transaction;
        command.CommandText = BuildPendingSelect(batchSize);
        Add(command, "maxRetries", _options.MaxRetries);
        var now = _time.GetUtcNow().UtcDateTime;
        Add(command, "nextAttemptNow", now, DbType.DateTime);
        Add(command, "leaseNow", now, DbType.DateTime);
        var messages = new List<IOutboxMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            messages.Add(ReadMessage(reader));
        return messages;
    }

    public async Task<IReadOnlyList<IOutboxMessage>> ClaimPendingAsync(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (lease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lease));

        var claimed = new List<IOutboxMessage>();
        await AdoNetStoreSupport.WithTransactionAsync(_factory, _unitOfWork, async (connection, transaction) =>
        {
            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = BuildPendingSelect(batchSize);
            Add(select, "maxRetries", _options.MaxRetries);
            var selectionTime = _time.GetUtcNow().UtcDateTime;
            Add(select, "nextAttemptNow", selectionTime, DbType.DateTime);
            Add(select, "leaseNow", selectionTime, DbType.DateTime);
            var candidates = new List<OutboxMessage>();
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
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
                    $"UPDATE {AdoNetStoreSupport.Quote(_dialect, AdoNetStoreSupport.OutboxTable)} " +
                    $"SET {Q("LockedUntilUtc")} = {P("lockedUntilUtc")} WHERE {Q("Id")} = {P("id")} " +
                    $"AND {Q("ProcessedAtUtc")} IS NULL AND {Q("RetryCount")} < {P("maxRetries")} " +
                    $"AND ({Q("NextAttemptAtUtc")} IS NULL OR {Q("NextAttemptAtUtc")} <= {P("nextAttemptNow")}) " +
                    $"AND ({Q("LockedUntilUtc")} IS NULL OR {Q("LockedUntilUtc")} <= {P("leaseNow")})";
                Add(update, "lockedUntilUtc", now + lease, DbType.DateTime);
                Add(update, "id", message.Id.ToString("D"));
                Add(update, "maxRetries", _options.MaxRetries);
                Add(update, "nextAttemptNow", now, DbType.DateTime);
                Add(update, "leaseNow", now, DbType.DateTime);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
                {
                    message.LockedUntilUtc = now + lease;
                    claimed.Add(message);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    public Task MarkProcessedAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(messageId, processed: true, error: null, nextAttemptAtUtc: null, cancellationToken);

    public Task MarkFailedAsync(Guid messageId, string error, CancellationToken cancellationToken = default) =>
        MarkFailedAsync(messageId, error, null, cancellationToken);

    public Task MarkFailedAsync(Guid messageId, string error, DateTime? nextAttemptAtUtc, CancellationToken cancellationToken = default) =>
        UpdateStatusAsync(messageId, processed: false, error, nextAttemptAtUtc, cancellationToken);

    private async Task UpdateStatusAsync(Guid messageId, bool processed, string? error, DateTime? nextAttemptAtUtc, CancellationToken cancellationToken)
    {
        await using var session = await AdoNetStoreSupport.OpenAsync(_factory, _unitOfWork, cancellationToken).ConfigureAwait(false);
        await using var command = session.Connection.CreateCommand();
        command.Transaction = session.Transaction;
        command.CommandText = processed
            ? $"UPDATE {AdoNetStoreSupport.Quote(_dialect, AdoNetStoreSupport.OutboxTable)} SET {Q("ProcessedAtUtc")} = {P("processedAtUtc")}, {Q("LastError")} = NULL, {Q("LockedUntilUtc")} = NULL WHERE {Q("Id")} = {P("id")}"
            : $"UPDATE {AdoNetStoreSupport.Quote(_dialect, AdoNetStoreSupport.OutboxTable)} SET {Q("RetryCount")} = {Q("RetryCount")} + 1, {Q("LastError")} = {P("lastError")}, {Q("NextAttemptAtUtc")} = {P("nextAttemptAtUtc")}, {Q("LockedUntilUtc")} = NULL WHERE {Q("Id")} = {P("id")}";
        if (processed)
            Add(command, "processedAtUtc", _time.GetUtcNow().UtcDateTime, DbType.DateTime);
        else
        {
            Add(command, "lastError", error, DbType.String);
            Add(command, "nextAttemptAtUtc", nextAttemptAtUtc.HasValue ? AdoNetStoreSupport.NormalizeUtc(nextAttemptAtUtc.Value) : null, DbType.DateTime);
        }
        Add(command, "id", messageId.ToString("D"));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
            throw new KeyNotFoundException($"Outbox message '{messageId}' does not exist.");
    }

    private string BuildPendingSelect(int batchSize)
    {
        var table = AdoNetStoreSupport.Quote(_dialect, AdoNetStoreSupport.OutboxTable);
        var cols = string.Join(", ", new[] { "Id", "MessageType", "Payload", "Headers", "CreatedAtUtc", "ProcessedAtUtc", "RetryCount", "LastError", "LockedUntilUtc", "NextAttemptAtUtc" }.Select(Q));
        var where = $"{Q("ProcessedAtUtc")} IS NULL AND {Q("RetryCount")} < {P("maxRetries")} " +
                    $"AND ({Q("NextAttemptAtUtc")} IS NULL OR {Q("NextAttemptAtUtc")} <= {P("nextAttemptNow")}) " +
                    $"AND ({Q("LockedUntilUtc")} IS NULL OR {Q("LockedUntilUtc")} <= {P("leaseNow")})";
        var order = $"ORDER BY {Q("CreatedAtUtc")} ASC";
        return _dialect switch
        {
            DatabaseDialect.SqlServer => $"SELECT TOP ({batchSize}) {cols} FROM {table} WHERE {where} {order}",
            DatabaseDialect.Oracle => $"SELECT {cols} FROM {table} WHERE {where} {order} FETCH FIRST {batchSize} ROWS ONLY",
            _ => $"SELECT {cols} FROM {table} WHERE {where} {order} LIMIT {batchSize}"
        };
    }

    private OutboxMessage ReadMessage(DbDataReader reader) => new()
    {
        Id = Guid.Parse(AdoNetStoreSupport.ReadNullableString(reader, "Id")!),
        MessageType = AdoNetStoreSupport.ReadNullableString(reader, "MessageType")!,
        Payload = AdoNetStoreSupport.ReadNullableString(reader, "Payload")!,
        Headers = AdoNetStoreSupport.DeserializeHeaders(AdoNetStoreSupport.ReadNullableString(reader, "Headers")),
        CreatedAtUtc = AdoNetStoreSupport.ReadUtcDateTime(reader, "CreatedAtUtc"),
        ProcessedAtUtc = AdoNetStoreSupport.ReadNullableUtcDateTime(reader, "ProcessedAtUtc"),
        RetryCount = Convert.ToInt32(reader.GetValue(reader.GetOrdinal("RetryCount")), System.Globalization.CultureInfo.InvariantCulture),
        LastError = AdoNetStoreSupport.ReadNullableString(reader, "LastError"),
        LockedUntilUtc = AdoNetStoreSupport.ReadNullableUtcDateTime(reader, "LockedUntilUtc"),
        NextAttemptAtUtc = AdoNetStoreSupport.ReadNullableUtcDateTime(reader, "NextAttemptAtUtc")
    };

    private async Task EnsureMySqlIndexAsync(DbConnection connection, DbTransaction? transaction, string tableName, string indexName, string columns, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = @tableName AND index_name = @indexName";
        Add(check, "tableName", tableName);
        Add(check, "indexName", indexName);
        if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            await using var create = connection.CreateCommand();
            create.Transaction = transaction;
            create.CommandText = AdoNetStoreSupport.CreateIndexSql(_dialect, indexName, tableName, columns);
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private string Q(string name) => AdoNetStoreSupport.Quote(_dialect, name);
    private string P(string name) => AdoNetStoreSupport.ParameterPrefix(_dialect) + name;

    private void Add(DbCommand command, string name, object? value, DbType? dbType = null) =>
        AdoNetStoreSupport.AddParameter(command, _dialect, name, value, dbType);
}
