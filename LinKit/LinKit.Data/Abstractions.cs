using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data;

/// <summary>
/// Supported database dialects for LinKit.Data.
/// </summary>
public enum DatabaseDialect
{
    /// <summary>SQLite, which also drives the in-memory and file-based test databases.</summary>
    Sqlite,

    /// <summary>PostgreSQL.</summary>
    PostgreSql,

    /// <summary>Microsoft SQL Server.</summary>
    SqlServer,

    /// <summary>MySQL and compatible servers such as MariaDB.</summary>
    MySql,

    /// <summary>Oracle Database.</summary>
    Oracle,
}

/// <summary>
/// Represents a paginated query result.
/// </summary>
/// <typeparam name="T">Row type.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>Rows of the requested page.</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>Total number of rows that match the filter across all pages.</summary>
    public required long TotalCount { get; init; }

    /// <summary>One-based number of the returned page.</summary>
    public required int PageNumber { get; init; }

    /// <summary>Requested page size.</summary>
    public required int PageSize { get; init; }

    /// <summary>Total number of pages, rounded up; zero when <see cref="PageSize"/> is not positive.</summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;

    /// <summary>Whether a page exists before the returned one.</summary>
    public bool HasPreviousPage => PageNumber > 1;

    /// <summary>Whether a page exists after the returned one.</summary>
    public bool HasNextPage => PageNumber < TotalPages;
}

/// <summary>
/// Lightweight, Native AOT-friendly LINQ queryable contract.
/// Supports both LINQ Query Comprehension syntax (from x in repo.AsQueryable() where ... select x)
/// and method chaining syntax.
/// </summary>
/// <typeparam name="TEntity">Entity type.</typeparam>
public interface ILinKitQueryable<TEntity> : IQueryable<TEntity>
    where TEntity : class
{
    /// <summary>Executes the query and materialises every row.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The materialised rows.</returns>
    Task<List<TEntity>> ToListAsync(CancellationToken ct = default);

    /// <summary>Returns the first row, or <see langword="null"/> when nothing matches.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The first row, or <see langword="null"/>.</returns>
    Task<TEntity?> FirstOrDefaultAsync(CancellationToken ct = default);

    /// <summary>Counts the matching rows on the server.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The row count.</returns>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>Checks whether any row matches.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns><see langword="true"/> when at least one row matches.</returns>
    Task<bool> AnyAsync(CancellationToken ct = default);

    /// <summary>Returns one page of rows plus the total count of the filtered set.</summary>
    /// <param name="pageNumber">One-based page number.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="ct">Token used to cancel the commands.</param>
    /// <returns>The page and the total row count.</returns>
    Task<PagedResult<TEntity>> ToPagedListAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct = default
    );
}

