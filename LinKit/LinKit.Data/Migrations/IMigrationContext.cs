using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data;

/// <summary>
/// Execution context provided to migrations during their up or down execution.
/// Supports raw SQL, strongly-typed LinKit repositories, and dialect-safe DDL helpers.
/// </summary>
public interface IMigrationContext
{
    /// <summary>Connection the migration runs on; the migration runner owns and disposes it.</summary>
    DbConnection Connection { get; }

    /// <summary>Transaction that wraps the migration, or <see langword="null"/> when DDL runs without one.</summary>
    DbTransaction? Transaction { get; }

    /// <summary>Dialect of the database being migrated.</summary>
    DatabaseDialect Dialect { get; }

    /// <summary>Service provider of the migration scope, used to resolve repositories and application services.</summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Executes a SQL statement within the migration transaction.
    /// </summary>
    /// <param name="sql">SQL text to execute.</param>
    /// <param name="parameters">Named parameters as <c>IEnumerable&lt;KeyValuePair&lt;string, object?&gt;&gt;</c> or
    /// <c>IEnumerable&lt;(string, object?)&gt;</c>; anonymous objects are rejected in Native AOT mode.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The number of affected rows.</returns>
    Task<int> ExecuteSqlAsync(
        string sql,
        object? parameters = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Queries records using SQL within the migration transaction.
    /// </summary>
    /// <param name="sql">SQL text to execute.</param>
    /// <param name="map">Maps the current row of the reader to a value.</param>
    /// <param name="parameters">Named parameters as a dictionary or tuple sequence.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The materialised rows.</returns>
    Task<List<T>> QuerySqlAsync<T>(
        string sql,
        Func<DbDataReader, T> map,
        object? parameters = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Executes a scalar query within the migration transaction.
    /// </summary>
    /// <param name="sql">SQL text to execute.</param>
    /// <param name="parameters">Named parameters as a dictionary or tuple sequence.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The first column of the first row, or <see langword="null"/> when it is <c>NULL</c>.</returns>
    Task<object?> ExecuteScalarAsync(
        string sql,
        object? parameters = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Checks if a record exists matching the given where condition in the specified table.
    /// </summary>
    /// <param name="tableName">Table to probe; the name is quoted for the active dialect.</param>
    /// <param name="whereCondition">Condition without the <c>WHERE</c> keyword, or blank to probe the whole table.</param>
    /// <param name="parameters">Named parameters as a dictionary or tuple sequence.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns><see langword="true"/> when at least one row matches.</returns>
    Task<bool> ExistsAsync(
        string tableName,
        string whereCondition,
        object? parameters = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Type-safe check whether any record matches the given LINQ predicate for <typeparamref name="TEntity"/>.
    /// Eliminates raw SQL strings, table names and manual parameter escaping.
    /// </summary>
    /// <typeparam name="TEntity">Mapped entity type; its table name and columns come from the generated metadata.</typeparam>
    /// <param name="predicate">Predicate translated to a <c>WHERE</c> clause.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns><see langword="true"/> when at least one row matches.</returns>
    Task<bool> ExistsAsync<TEntity>(
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate,
        CancellationToken ct = default
    )
        where TEntity : class;

    /// <summary>
    /// Resolves a strongly-typed LinKit entity repository from the current service provider.
    /// </summary>
    /// <typeparam name="TEntity">Entity type.</typeparam>
    /// <typeparam name="TId">Primary key type.</typeparam>
    /// <returns>The scoped repository, so migration writes join the ambient transaction.</returns>
    IRepository<TEntity, TId> GetRepository<TEntity, TId>()
        where TEntity : class;

    /// <summary>
    /// Renames a table column while preserving existing data across SQLite, PostgreSQL, SQL Server, MySQL and Oracle.
    /// </summary>
    /// <param name="tableName">Table that owns the column.</param>
    /// <param name="oldColumnName">Current column name.</param>
    /// <param name="newColumnName">New column name.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>A task that completes when the column is renamed.</returns>
    Task RenameColumnAsync(
        string tableName,
        string oldColumnName,
        string newColumnName,
        CancellationToken ct = default
    );

    /// <summary>
    /// Drops a column from the specified table.
    /// </summary>
    /// <param name="tableName">Table that owns the column.</param>
    /// <param name="columnName">Column to drop.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>A task that completes when the column is dropped.</returns>
    Task DropColumnAsync(string tableName, string columnName, CancellationToken ct = default);

    /// <summary>
    /// Renames a database table.
    /// </summary>
    /// <param name="oldTableName">Current table name.</param>
    /// <param name="newTableName">New table name.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>A task that completes when the table is renamed.</returns>
    Task RenameTableAsync(string oldTableName, string newTableName, CancellationToken ct = default);

    /// <summary>
    /// Drops a database table.
    /// </summary>
    /// <param name="tableName">Table to drop.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>A task that completes when the table is dropped.</returns>
    Task DropTableAsync(string tableName, CancellationToken ct = default);
}
