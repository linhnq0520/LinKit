using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data;

/// <summary>
/// Supported database dialects for LinKit.Data.
/// </summary>
public enum DatabaseDialect
{
    Sqlite,
    PostgreSql,
    SqlServer,
    MySql,
    Oracle
}

/// <summary>
/// Represents a paginated query result.
/// </summary>
public sealed class PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required long TotalCount { get; init; }
    public required int PageNumber { get; init; }
    public required int PageSize { get; init; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;
}

/// <summary>
/// Lightweight, Native AOT-friendly LINQ queryable contract.
/// Supports both LINQ Query Comprehension syntax (from x in repo.AsQueryable() where ... select x)
/// and method chaining syntax.
/// </summary>
public interface ILinKitQueryable<TEntity> : IQueryable<TEntity> where TEntity : class
{
    Task<List<TEntity>> ToListAsync(CancellationToken ct = default);
    Task<TEntity?> FirstOrDefaultAsync(CancellationToken ct = default);
    Task<long> CountAsync(CancellationToken ct = default);
    Task<bool> AnyAsync(CancellationToken ct = default);
    Task<PagedResult<TEntity>> ToPagedListAsync(int pageNumber, int pageSize, CancellationToken ct = default);
}

/// <summary>
/// Fluent query builder for entity querying without relying on heavy EF Core runtime.
/// </summary>
public interface IFluentQuery<TEntity> where TEntity : class
{
    IFluentQuery<TEntity> Where(string sqlCondition, object? parameters = null);
    IFluentQuery<TEntity> OrderBy(string column);
    IFluentQuery<TEntity> OrderByDescending(string column);
    IFluentQuery<TEntity> Skip(int count);
    IFluentQuery<TEntity> Take(int count);

    Task<List<TEntity>> ToListAsync(CancellationToken ct = default);
    Task<TEntity?> FirstOrDefaultAsync(CancellationToken ct = default);
    Task<long> CountAsync(CancellationToken ct = default);
    Task<bool> AnyAsync(CancellationToken ct = default);
    Task<PagedResult<TEntity>> ToPagedListAsync(int pageNumber, int pageSize, CancellationToken ct = default);
}

/// <summary>
/// High-performance, compile-time CRUD and querying repository contract.
/// </summary>
public interface IRepository<TEntity, in TId> where TEntity : class
{
    // Single & Bulk read
    Task<TEntity?> GetByIdAsync(TId id, CancellationToken ct = default);
    Task<List<TEntity>> ListAsync(int skip = 0, int take = 100, CancellationToken ct = default);
    Task<PagedResult<TEntity>> GetPagedAsync(int pageNumber, int pageSize, CancellationToken ct = default);

    // Insert
    Task InsertAsync(TEntity entity, CancellationToken ct = default);
    Task InsertRangeAsync(IEnumerable<TEntity> entities, CancellationToken ct = default);

    // Update
    Task UpdateAsync(TEntity entity, CancellationToken ct = default);
    Task UpdateRangeAsync(IEnumerable<TEntity> entities, CancellationToken ct = default);

    // Delete
    Task DeleteAsync(TId id, CancellationToken ct = default);
    Task DeleteRangeAsync(IEnumerable<TId> ids, CancellationToken ct = default);

    // Aggregate & check
    Task<long> CountAsync(CancellationToken ct = default);
    Task<bool> ExistsAsync(TId id, CancellationToken ct = default);

    // Raw SQL Execution & Query
    Task<List<TEntity>> QuerySqlAsync(string sql, object? parameters = null, CancellationToken ct = default);
    Task<TEntity?> QuerySingleSqlAsync(string sql, object? parameters = null, CancellationToken ct = default);
    Task<int> ExecuteSqlAsync(string sql, object? parameters = null, CancellationToken ct = default);
    Task<TScalar?> ExecuteScalarSqlAsync<TScalar>(string sql, object? parameters = null, CancellationToken ct = default);

    // Fluent Query Builder
    IFluentQuery<TEntity> Query();

    // LINQ Queryable (supports 'from a in repo.AsQueryable() where ... select a')
    ILinKitQueryable<TEntity> AsQueryable();
}

/// <summary>
/// Factory for creating and configuring database connections.
/// </summary>
public interface IDbConnectionFactory
{
    DatabaseDialect Dialect { get; }
    DbConnection CreateConnection();
    ValueTask<DbConnection> CreateConnectionAsync(CancellationToken ct = default);
}

/// <summary>
/// Unit of work and ambient transaction manager across repositories.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable, IDisposable
{
    DbTransaction? CurrentTransaction { get; }
    Task<DbTransaction> BeginTransactionAsync(CancellationToken ct = default);
    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
}

/// <summary>
/// Observes Unit of Work transaction lifecycle events. Interceptors are invoked in DI registration order.
/// </summary>
public interface IUnitOfWorkInterceptor
{
    /// <summary>
    /// Runs inside the active transaction. Throwing prevents the commit and causes LinKit.Data to roll back.
    /// Use the supplied connection and transaction directly; do not call UnitOfWork methods from this callback.
    /// </summary>
    Task BeforeCommitAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Runs after the transaction has committed and been disposed. Failures are reported as
    /// <see cref="UnitOfWorkHookException"/>; they cannot undo the committed transaction.
    /// </summary>
    Task AfterCommitAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Runs after a transaction has been successfully rolled back and disposed.
    /// </summary>
    Task AfterRollbackAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Reports failures in Unit of Work lifecycle callbacks.
/// </summary>
public sealed class UnitOfWorkHookException : Exception
{
    public string HookName { get; }
    public bool TransactionCommitted { get; }

    public UnitOfWorkHookException(string hookName, bool transactionCommitted, AggregateException innerException)
        : base(
            transactionCommitted
                ? $"The transaction committed, but one or more {hookName} callbacks failed."
                : $"The transaction rolled back, but one or more {hookName} callbacks failed.",
            innerException)
    {
        HookName = hookName;
        TransactionCommitted = transactionCommitted;
    }
}
