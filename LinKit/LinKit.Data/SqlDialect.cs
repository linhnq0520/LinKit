using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;

namespace LinKit.Data;

public enum DatabaseKind
{
    Sqlite = 0,
    PostgreSql = 1,
    SqlServer = 2,
    MySql = 3,
    Oracle = 4
}

public sealed class SqlDialect
{
    public DatabaseKind Kind { get; }

    public SqlDialect(DatabaseKind kind)
    {
        Kind = Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
    }

    public SqlDialect(DatabaseDialect dialect) : this((DatabaseKind)dialect) { }

    public bool TransactionalDdl => Kind is DatabaseKind.Sqlite or DatabaseKind.PostgreSql or DatabaseKind.SqlServer;
    public int MaxParameters => Kind switch { DatabaseKind.SqlServer => 2100, DatabaseKind.Sqlite => 999, DatabaseKind.Oracle => 65535, _ => 32000 };
    public int MaxInItems => Kind == DatabaseKind.Oracle ? 1000 : int.MaxValue;
    public string Parameter(int index) => (Kind == DatabaseKind.Oracle ? ":p" : "@p") + index;

    public string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return Kind switch
        {
            DatabaseKind.SqlServer => "[" + identifier.Replace("]", "]]") + "]",
            DatabaseKind.MySql => "`" + identifier.Replace("`", "``") + "`",
            _ => "\"" + identifier.Replace("\"", "\"\"") + "\""
        };
    }

    public string Table(TableModel table) => table.Schema is null
        ? Quote(table.Name) : Quote(table.Schema) + "." + Quote(table.Name);

    public string True => Kind == DatabaseKind.PostgreSql ? "TRUE" : "1";
    public string False => Kind == DatabaseKind.PostgreSql ? "FALSE" : "0";

    public string Paginate(string sql, string? order, int? skip, int? take, Func<int, string>? parameter = null)
    {
        if (skip < 0 || take < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take == 0) return $"SELECT * FROM ({sql}) {Quote("empty_result")} WHERE 1 = 0";
        string Bind(int value) => parameter?.Invoke(value) ?? value.ToString(CultureInfo.InvariantCulture);
        var ordered = order is null ? sql : sql + " ORDER BY " + order;
        if (skip is null && take is null) return ordered;
        return Kind switch
        {
            DatabaseKind.SqlServer => ordered + (order is null ? " ORDER BY (SELECT NULL)" : "") + $" OFFSET {Bind(skip ?? 0)} ROWS" + (take is null ? "" : $" FETCH NEXT {Bind(take.Value)} ROWS ONLY"),
            DatabaseKind.Oracle => ordered + $" OFFSET {Bind(skip ?? 0)} ROWS" + (take is null ? "" : $" FETCH NEXT {Bind(take.Value)} ROWS ONLY"),
            DatabaseKind.PostgreSql => ordered + (take is null ? "" : $" LIMIT {Bind(take.Value)}") + (skip is null ? "" : $" OFFSET {Bind(skip.Value)}"),
            DatabaseKind.MySql => ordered + $" LIMIT {(take.HasValue ? Bind(take.Value) : "18446744073709551615")} OFFSET {Bind(skip ?? 0)}",
            _ => ordered + $" LIMIT {(take.HasValue ? Bind(take.Value) : "-1")} OFFSET {Bind(skip ?? 0)}"
        };
    }

    public string StoreType(ColumnModel column)
    {
        var length = column.Length > 0 ? column.Length : 255;
        var precision = column.Precision > 0 ? column.Precision : 18;
        return column.Type switch
        {
            DbType.Int16 => Kind == DatabaseKind.Oracle ? "NUMBER(5)" : "SMALLINT",
            DbType.Int32 => Kind == DatabaseKind.Oracle ? "NUMBER(10)" : "INTEGER",
            DbType.Int64 => Kind == DatabaseKind.Oracle ? "NUMBER(19)" : Kind == DatabaseKind.Sqlite ? "INTEGER" : "BIGINT",
            DbType.Byte => Kind == DatabaseKind.Oracle ? "NUMBER(3)" : "SMALLINT",
            DbType.Boolean => Kind switch { DatabaseKind.PostgreSql => "BOOLEAN", DatabaseKind.SqlServer => "BIT", DatabaseKind.Oracle => "NUMBER(1)", _ => "INTEGER" },
            DbType.Decimal => Kind == DatabaseKind.Oracle ? $"NUMBER({precision}, {column.Scale})" : $"DECIMAL({precision}, {column.Scale})",
            DbType.Double => Kind switch { DatabaseKind.PostgreSql => "DOUBLE PRECISION", DatabaseKind.SqlServer => "FLOAT", DatabaseKind.Oracle => "BINARY_DOUBLE", _ => "DOUBLE" },
            DbType.Single => Kind == DatabaseKind.Oracle ? "BINARY_FLOAT" : "REAL",
            DbType.Guid => Kind switch { DatabaseKind.PostgreSql => "UUID", DatabaseKind.SqlServer => "UNIQUEIDENTIFIER", _ => "CHAR(36)" },
            DbType.Date => Kind == DatabaseKind.Sqlite ? "TEXT" : "DATE",
            DbType.Time => Kind is DatabaseKind.Sqlite or DatabaseKind.Oracle ? "VARCHAR(32)" : "TIME",
            DbType.DateTime2 => Kind switch { DatabaseKind.SqlServer => "DATETIME2", DatabaseKind.MySql => "DATETIME(6)", DatabaseKind.Sqlite => "TEXT", _ => "TIMESTAMP" },
            DbType.DateTimeOffset => Kind switch { DatabaseKind.PostgreSql or DatabaseKind.Oracle => "TIMESTAMP WITH TIME ZONE", DatabaseKind.SqlServer => "DATETIMEOFFSET", _ => "VARCHAR(40)" },
            DbType.Binary => Kind switch { DatabaseKind.Sqlite => "BLOB", DatabaseKind.PostgreSql => "BYTEA", DatabaseKind.SqlServer => "VARBINARY(MAX)", DatabaseKind.MySql => "LONGBLOB", _ => "BLOB" },
            DbType.String => Kind switch { DatabaseKind.SqlServer => $"NVARCHAR({length})", DatabaseKind.Oracle => $"NVARCHAR2({length})", _ => $"VARCHAR({length})" },
            _ => throw new NotSupportedException($"No store mapping for {column.Type}.")
        };
    }

    public string ColumnDefinition(ColumnModel column)
    {
        var sql = Quote(column.Name) + " " + StoreType(column);
        if (column.Generated)
        {
            sql += Kind switch
            {
                DatabaseKind.Sqlite => " PRIMARY KEY AUTOINCREMENT",
                DatabaseKind.SqlServer => " IDENTITY(1,1)",
                DatabaseKind.MySql => " AUTO_INCREMENT",
                _ => " GENERATED BY DEFAULT AS IDENTITY"
            };
        }
        if (column.PrimaryKey && !(column.Generated && Kind == DatabaseKind.Sqlite)) sql += " PRIMARY KEY";
        if (!column.Nullable && !column.PrimaryKey) sql += " NOT NULL";
        return sql;
    }

    public string CreateTable(TableModel table) => $"CREATE TABLE {Table(table)} ({string.Join(", ", table.Columns.Select(ColumnDefinition))})";

    public DbParameter Bind(DbCommand command, int index, object? value, ColumnModel? column = null)
    {
        if (column is { Type: DbType.String, Length: > 0 } && value is string text && text.Length > column.Length)
            throw new ArgumentException($"Value exceeds the declared length of column '{column.Name}'.");
        var parameter = command.CreateParameter();
        parameter.ParameterName = Parameter(index);
        if (column is not null)
        {
            parameter.DbType = column.Type switch
            {
                DbType.Guid when Kind is DatabaseKind.Sqlite or DatabaseKind.MySql or DatabaseKind.Oracle => DbType.String,
                DbType.Boolean when Kind == DatabaseKind.Oracle => DbType.Int32,
                DbType.Date when Kind == DatabaseKind.Sqlite => DbType.String,
                DbType.Time when Kind is DatabaseKind.Sqlite or DatabaseKind.Oracle => DbType.String,
                DbType.DateTimeOffset when Kind is DatabaseKind.Sqlite or DatabaseKind.MySql => DbType.String,
                _ => column.Type
            };
            if (column.Length > 0) parameter.Size = column.Length;
            if (column.Precision > 0) { parameter.Precision = column.Precision; parameter.Scale = column.Scale; }
        }
        if (value is Guid guid && Kind is DatabaseKind.Sqlite or DatabaseKind.MySql or DatabaseKind.Oracle)
        {
            value = guid.ToString();
            parameter.DbType = DbType.String;
        }
        if (value is bool boolean && Kind == DatabaseKind.Oracle)
        {
            value = boolean ? 1 : 0;
            parameter.DbType = DbType.Int32;
        }
        if (value is DateOnly date)
        {
            if (Kind == DatabaseKind.Sqlite)
            {
                value = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                parameter.DbType = DbType.String;
            }
            else
            {
                value = date.ToDateTime(TimeOnly.MinValue);
                parameter.DbType = DbType.Date;
            }
        }
        if (value is TimeOnly time)
        {
            if (Kind is DatabaseKind.Sqlite or DatabaseKind.Oracle)
            {
                value = time.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                parameter.DbType = DbType.String;
            }
            else
            {
                value = time.ToTimeSpan();
                parameter.DbType = DbType.Time;
            }
        }
        if (value is Enum enumValue)
        {
            value = Convert.ToInt64(enumValue, CultureInfo.InvariantCulture);
        }
        if (value is DateTimeOffset offset && Kind is DatabaseKind.Sqlite or DatabaseKind.MySql)
        {
            value = offset.ToUniversalTime().ToString("O");
            parameter.DbType = DbType.String;
        }
        if (value is DateTimeOffset postgresOffset && Kind == DatabaseKind.PostgreSql)
        {
            value = postgresOffset.ToUniversalTime();
        }
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }
}
