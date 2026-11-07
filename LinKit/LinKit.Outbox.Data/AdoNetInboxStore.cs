using System.Data;
using System.Data.Common;
using LinKit.Data;
using LinKit.Outbox;

namespace LinKit.Outbox.Data;

public sealed class AdoNetInboxStore : IInboxStore, IAdoNetSchemaStore
{
    private readonly IDbConnectionFactory _factory;
    private readonly IUnitOfWork _unitOfWork;
    private readonly DatabaseDialect _dialect;
    private readonly TimeProvider _time;

    public AdoNetInboxStore(IDbConnectionFactory factory, IUnitOfWork unitOfWork, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _dialect = factory.Dialect;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        var dateType = AdoNetStoreSupport.GetDateTimeType(_dialect);
        var keyType = AdoNetStoreSupport.GetStringType(_dialect, 512);
        var columns = string.Join(", ",
            $"{Q("MessageId")} {keyType} NOT NULL",
            $"{Q("ConsumerName")} {AdoNetStoreSupport.GetStringType(_dialect, 256)} NOT NULL",
            $"{Q("ProcessedAtUtc")} {dateType} NOT NULL",
            $"{Q("ExpiresAtUtc")} {dateType} NULL",
            $"PRIMARY KEY ({Q("MessageId")}, {Q("ConsumerName")})");
        await using var connection = await AdoNetStoreSupport.OpenIndependentConnectionAsync(_factory, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = AdoNetStoreSupport.CreateTableSql(_dialect, AdoNetStoreSupport.InboxTable, columns);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasBeenProcessedAsync(string messageId, string consumerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        await using var session = await AdoNetStoreSupport.OpenAsync(_factory, _unitOfWork, cancellationToken).ConfigureAwait(false);
        await using var command = session.Connection.CreateCommand();
        command.Transaction = session.Transaction;
        var table = AdoNetStoreSupport.Quote(_dialect, AdoNetStoreSupport.InboxTable);
        command.CommandText = $"SELECT 1 FROM {table} WHERE {Q("MessageId")} = {P("messageId")} AND {Q("ConsumerName")} = {P("consumerName")} AND ({Q("ExpiresAtUtc")} IS NULL OR {Q("ExpiresAtUtc")} > {P("now")})";
        Add(command, "messageId", messageId);
        Add(command, "consumerName", consumerName);
        Add(command, "now", _time.GetUtcNow().UtcDateTime, DbType.DateTime);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task MarkProcessedAsync(string messageId, string consumerName, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        if (expiry < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiry));

        var now = _time.GetUtcNow().UtcDateTime;
        if (expiry.HasValue && expiry.Value > DateTime.MaxValue - now)
            throw new ArgumentOutOfRangeException(nameof(expiry), "Inbox expiry exceeds the supported date range.");
        DateTime? expiresAt = expiry.HasValue ? now + expiry.Value : null;
        await AdoNetStoreSupport.WithTransactionAsync(_factory, _unitOfWork, async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = BuildUpsertSql();
            Add(command, "messageId", messageId);
            Add(command, "consumerName", consumerName);
            Add(command, "processedAtUtc", now, DbType.DateTime);
            Add(command, "expiresAtUtc", expiresAt, DbType.DateTime);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryMarkProcessedAsync(string messageId, string consumerName, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        if (expiry < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiry));

        var now = _time.GetUtcNow().UtcDateTime;
        if (expiry.HasValue && expiry.Value > DateTime.MaxValue - now)
            throw new ArgumentOutOfRangeException(nameof(expiry), "Inbox expiry exceeds the supported date range.");
        DateTime? expiresAt = expiry.HasValue ? now + expiry.Value : null;
        var claimed = false;
        await AdoNetStoreSupport.WithTransactionAsync(_factory, _unitOfWork, async (connection, transaction) =>
        {
            var table = AdoNetStoreSupport.Quote(_dialect, AdoNetStoreSupport.InboxTable);
            if (_dialect == DatabaseDialect.MySql)
            {
                // MySQL affected-row counts for ON DUPLICATE KEY UPDATE depend on connection flags,
                // so purge an expired claim first and detect the live duplicate via error 1062.
                await using var purge = connection.CreateCommand();
                purge.Transaction = transaction;
                purge.CommandText = $"DELETE FROM {table} WHERE {Q("MessageId")} = {P("messageId")} AND {Q("ConsumerName")} = {P("consumerName")} AND {Q("ExpiresAtUtc")} IS NOT NULL AND {Q("ExpiresAtUtc")} <= {P("now")}";
                Add(purge, "messageId", messageId);
                Add(purge, "consumerName", consumerName);
                Add(purge, "now", now, DbType.DateTime);
                await purge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = BuildClaimSql();
            Add(command, "messageId", messageId);
            Add(command, "consumerName", consumerName);
            Add(command, "processedAtUtc", now, DbType.DateTime);
            Add(command, "expiresAtUtc", expiresAt, DbType.DateTime);
            if (_dialect != DatabaseDialect.MySql)
                Add(command, "now", now, DbType.DateTime);
            try
            {
                claimed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
            }
            catch (DbException ex) when (IsUniqueViolation(ex))
            {
                claimed = false;
            }
        }, cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    private string BuildClaimSql()
    {
        var table = AdoNetStoreSupport.Quote(_dialect, AdoNetStoreSupport.InboxTable);
        var columns = $"{Q("MessageId")}, {Q("ConsumerName")}, {Q("ProcessedAtUtc")}, {Q("ExpiresAtUtc")}";
        var values = $"{P("messageId")}, {P("consumerName")}, {P("processedAtUtc")}, {P("expiresAtUtc")}";
        // Insert-only claim: an existing row is overwritten only when its claim expired,
        // matching HasBeenProcessedAsync semantics. Affected rows == 0 means "already claimed".
        return _dialect switch
        {
            DatabaseDialect.SqlServer =>
                $"MERGE {table} WITH (HOLDLOCK) AS target USING (SELECT {P("messageId")} AS {Q("MessageId")}, {P("consumerName")} AS {Q("ConsumerName")}, {P("processedAtUtc")} AS {Q("ProcessedAtUtc")}, {P("expiresAtUtc")} AS {Q("ExpiresAtUtc")}) AS source " +
                $"ON target.{Q("MessageId")} = source.{Q("MessageId")} AND target.{Q("ConsumerName")} = source.{Q("ConsumerName")} " +
                $"WHEN MATCHED AND target.{Q("ExpiresAtUtc")} IS NOT NULL AND target.{Q("ExpiresAtUtc")} <= {P("now")} THEN UPDATE SET {Q("ProcessedAtUtc")} = source.{Q("ProcessedAtUtc")}, {Q("ExpiresAtUtc")} = source.{Q("ExpiresAtUtc")} " +
                $"WHEN NOT MATCHED THEN INSERT ({columns}) VALUES (source.{Q("MessageId")}, source.{Q("ConsumerName")}, source.{Q("ProcessedAtUtc")}, source.{Q("ExpiresAtUtc")});",
            DatabaseDialect.Oracle =>
                $"MERGE INTO {table} target USING (SELECT {P("messageId")} AS {Q("MessageId")}, {P("consumerName")} AS {Q("ConsumerName")}, {P("processedAtUtc")} AS {Q("ProcessedAtUtc")}, {P("expiresAtUtc")} AS {Q("ExpiresAtUtc")} FROM dual) source " +
                $"ON (target.{Q("MessageId")} = source.{Q("MessageId")} AND target.{Q("ConsumerName")} = source.{Q("ConsumerName")}) " +
                $"WHEN MATCHED THEN UPDATE SET target.{Q("ProcessedAtUtc")} = source.{Q("ProcessedAtUtc")}, target.{Q("ExpiresAtUtc")} = source.{Q("ExpiresAtUtc")} WHERE target.{Q("ExpiresAtUtc")} IS NOT NULL AND target.{Q("ExpiresAtUtc")} <= {P("now")} " +
                $"WHEN NOT MATCHED THEN INSERT ({columns}) VALUES (source.{Q("MessageId")}, source.{Q("ConsumerName")}, source.{Q("ProcessedAtUtc")}, source.{Q("ExpiresAtUtc")})",
            DatabaseDialect.MySql =>
                $"INSERT INTO {table} ({columns}) VALUES ({values})",
            _ =>
                $"INSERT INTO {table} ({columns}) VALUES ({values}) ON CONFLICT ({Q("MessageId")}, {Q("ConsumerName")}) DO UPDATE SET {Q("ProcessedAtUtc")} = excluded.{Q("ProcessedAtUtc")}, {Q("ExpiresAtUtc")} = excluded.{Q("ExpiresAtUtc")} WHERE {table}.{Q("ExpiresAtUtc")} IS NOT NULL AND {table}.{Q("ExpiresAtUtc")} <= {P("now")}"
        };
    }

    private bool IsUniqueViolation(DbException exception) => _dialect switch
    {
        // SQLite reports the generic SQLITE_CONSTRAINT (19) / extended CONSTRAINT_UNIQUE (2067);
        // all inserted values are validated non-null, so a constraint failure here is the primary key.
        DatabaseDialect.Sqlite => exception.ErrorCode is 19 or 2067,
        DatabaseDialect.PostgreSql => exception.SqlState == "23505",
        DatabaseDialect.SqlServer => exception.ErrorCode is 2601 or 2627,
        DatabaseDialect.MySql => exception.ErrorCode == 1062,
        DatabaseDialect.Oracle => exception.ErrorCode == 1, // ORA-00001
        _ => false
    };

    private string BuildUpsertSql()
    {
        var table = AdoNetStoreSupport.Quote(_dialect, AdoNetStoreSupport.InboxTable);
        var columns = $"{Q("MessageId")}, {Q("ConsumerName")}, {Q("ProcessedAtUtc")}, {Q("ExpiresAtUtc")}";
        var values = $"{P("messageId")}, {P("consumerName")}, {P("processedAtUtc")}, {P("expiresAtUtc")}";
        return _dialect switch
        {
            DatabaseDialect.SqlServer =>
                $"MERGE {table} WITH (HOLDLOCK) AS target USING (SELECT {P("messageId")} AS {Q("MessageId")}, {P("consumerName")} AS {Q("ConsumerName")}, {P("processedAtUtc")} AS {Q("ProcessedAtUtc")}, {P("expiresAtUtc")} AS {Q("ExpiresAtUtc")}) AS source " +
                $"ON target.{Q("MessageId")} = source.{Q("MessageId")} AND target.{Q("ConsumerName")} = source.{Q("ConsumerName")} " +
                $"WHEN MATCHED THEN UPDATE SET {Q("ProcessedAtUtc")} = source.{Q("ProcessedAtUtc")}, {Q("ExpiresAtUtc")} = source.{Q("ExpiresAtUtc")} " +
                $"WHEN NOT MATCHED THEN INSERT ({columns}) VALUES (source.{Q("MessageId")}, source.{Q("ConsumerName")}, source.{Q("ProcessedAtUtc")}, source.{Q("ExpiresAtUtc")});",
            DatabaseDialect.Oracle =>
                $"MERGE INTO {table} target USING (SELECT {P("messageId")} AS {Q("MessageId")}, {P("consumerName")} AS {Q("ConsumerName")}, {P("processedAtUtc")} AS {Q("ProcessedAtUtc")}, {P("expiresAtUtc")} AS {Q("ExpiresAtUtc")} FROM dual) source " +
                $"ON (target.{Q("MessageId")} = source.{Q("MessageId")} AND target.{Q("ConsumerName")} = source.{Q("ConsumerName")}) " +
                $"WHEN MATCHED THEN UPDATE SET target.{Q("ProcessedAtUtc")} = source.{Q("ProcessedAtUtc")}, target.{Q("ExpiresAtUtc")} = source.{Q("ExpiresAtUtc")} " +
                $"WHEN NOT MATCHED THEN INSERT ({columns}) VALUES (source.{Q("MessageId")}, source.{Q("ConsumerName")}, source.{Q("ProcessedAtUtc")}, source.{Q("ExpiresAtUtc")})",
            DatabaseDialect.MySql =>
                $"INSERT INTO {table} ({columns}) VALUES ({values}) ON DUPLICATE KEY UPDATE {Q("ProcessedAtUtc")} = VALUES({Q("ProcessedAtUtc")}), {Q("ExpiresAtUtc")} = VALUES({Q("ExpiresAtUtc")})",
            _ =>
                $"INSERT INTO {table} ({columns}) VALUES ({values}) ON CONFLICT ({Q("MessageId")}, {Q("ConsumerName")}) DO UPDATE SET {Q("ProcessedAtUtc")} = excluded.{Q("ProcessedAtUtc")}, {Q("ExpiresAtUtc")} = excluded.{Q("ExpiresAtUtc")}"
        };
    }

    private string Q(string name) => AdoNetStoreSupport.Quote(_dialect, name);
    private string P(string name) => AdoNetStoreSupport.ParameterPrefix(_dialect) + name;

    private void Add(DbCommand command, string name, object? value, DbType? dbType = null) =>
        AdoNetStoreSupport.AddParameter(command, _dialect, name, value, dbType);
}
