using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LinKit.Core.Messaging;
using LinKit.Data;

namespace LinKit.Outbox.Data;

internal interface IAdoNetSchemaStore
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken = default);
}

internal sealed class AdoNetSession : IAsyncDisposable
{
    private readonly bool _ownsConnection;

    public DbConnection Connection { get; }
    public DbTransaction? Transaction { get; }

    public AdoNetSession(DbConnection connection, DbTransaction? transaction, bool ownsConnection)
    {
        Connection = connection;
        Transaction = transaction;
        _ownsConnection = ownsConnection;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsConnection)
            await Connection.DisposeAsync().ConfigureAwait(false);
    }
}

internal static class AdoNetStoreSupport
{
    public const string OutboxTable = "LinKitOutboxMessages";
    public const string InboxTable = "LinKitInboxMessages";

    public static async ValueTask<AdoNetSession> OpenAsync(
        IDbConnectionFactory factory,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        if (unitOfWork.CurrentTransaction is { } transaction)
        {
            var transactionConnection = transaction.Connection
                ?? throw new InvalidOperationException("The active unit-of-work transaction has no connection.");
            return new AdoNetSession(transactionConnection, transaction, false);
        }

        return new AdoNetSession(
            await OpenIndependentConnectionAsync(factory, cancellationToken).ConfigureAwait(false),
            null,
            true);
    }

    public static async ValueTask<DbConnection> OpenIndependentConnectionAsync(
        IDbConnectionFactory factory,
        CancellationToken cancellationToken)
    {
        var connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task WithTransactionAsync(
        IDbConnectionFactory factory,
        IUnitOfWork unitOfWork,
        Func<DbConnection, DbTransaction, Task> action,
        CancellationToken cancellationToken)
    {
        if (unitOfWork.CurrentTransaction is { } ambient)
        {
            var connection = ambient.Connection
                ?? throw new InvalidOperationException("The active unit-of-work transaction has no connection.");
            await action(connection, ambient).ConfigureAwait(false);
            return;
        }

        await using var session = await OpenAsync(factory, unitOfWork, cancellationToken).ConfigureAwait(false);
        await using var transaction = await session.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action(session.Connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception operationException)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException("The ADO.NET transaction failed and rollback also failed.", operationException, rollbackException);
            }
            throw;
        }
    }

    public static string Quote(DatabaseDialect dialect, string identifier) => dialect switch
    {
        DatabaseDialect.SqlServer => $"[{identifier}]",
        DatabaseDialect.MySql => $"`{identifier}`",
        _ => $"\"{identifier}\""
    };

    public static string ParameterPrefix(DatabaseDialect dialect) =>
        dialect == DatabaseDialect.Oracle ? ":" : "@";

    public static DbParameter AddParameter(
        DbCommand command,
        DatabaseDialect dialect,
        string name,
        object? value,
        DbType? dbType = null)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = ParameterPrefix(dialect) + name;
        if (dbType.HasValue)
            parameter.DbType = dbType.Value;
        else if (value is string || value is null)
            parameter.DbType = DbType.String;
        else if (value is DateTime)
            parameter.DbType = DbType.DateTime;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }

    public static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public static string? ReadNullableString(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public static DateTime ReadUtcDateTime(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
            _ => DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
        };
    }

    public static DateTime? ReadNullableUtcDateTime(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
            return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dateTime => DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime,
            _ => DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
        };
    }

    public static string SerializeHeaders(MessageHeaders? headers)
    {
        if (headers is null || headers.Count == 0)
            return "{}";

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in headers)
            {
                writer.WritePropertyName(key);
                switch (value)
                {
                    case null: writer.WriteNullValue(); break;
                    case string text: writer.WriteStringValue(text); break;
                    case bool boolean: writer.WriteBooleanValue(boolean); break;
                    case byte or sbyte or short or ushort or int or uint or long:
                        writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                        break;
                    case ulong unsigned: writer.WriteNumberValue(unsigned); break;
                    case float or double or decimal:
                        writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                        break;
                    case byte[] bytes: writer.WriteBase64StringValue(bytes); break;
                    case JsonElement element: element.WriteTo(writer); break;
                    default: writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static MessageHeaders DeserializeHeaders(string? json)
    {
        var headers = new MessageHeaders();
        if (string.IsNullOrWhiteSpace(json))
            return headers;

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Persisted outbox headers must be a JSON object.");

        foreach (var property in document.RootElement.EnumerateObject())
        {
            object? value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when property.Value.TryGetInt64(out var number) => number,
                JsonValueKind.Null => null,
                _ => property.Value.Clone()
            };
            if (value is not null)
                headers[property.Name] = value;
        }
        return headers;
    }

    public static string CreateTableSql(DatabaseDialect dialect, string tableName, string columns)
    {
        var quotedTable = Quote(dialect, tableName);
        var body = dialect switch
        {
            DatabaseDialect.SqlServer =>
                $"IF OBJECT_ID(N'{tableName}', N'U') IS NULL CREATE TABLE {quotedTable} ({columns})",
            DatabaseDialect.Oracle =>
                $"BEGIN EXECUTE IMMEDIATE 'CREATE TABLE {quotedTable} ({columns})'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -955 THEN RAISE; END IF; END;",
            _ => $"CREATE TABLE IF NOT EXISTS {quotedTable} ({columns})"
        };
        return body;
    }

    public static string CreateIndexSql(DatabaseDialect dialect, string indexName, string tableName, string columns)
    {
        var quotedIndex = Quote(dialect, indexName);
        var quotedTable = Quote(dialect, tableName);
        var body = dialect switch
        {
            DatabaseDialect.SqlServer =>
                $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{indexName}') CREATE INDEX {quotedIndex} ON {quotedTable} ({columns})",
            DatabaseDialect.Oracle =>
                $"BEGIN EXECUTE IMMEDIATE 'CREATE INDEX {quotedIndex} ON {quotedTable} ({columns})'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -955 THEN RAISE; END IF; END;",
            DatabaseDialect.MySql =>
                $"CREATE INDEX {quotedIndex} ON {quotedTable} ({columns})",
            _ => $"CREATE INDEX IF NOT EXISTS {quotedIndex} ON {quotedTable} ({columns})"
        };
        return body;
    }

    public static string GetStringType(DatabaseDialect dialect, int length) => dialect switch
    {
        DatabaseDialect.SqlServer => $"NVARCHAR({length})",
        DatabaseDialect.MySql => $"VARCHAR({length})",
        DatabaseDialect.Oracle => $"VARCHAR2({length})",
        DatabaseDialect.PostgreSql => "TEXT",
        _ => "TEXT"
    };

    public static string GetTextType(DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.SqlServer => "NVARCHAR(MAX)",
        DatabaseDialect.MySql => "LONGTEXT",
        DatabaseDialect.Oracle => "CLOB",
        _ => "TEXT"
    };

    public static string GetDateTimeType(DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.SqlServer => "DATETIME2",
        DatabaseDialect.MySql => "DATETIME(6)",
        DatabaseDialect.Oracle => "TIMESTAMP",
        DatabaseDialect.PostgreSql => "TIMESTAMP WITH TIME ZONE",
        _ => "TEXT"
    };
}
