using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;

namespace LinKit.Data;

/// <summary>Database engine the session renders SQL for.</summary>
public enum DatabaseKind
{
    /// <summary>SQLite, which also backs the in-memory test databases.</summary>
    Sqlite = 0,

    /// <summary>PostgreSQL.</summary>
    PostgreSql = 1,

    /// <summary>Microsoft SQL Server.</summary>
    SqlServer = 2,

    /// <summary>MySQL and compatible servers such as MariaDB.</summary>
    MySql = 3,

    /// <summary>Oracle Database.</summary>
    Oracle = 4,
}

/// <summary>
/// Renders the provider-specific parts of the SQL that <see cref="DataSession"/> executes: quoting, parameter
/// naming, pagination, store types and parameter binding.
/// </summary>
public sealed class SqlDialect
{
    /// <summary>Database engine this instance renders for.</summary>
    public DatabaseKind Kind { get; }

    /// <summary>Creates a dialect for the given engine.</summary>
    /// <param name="kind">Database engine.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined value.</exception>
    public SqlDialect(DatabaseKind kind)
    {
        Kind = Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
    }

    /// <summary>Creates a dialect from the public <see cref="DatabaseDialect"/> enumeration.</summary>
    /// <param name="dialect">Database dialect to map.</param>
    public SqlDialect(DatabaseDialect dialect)
        : this((DatabaseKind)dialect) { }

    /// <summary>Whether the engine supports transactional DDL, so schema changes can be rolled back.</summary>
    public bool TransactionalDdl =>
        Kind is DatabaseKind.Sqlite or DatabaseKind.PostgreSql or DatabaseKind.SqlServer;

    /// <summary>Maximum number of parameters a single command may carry on this engine.</summary>
    public int MaxParameters =>
        Kind switch
        {
            DatabaseKind.SqlServer => 2100,
            DatabaseKind.Sqlite => 999,
            DatabaseKind.Oracle => 65535,
            _ => 32000,
        };

    /// <summary>Maximum number of values an <c>IN (...)</c> list may carry; unlimited except on Oracle.</summary>
    public int MaxInItems => Kind == DatabaseKind.Oracle ? 1000 : int.MaxValue;

    /// <summary>Hard ceiling on the number of <c>VALUES</c> rows a provider accepts in one statement.</summary>
    public const int MaxRowsPerInsertStatement = 1000;

    /// <summary>
    /// True when <c>INSERT INTO t (a, b) VALUES (...), (...)</c> is valid. Oracle only supports multi-row
    /// inserts through <c>INSERT ALL</c>, which cannot return generated identities, so it stays a per-row path.
    /// </summary>
    public bool SupportsMultiRowInsert => Kind is not DatabaseKind.Oracle;

    /// <summary>
    /// True when a generated key can be read back for a multi-row insert. SQL Server uses
    /// <c>OUTPUT INSERTED</c>, PostgreSQL and SQLite use <c>RETURNING</c>; MySQL needs one statement per row.
    /// </summary>
    public bool SupportsBatchInsertIdentity =>
        Kind is DatabaseKind.Sqlite or DatabaseKind.PostgreSql or DatabaseKind.SqlServer;

    /// <summary>True when the provider accepts <c>UPDATE ... FROM (VALUES ...) AS v(...)</c>.</summary>
    public bool SupportsUpdateFromValues =>
        Kind is DatabaseKind.PostgreSql or DatabaseKind.SqlServer;

    /// <summary>True when the provider accepts <c>UPDATE ... INNER JOIN (SELECT ... UNION ALL ...) v</c>.</summary>
    public bool SupportsUpdateJoinValues => Kind == DatabaseKind.MySql;

    /// <summary>Renders the placeholder of the argument at <paramref name="index"/>.</summary>
    /// <param name="index">Zero-based argument position.</param>
    /// <returns>The placeholder text, for example <c>@p0</c>.</returns>
    public string Parameter(int index) => (Kind == DatabaseKind.Oracle ? ":p" : "@p") + index;