/// <summary>
/// Fluent query builder for entity querying without relying on heavy EF Core runtime.
/// </summary>
/// <typeparam name="TEntity">Entity type.</typeparam>
public interface IFluentQuery<TEntity>
    where TEntity : class
{
    /// <summary>Filters with a raw SQL condition. The condition is appended verbatim, so it must not contain user input.</summary>
    /// <param name="sqlCondition">SQL fragment without the <c>WHERE</c> keyword.</param>
    /// <param name="parameters">Values bound to the fragment's placeholders.</param>
    /// <returns>The same builder, to allow chaining.</returns>
    IFluentQuery<TEntity> Where(string sqlCondition, object? parameters = null);

    /// <summary>Filters with a translated LINQ predicate.</summary>
    /// <param name="predicate">Predicate translated to SQL.</param>
    /// <returns>The same builder, to allow chaining.</returns>
    IFluentQuery<TEntity> Where(Expression<Func<TEntity, bool>> predicate);

    /// <summary>Orders ascending by a physical column name.</summary>
    /// <param name="column">Column name; it is quoted for the active dialect.</param>
    /// <returns>The same builder, to allow chaining.</returns>
    IFluentQuery<TEntity> OrderBy(string column);

    /// <summary>Orders ascending by a translated key selector.</summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="keySelector">Selector translated to SQL.</param>
    /// <returns>The same builder, to allow chaining.</returns>
    IFluentQuery<TEntity> OrderBy<TKey>(Expression<Func<TEntity, TKey>> keySelector);

    /// <summary>Orders descending by a physical column name.</summary>
    /// <param name="column">Column name; it is quoted for the active dialect.</param>
    /// <returns>The same builder, to allow chaining.</returns>
    IFluentQuery<TEntity> OrderByDescending(string column);

    /// <summary>Orders descending by a translated key selector.</summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="keySelector">Selector translated to SQL.</param>
    /// <returns>The same builder, to allow chaining.</returns>
    IFluentQuery<TEntity> OrderByDescending<TKey>(Expression<Func<TEntity, TKey>> keySelector);

    /// <summary>Adds an ascending tie-breaker by physical column name.</summary>
    /// <param name="column">Column name.</param>
    /// <returns>The same builder, to allow chaining.</returns>
    IFluentQuery<TEntity> ThenBy(string column);

    /// <summary>Adds a descending tie-breaker by physical column name.</summary>
    /// <param name="column">Column name.</param>
    /// <returns>The same builder, to allow chaining.</returns>
    IFluentQuery<TEntity> ThenByDescending(string column);

    /// <summary>Adds an ascending tie-breaker by translated key selector.</summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="keySelector">Selector translated to SQL.</param>
    /// <returns>The same builder, to allow chaining.</returns>
    IFluentQuery<TEntity> ThenBy<TKey>(Expression<Func<TEntity, TKey>> keySelector);

    /// <summary>Adds a descending tie-breaker by translated key selector.</summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="keySelector">Selector translated to SQL.</param>
    /// <returns>The same builder, to allow chaining.</returns>
    IFluentQuery<TEntity> ThenByDescending<TKey>(Expression<Func<TEntity, TKey>> keySelector);

    /// <summary>Skips a number of rows; pair it with an ordering for stable results.</summary>
    /// <param name="count">Rows to skip.</param>
    /// <returns>The same builder, to allow chaining.</returns>
    IFluentQuery<TEntity> Skip(int count);

    /// <summary>Limits the number of returned rows.</summary>
    /// <param name="count">Maximum rows to return.</param>
    /// <returns>The same builder, to allow chaining.</returns>
    IFluentQuery<TEntity> Take(int count);

    /// <summary>Executes the query and materialises every row.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The materialised rows.</returns>
    Task<List<TEntity>> ToListAsync(CancellationToken ct = default);

    /// <summary>Returns the first row, or <see langword="null"/> when nothing matches.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The first row, or <see langword="null"/>.</returns>
    Task<TEntity?> FirstOrDefaultAsync(CancellationToken ct = default);

    /// <summary>Counts the matching rows on the server.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The row count.</returns>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>Checks whether any row matches.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns><see langword="true"/> when at least one row matches.</returns>
    Task<bool> AnyAsync(CancellationToken ct = default);

    /// <summary>Returns one page of rows plus the total count of the filtered set.</summary>
    /// <param name="pageNumber">One-based page number.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="ct">Token used to cancel the commands.</param>
    /// <returns>The page and the total row count.</returns>
    Task<PagedResult<TEntity>> ToPagedListAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct = default
    );
}

