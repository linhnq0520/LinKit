using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data;

/// <summary>
/// Execution context provided to IDataMigration during UpAsync or DownAsync execution.
/// Supports raw SQL, strongly-typed LinKit repositories, and dialect-safe DDL helpers.
/// </summary>
public interface IMigrationContext
{
    DbConnection Connection { get; }
    DbTransaction? Transaction { get; }
    DatabaseDialect Dialect { get; }
    IServiceProvider Services { get; }

    /// <summary>
    /// Executes a SQL statement within the migration transaction.
    /// </summary>
    Task<int> ExecuteSqlAsync(string sql, object? parameters = null, CancellationToken ct = default);

    /// <summary>
    /// Queries records using SQL within the migration transaction.
    /// </summary>
    Task<List<T>> QuerySqlAsync<T>(string sql, Func<DbDataReader, T> map, object? parameters = null, CancellationToken ct = default);

    /// <summary>
    /// Executes a scalar query within the migration transaction.
    /// </summary>
    Task<object?> ExecuteScalarAsync(string sql, object? parameters = null, CancellationToken ct = default);

    /// <summary>
    /// Checks if a record exists matching the given where condition in the specified table.
    /// </summary>
    Task<bool> ExistsAsync(string tableName, string whereCondition, object? parameters = null, CancellationToken ct = default);

    /// <summary>
    /// Type-safe check if any record exists matching the given LINQ predicate for entity TEntity (Cách 1).
    /// Eliminates raw SQL strings, table names, and manual parameter escaping.
    /// </summary>
    Task<bool> ExistsAsync<TEntity>(System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class;

    /// <summary>
    /// Resolves a strongly-typed LinKit entity repository from the current service provider.
    /// </summary>
    IRepository<TEntity, TId> GetRepository<TEntity, TId>() where TEntity : class;

    // --- Dialect-Safe DDL Helpers ---

    /// <summary>
    /// Renames a table column while preserving existing data across SQLite, PostgreSQL, SQL Server, MySQL, and Oracle.
    /// </summary>
    Task RenameColumnAsync(string tableName, string oldColumnName, string newColumnName, CancellationToken ct = default);

    /// <summary>
    /// Drops a column from the specified table.
    /// </summary>
    Task DropColumnAsync(string tableName, string columnName, CancellationToken ct = default);

    /// <summary>
    /// Renames a database table.
    /// </summary>
    Task RenameTableAsync(string oldTableName, string newTableName, CancellationToken ct = default);

    /// <summary>
    /// Drops a database table.
    /// </summary>
    Task DropTableAsync(string tableName, CancellationToken ct = default);
}