    /// <summary>Quotes an identifier with the delimiters of this engine and escapes embedded delimiters.</summary>
    /// <param name="identifier">Raw identifier.</param>
    /// <returns>The quoted identifier.</returns>
    /// <exception cref="ArgumentException"><paramref name="identifier"/> is <see langword="null"/>, empty or blank.</exception>
    public string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return Kind switch
        {
            DatabaseKind.SqlServer => "[" + identifier.Replace("]", "]]") + "]",
            DatabaseKind.MySql => "`" + identifier.Replace("`", "``") + "`",
            _ => "\"" + identifier.Replace("\"", "\"\"") + "\"",
        };
    }

    /// <summary>Renders the qualified table name, including the schema when the model declares one.</summary>
    /// <param name="table">Table model to render.</param>
    /// <returns>The quoted, optionally schema-qualified table name.</returns>
    public string Table(TableModel table) =>
        table.Schema is null ? Quote(table.Name) : Quote(table.Schema) + "." + Quote(table.Name);

    /// <summary>Literal that represents a true boolean on this engine.</summary>
    public string True => Kind == DatabaseKind.PostgreSql ? "TRUE" : "1";

    /// <summary>Literal that represents a false boolean on this engine.</summary>
    public string False => Kind == DatabaseKind.PostgreSql ? "FALSE" : "0";

    /// <summary>Appends the offset and limit clause appropriate for this engine.</summary>
    /// <param name="sql">Query to paginate, without ordering or pagination.</param>
    /// <param name="order">Ordering clause without the <c>ORDER BY</c> keyword; required by the engines that reject
    /// pagination without ordering.</param>
    /// <param name="skip">Rows to skip, or <see langword="null"/> for none.</param>
    /// <param name="take">Rows to return, or <see langword="null"/> for all.</param>
    /// <param name="parameter">Renders an inline value as a bound parameter name; when omitted the values are written
    /// as literals.</param>
    /// <returns>The paginated statement.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="skip"/> or <paramref name="take"/> is negative.</exception>
    public string Paginate(
        string sql,
        string? order,
        int? skip,
        int? take,
        Func<int, string>? parameter = null
    )
    {
        if (skip < 0 || take < 0)
            throw new ArgumentOutOfRangeException(nameof(skip));
        if (take == 0)
            return $"SELECT * FROM ({sql}) {Quote("empty_result")} WHERE 1 = 0";
        string Bind(int value) =>
            parameter?.Invoke(value) ?? value.ToString(CultureInfo.InvariantCulture);
        var ordered = order is null ? sql : sql + " ORDER BY " + order;
        if (skip is null && take is null)
            return ordered;
        return Kind switch
        {
            DatabaseKind.SqlServer => ordered
                + (order is null ? " ORDER BY (SELECT NULL)" : "")
                + $" OFFSET {Bind(skip ?? 0)} ROWS"
                + (take is null ? "" : $" FETCH NEXT {Bind(take.Value)} ROWS ONLY"),
            DatabaseKind.Oracle => ordered
                + (order is null ? " ORDER BY (SELECT NULL)" : "")
                + $" OFFSET {Bind(skip ?? 0)} ROWS"
                + (take is null ? "" : $" FETCH NEXT {Bind(take.Value)} ROWS ONLY"),
            DatabaseKind.PostgreSql => ordered
                + (take is null ? "" : $" LIMIT {Bind(take.Value)}")
                + (skip is null ? "" : $" OFFSET {Bind(skip.Value)}"),
            DatabaseKind.MySql => ordered
                + $" LIMIT {(take.HasValue ? Bind(take.Value) : "18446744073709551615")} OFFSET {Bind(skip ?? 0)}",
            _ => ordered
                + $" LIMIT {(take.HasValue ? Bind(take.Value) : "-1")} OFFSET {Bind(skip ?? 0)}",
        };
    }

    /// <summary>Maps a column model to the store type of this engine.</summary>
    /// <param name="column">Column to map.</param>
    /// <returns>The provider type name used in DDL.</returns>
    /// <exception cref="NotSupportedException">The column type has no store mapping.</exception>
    public string StoreType(ColumnModel column)
    {
        var length = column.Length > 0 ? column.Length : 255;
        var precision = column.Precision > 0 ? column.Precision : 18;
        return column.Type switch
        {
            DbType.Int16 => Kind == DatabaseKind.Oracle ? "NUMBER(5)" : "SMALLINT",
            DbType.Int32 => Kind == DatabaseKind.Oracle ? "NUMBER(10)" : "INTEGER",
            DbType.Int64 => Kind == DatabaseKind.Oracle ? "NUMBER(19)"
            : Kind == DatabaseKind.Sqlite ? "INTEGER"
            : "BIGINT",
            DbType.Byte => Kind == DatabaseKind.Oracle ? "NUMBER(3)" : "SMALLINT",
            DbType.Boolean => Kind switch
            {
                DatabaseKind.PostgreSql => "BOOLEAN",
                DatabaseKind.SqlServer => "BIT",
                DatabaseKind.Oracle => "NUMBER(1)",
                _ => "INTEGER",
            },
            DbType.Decimal => Kind == DatabaseKind.Oracle
                ? $"NUMBER({precision}, {column.Scale})"
                : $"DECIMAL({precision}, {column.Scale})",
            DbType.Double => Kind switch
            {
                DatabaseKind.PostgreSql => "DOUBLE PRECISION",
                DatabaseKind.SqlServer => "FLOAT",
                DatabaseKind.Oracle => "BINARY_DOUBLE",
                _ => "DOUBLE",
            },
            DbType.Single => Kind == DatabaseKind.Oracle ? "BINARY_FLOAT" : "REAL",
            DbType.Guid => Kind switch
            {
                DatabaseKind.PostgreSql => "UUID",
                DatabaseKind.SqlServer => "UNIQUEIDENTIFIER",
                _ => "CHAR(36)",
            },
            DbType.Date => Kind == DatabaseKind.Sqlite ? "TEXT" : "DATE",
            DbType.Time => Kind is DatabaseKind.Sqlite or DatabaseKind.Oracle
                ? "VARCHAR(32)"
                : "TIME",
            DbType.DateTime2 => Kind switch
            {
                DatabaseKind.SqlServer => "DATETIME2",
                DatabaseKind.MySql => "DATETIME(6)",
                DatabaseKind.Sqlite => "TEXT",
                _ => "TIMESTAMP",
            },
            DbType.DateTimeOffset => Kind switch
            {
                DatabaseKind.PostgreSql or DatabaseKind.Oracle => "TIMESTAMP WITH TIME ZONE",
                DatabaseKind.SqlServer => "DATETIMEOFFSET",
                _ => "VARCHAR(40)",
            },
            DbType.Binary => Kind switch
            {
                DatabaseKind.Sqlite => "BLOB",
                DatabaseKind.PostgreSql => "BYTEA",
                DatabaseKind.SqlServer => "VARBINARY(MAX)",
                DatabaseKind.MySql => "LONGBLOB",
                _ => "BLOB",
            },
            DbType.String => Kind switch
            {
                DatabaseKind.SqlServer => $"NVARCHAR({length})",
                DatabaseKind.Oracle => $"NVARCHAR2({length})",
                _ => $"VARCHAR({length})",
            },
            _ => throw new NotSupportedException($"No store mapping for {column.Type}."),
        };
    }

    /// <summary>Renders one column definition of a <c>CREATE TABLE</c> statement.</summary>
    /// <param name="column">Column to render.</param>
    /// <returns>Quoted name, store type, identity, key and nullability clauses.</returns>
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
                _ => " GENERATED BY DEFAULT AS IDENTITY",
            };
        }
        if (column.PrimaryKey && !(column.Generated && Kind == DatabaseKind.Sqlite))
            sql += " PRIMARY KEY";
        if (!column.Nullable && !column.PrimaryKey)
            sql += " NOT NULL";
        return sql;
    }

    /// <summary>Renders a <c>CREATE TABLE</c> statement for the model.</summary>
    /// <param name="table">Table to create.</param>
    /// <returns>The DDL statement.</returns>
    public string CreateTable(TableModel table) =>
        $"CREATE TABLE {Table(table)} ({string.Join(", ", table.Columns.Select(ColumnDefinition))})";

    /// <summary>Adds a parameter that carries <paramref name="value"/>, adapting the value to what this engine accepts.</summary>
    /// <param name="command">Command to extend.</param>
    /// <param name="index">Zero-based argument position; it determines the placeholder name.</param>
    /// <param name="value">Value to bind.</param>
    /// <param name="column">Column the value belongs to, when known; it drives the provider type, size and precision.</param>
    /// <returns>The created parameter.</returns>
    /// <remarks>
    /// Values that this engine cannot represent natively are converted: GUIDs become text on SQLite, MySQL and Oracle,
    /// booleans become <c>0/1</c> on Oracle, and <see cref="DateOnly"/>, <see cref="TimeOnly"/> and
    /// <see cref="DateTimeOffset"/> become ISO text where the engine has no matching type.
    /// </remarks>
    /// <exception cref="ArgumentException">A string is longer than the declared column length.</exception>
    public DbParameter Bind(DbCommand command, int index, object? value, ColumnModel? column = null)
    {
        if (
            column is { Type: DbType.String, Length: > 0 }
            && value is string text
            && text.Length > column.Length
        )
            throw new ArgumentException(
                $"Value exceeds the declared length of column '{column.Name}'."
            );
        var parameter = command.CreateParameter();
        parameter.ParameterName = Parameter(index);
        if (column is not null)
        {
            parameter.DbType = column.Type switch
            {
                DbType.Guid
                    when Kind is DatabaseKind.Sqlite or DatabaseKind.MySql or DatabaseKind.Oracle =>
                    DbType.String,
                DbType.Boolean when Kind == DatabaseKind.Oracle => DbType.Int32,
                DbType.Date when Kind == DatabaseKind.Sqlite => DbType.String,
                DbType.Time when Kind is DatabaseKind.Sqlite or DatabaseKind.Oracle =>
                    DbType.String,
                DbType.DateTimeOffset when Kind is DatabaseKind.Sqlite or DatabaseKind.MySql =>
                    DbType.String,
                _ => column.Type,
            };
            if (column.Length > 0)
                parameter.Size = column.Length;
            if (column.Precision > 0)
            {
                parameter.Precision = column.Precision;
                parameter.Scale = column.Scale;
            }
        }
        if (
            value is Guid guid
            && Kind is DatabaseKind.Sqlite or DatabaseKind.MySql or DatabaseKind.Oracle
        )
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