/// <summary>
/// High-performance, compile-time CRUD and querying repository contract.
/// </summary>
/// <typeparam name="TEntity">Entity type.</typeparam>
/// <typeparam name="TId">Primary key type.</typeparam>
public interface IRepository<TEntity, in TId>
    where TEntity : class
{
    /// <summary>Loads one entity by primary key.</summary>
    /// <param name="id">Primary key value.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The entity, or <see langword="null"/> when no row matches.</returns>
    Task<TEntity?> GetByIdAsync(TId id, CancellationToken ct = default);

    /// <summary>Loads one page of entities without filtering.</summary>
    /// <param name="skip">Rows to skip.</param>
    /// <param name="take">Maximum rows to return.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The loaded entities.</returns>
    Task<List<TEntity>> ListAsync(int skip = 0, int take = 100, CancellationToken ct = default);

    /// <summary>Loads one numbered page together with the total row count.</summary>
    /// <param name="pageNumber">One-based page number.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="ct">Token used to cancel the commands.</param>
    /// <returns>The page and the total row count.</returns>
    Task<PagedResult<TEntity>> GetPagedAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct = default
    );

    /// <summary>Inserts one entity.</summary>
    /// <param name="entity">Entity to insert; generated keys are written back to it.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>A task that completes when the row is written.</returns>
    Task InsertAsync(TEntity entity, CancellationToken ct = default);

    /// <summary>Inserts a batch of entities.</summary>
    /// <param name="entities">Entities to insert.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>A task that completes when the batch is written.</returns>
    Task InsertRangeAsync(IEnumerable<TEntity> entities, CancellationToken ct = default);

    /// <summary>Updates one entity by primary key, honouring optimistic concurrency when configured.</summary>
    /// <param name="entity">Entity carrying the new values and the original concurrency token.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>A task that completes when the row is written.</returns>
    Task UpdateAsync(TEntity entity, CancellationToken ct = default);

    /// <summary>Updates a batch of entities by primary key.</summary>
    /// <param name="entities">Entities carrying the new values.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>A task that completes when the batch is written.</returns>
    Task UpdateRangeAsync(IEnumerable<TEntity> entities, CancellationToken ct = default);

    /// <summary>Deletes one entity by primary key, soft-deleting it when the table declares a soft-delete column.</summary>
    /// <param name="id">Primary key value.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>A task that completes when the row is removed.</returns>
    Task DeleteAsync(TId id, CancellationToken ct = default);

    /// <summary>Deletes a batch of entities by primary key.</summary>
    /// <param name="ids">Primary key values to remove.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>A task that completes when the rows are removed.</returns>
    Task DeleteRangeAsync(IEnumerable<TId> ids, CancellationToken ct = default);

    /// <summary>Counts every row of the table.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The row count.</returns>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>Checks whether a primary key exists.</summary>
    /// <param name="id">Primary key value.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns><see langword="true"/> when the row exists.</returns>
    Task<bool> ExistsAsync(TId id, CancellationToken ct = default);

    /// <summary>Runs a hand written query and maps the rows to entities.</summary>
    /// <param name="sql">SQL text with dialect placeholders such as <c>@p0</c>.</param>
    /// <param name="parameters">Named parameters supplied as an <see cref="IEnumerable{T}"/> of
    /// <see cref="KeyValuePair{TKey, TValue}"/> (for example a <see cref="Dictionary{TKey, TValue}"/>) or of
    /// <c>(string, object?)</c> tuples. Anonymous objects are rejected so the call stays Native AOT compatible.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The mapped entities.</returns>
    Task<List<TEntity>> QuerySqlAsync(
        string sql,
        object? parameters = null,
        CancellationToken ct = default
    );

    /// <summary>Runs a hand written query and maps the first row, or <see langword="null"/>.</summary>
    /// <param name="sql">SQL text with dialect placeholders.</param>
    /// <param name="parameters">Values bound to the placeholders.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The first mapped entity, or <see langword="null"/>.</returns>
    Task<TEntity?> QuerySingleSqlAsync(
        string sql,
        object? parameters = null,
        CancellationToken ct = default
    );

    /// <summary>Runs a non-query statement such as an update or a DDL batch.</summary>
    /// <param name="sql">SQL text with dialect placeholders.</param>
    /// <param name="parameters">Values bound to the placeholders.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The number of affected rows.</returns>
    Task<int> ExecuteSqlAsync(
        string sql,
        object? parameters = null,
        CancellationToken ct = default
    );

    /// <summary>Runs a query and reads a single scalar value.</summary>
    /// <typeparam name="TScalar">Scalar type.</typeparam>
    /// <param name="sql">SQL text with dialect placeholders.</param>
    /// <param name="parameters">Values bound to the placeholders.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The scalar value, or <see langword="null"/> when it is <c>NULL</c>.</returns>
    Task<TScalar?> ExecuteScalarSqlAsync<TScalar>(
        string sql,
        object? parameters = null,
        CancellationToken ct = default
    );

    /// <summary>Starts a fluent query over the table, filtering with raw SQL conditions.</summary>
    /// <returns>A builder over the table.</returns>
    IFluentQuery<TEntity> Query();

    /// <summary>LINQ surface over the table, translated to SQL without materialising rows.</summary>
    ILinKitQueryable<TEntity> Table { get; }

    /// <summary>LINQ surface over the table, identical to <see cref="Table"/>.</summary>
    /// <returns>A queryable over the table.</returns>
    ILinKitQueryable<TEntity> AsQueryable();
}

/// <summary>
/// Factory for creating and configuring database connections.
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>Dialect of the connections this factory produces.</summary>
    DatabaseDialect Dialect { get; }

    /// <summary>Creates a new, closed connection.</summary>
    /// <returns>The connection; the caller owns and disposes it.</returns>
    DbConnection CreateConnection();

    /// <summary>Creates a new, closed connection without blocking the calling thread.</summary>
    /// <param name="ct">Token used to cancel connection creation.</param>
    /// <returns>The connection; the caller owns and disposes it.</returns>
    ValueTask<DbConnection> CreateConnectionAsync(CancellationToken ct = default);
}

/// <summary>
/// Unit of work and ambient transaction manager across repositories.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable, IDisposable
{
    /// <summary>The ambient transaction, or <see langword="null"/> when none is active.</summary>
    DbTransaction? CurrentTransaction { get; }

    /// <summary>Opens the ambient transaction and the connection that carries it.</summary>
    /// <param name="ct">Token used to cancel the operation.</param>
    /// <returns>The opened transaction.</returns>
    Task<DbTransaction> BeginTransactionAsync(CancellationToken ct = default);

    /// <summary>Commits the ambient transaction and runs the before/after commit interceptors.</summary>
    /// <param name="ct">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the transaction is committed.</returns>
    Task CommitAsync(CancellationToken ct = default);

    /// <summary>Rolls back the ambient transaction and runs the after rollback interceptors.</summary>
    /// <param name="ct">Token used to cancel the operation.</param>
    /// <returns>A task that completes when the transaction is rolled back.</returns>
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
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;

    /// <summary>
    /// Runs after the transaction has committed and been disposed. Failures are reported as
    /// <see cref="UnitOfWorkHookException"/>; they cannot undo the committed transaction.
    /// </summary>
    Task AfterCommitAsync(DbConnection connection, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Runs after a transaction has been successfully rolled back and disposed.
    /// </summary>
    Task AfterRollbackAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;
}

/// <summary>
/// Reports failures in Unit of Work lifecycle callbacks.
/// </summary>
public sealed class UnitOfWorkHookException : Exception
{
    /// <summary>Name of the lifecycle callback that failed.</summary>
    public string HookName { get; }

    /// <summary>Whether the transaction had already committed when the failure was observed.</summary>
    public bool TransactionCommitted { get; }

    /// <summary>Creates the exception that aggregates the failed callbacks.</summary>
    /// <param name="hookName">Name of the lifecycle callback that failed.</param>
    /// <param name="transactionCommitted">Whether the transaction had already committed.</param>
    /// <param name="innerException">The aggregated callback failures.</param>
    public UnitOfWorkHookException(
        string hookName,
        bool transactionCommitted,
        AggregateException innerException
    )
        : base(
            transactionCommitted
                ? $"The transaction committed, but one or more {hookName} callbacks failed."
                : $"The transaction rolled back, but one or more {hookName} callbacks failed.",
            innerException
        )
    {
        HookName = hookName;
        TransactionCommitted = transactionCommitted;
    }
}
