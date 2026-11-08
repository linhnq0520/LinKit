using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LinKit.Data;

/// <summary>Adapts a plain connection factory delegate to <see cref="IDbConnectionFactory"/>.</summary>
public class DelegateDbConnectionFactory : IDbConnectionFactory
{
    private readonly Func<DbConnection> _connectionCreator;

    /// <summary>Dialect reported to consumers of the factory.</summary>
    public DatabaseDialect Dialect { get; }

    /// <summary>Creates the factory.</summary>
    /// <param name="dialect">Dialect reported by <see cref="Dialect"/>.</param>
    /// <param name="connectionCreator">Creates a new, unopened connection on each call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connectionCreator"/> is <see langword="null"/>.</exception>
    public DelegateDbConnectionFactory(
        DatabaseDialect dialect,
        Func<DbConnection> connectionCreator
    )
    {
        Dialect = dialect;
        _connectionCreator =
            connectionCreator ?? throw new ArgumentNullException(nameof(connectionCreator));
    }

    /// <summary>Creates an unopened connection.</summary>
    /// <returns>The new connection.</returns>
    public DbConnection CreateConnection() => _connectionCreator();

    /// <summary>Creates an unopened connection.</summary>
    /// <param name="ct">Unused; the delegate is synchronous.</param>
    /// <returns>The new connection wrapped in a completed task.</returns>
    public ValueTask<DbConnection> CreateConnectionAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(_connectionCreator());
}

/// <summary>
/// Owns one connection and at most one transaction, running the registered interceptors around commit and rollback.
/// </summary>
/// <remarks>
/// The unit of work is the place where business writes, outbox rows and interceptors share a single transaction:
/// a <see cref="DataSession"/> created in the same scope borrows this connection instead of opening its own.
/// </remarks>
public class UnitOfWork : IUnitOfWork
{
    private readonly IDbConnectionFactory _factory;
    private readonly IReadOnlyList<IUnitOfWorkInterceptor> _interceptors;
    private readonly ILogger<UnitOfWork>? _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private DbConnection? _connection;
    private DbTransaction? _transaction;

    /// <summary>Creates a unit of work without interceptors or logging.</summary>
    /// <param name="factory">Factory used to open the connection on demand.</param>
    public UnitOfWork(IDbConnectionFactory factory)
        : this(factory, null, null) { }

    /// <summary>Creates a unit of work.</summary>
    /// <param name="factory">Factory used to open the connection on demand.</param>
    /// <param name="interceptors">Callbacks invoked before commit and after commit or rollback.</param>
    /// <param name="logger">Optional logger that records failures of the post-transaction callbacks.</param>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    public UnitOfWork(
        IDbConnectionFactory factory,
        IEnumerable<IUnitOfWorkInterceptor>? interceptors = null,
        ILogger<UnitOfWork>? logger = null
    )
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _interceptors = interceptors is null
            ? Array.Empty<IUnitOfWorkInterceptor>()
            : new List<IUnitOfWorkInterceptor>(interceptors);
        _logger = logger;
    }

    /// <summary>The active transaction, or <see langword="null"/> when none was started.</summary>
    public DbTransaction? CurrentTransaction => _transaction;

    /// <summary>The connection owned by the unit of work, or <see langword="null"/> before the first use.</summary>
    public DbConnection? CurrentConnection => _connection;

    internal async Task<DbConnection> EnsureConnectionAsync(CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            _connection ??= await _factory.CreateConnectionAsync(ct);
            if (_connection.State != ConnectionState.Open)
                await _connection.OpenAsync(ct);
            return _connection;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>Starts a transaction with the provider default isolation level, or returns the active one.</summary>
    /// <param name="ct">Token used to cancel the operation.</param>
    /// <returns>The active transaction.</returns>
    public Task<DbTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
        BeginTransactionAsync(IsolationLevel.Unspecified, ct);

    /// <summary>Starts (or returns the already active) transaction using the requested isolation level.</summary>
    public async Task<DbTransaction> BeginTransactionAsync(
        IsolationLevel isolation,
        CancellationToken ct = default
    )
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            if (_transaction != null)
                return _transaction;

            _connection ??= await _factory.CreateConnectionAsync(ct);
            if (_connection.State != ConnectionState.Open)
            {
                await _connection.OpenAsync(ct);
            }

            _transaction = await _connection.BeginTransactionAsync(isolation, ct);
            return _transaction;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>Commits the active transaction after running the interceptors.</summary>
    /// <param name="ct">Token used to cancel the commit.</param>
    /// <returns>A task that completes when the transaction is committed.</returns>
    /// <exception cref="UnitOfWorkHookException">The commit succeeded but a post-commit callback failed.</exception>
    /// <exception cref="AggregateException">The commit and the rollback both failed, or cleanup failed as well.</exception>
    /// <remarks>A failed commit is rolled back before the failure is rethrown, so the caller sees the original error.</remarks>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        DbConnection? connection = null;
        DbTransaction? transaction = null;
        Exception? operationFailure = null;
        bool committed = false;
        bool rolledBack = false;
        try
        {
            transaction = _transaction;
            connection = _connection;
            if (transaction is null)
                return;

            try
            {
                foreach (var interceptor in _interceptors)
                {
                    await interceptor.BeforeCommitAsync(connection!, transaction, ct);
                }

                await transaction.CommitAsync(ct);
                committed = true;
            }
            catch (Exception ex)
            {
                operationFailure = ex;
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    rolledBack = true;
                }
                catch (Exception rollbackException)
                {
                    operationFailure = new AggregateException(
                        "Unit of Work commit failed, and the transaction could not be rolled back.",
                        ex,
                        rollbackException
                    );
                }
            }
            finally
            {
                try
                {
                    await transaction.DisposeAsync();
                }
                catch (Exception disposeException)
                {
                    operationFailure = operationFailure is null
                        ? disposeException
                        : new AggregateException(operationFailure, disposeException);
                }

                _transaction = null;
            }
        }
        finally
        {
            _syncLock.Release();
        }

        if (committed)
        {
            var hookFailure = await InvokePostTransactionHooksAsync(
                "AfterCommit",
                connection!,
                static (interceptor, currentConnection) =>
                    interceptor.AfterCommitAsync(currentConnection, CancellationToken.None)
            );
            if (operationFailure is not null && hookFailure is not null)
                throw new AggregateException(
                    "The transaction committed, but cleanup and post-commit callbacks failed.",
                    operationFailure,
                    hookFailure
                );
            if (hookFailure is not null)
                ExceptionDispatchInfo.Capture(hookFailure).Throw();
            if (operationFailure is not null)
                ExceptionDispatchInfo.Capture(operationFailure).Throw();
            return;
        }

        if (rolledBack)
        {
            var hookFailure = await InvokePostTransactionHooksAsync(
                "AfterRollback",
                connection!,
                static (interceptor, currentConnection) =>
                    interceptor.AfterRollbackAsync(currentConnection, CancellationToken.None)
            );
            if (operationFailure is not null && hookFailure is not null)
                throw new AggregateException(
                    "The transaction was rolled back after a commit failure, and rollback callbacks also failed.",
                    operationFailure,
                    hookFailure
                );
            if (hookFailure is not null)
                ExceptionDispatchInfo.Capture(hookFailure).Throw();
        }

        if (operationFailure is not null)
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
    }

    /// <summary>Rolls back the active transaction.</summary>
    /// <param name="ct">Token used to cancel the rollback.</param>
    /// <returns>A task that completes when the transaction is rolled back.</returns>
    /// <exception cref="UnitOfWorkHookException">The rollback succeeded but a post-rollback callback failed.</exception>
    public async Task RollbackAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        DbConnection? connection = null;
        bool rolledBack = false;
        Exception? operationFailure = null;
        try
        {
            var transaction = _transaction;
            connection = _connection;
            if (transaction is not null)
            {
                try
                {
                    await transaction.RollbackAsync(ct);
                    rolledBack = true;
                }
                catch (Exception ex)
                {
                    operationFailure = ex;
                }
                finally
                {
                    try
                    {
                        await transaction.DisposeAsync();
                    }
                    catch (Exception disposeException)
                    {
                        operationFailure = operationFailure is null
                            ? disposeException
                            : new AggregateException(operationFailure, disposeException);
                    }

                    _transaction = null;
                }
            }
        }
        finally
        {
            _syncLock.Release();
        }

        if (rolledBack)
        {
            var hookFailure = await InvokePostTransactionHooksAsync(
                "AfterRollback",
                connection!,
                static (interceptor, currentConnection) =>
                    interceptor.AfterRollbackAsync(currentConnection, CancellationToken.None)
            );
            if (operationFailure is not null && hookFailure is not null)
                throw new AggregateException(
                    "The transaction was rolled back, but cleanup and rollback callbacks failed.",
                    operationFailure,
                    hookFailure
                );
            if (hookFailure is not null)
                ExceptionDispatchInfo.Capture(hookFailure).Throw();
        }

        if (operationFailure is not null)
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
    }

    private async Task<Exception?> InvokePostTransactionHooksAsync(
        string hookName,
        DbConnection connection,
        Func<IUnitOfWorkInterceptor, DbConnection, Task> invoke
    )
    {
        List<Exception>? failures = null;
        foreach (var interceptor in _interceptors)
        {
            try
            {
                await invoke(interceptor, connection);
            }
            catch (Exception ex)
            {
                (failures ??= new List<Exception>()).Add(ex);
                _logger?.LogError(ex, "LinKit.Data {HookName} callback failed.", hookName);
            }
        }

        return failures is null
            ? null
            : new UnitOfWorkHookException(
                hookName,
                hookName == "AfterCommit",
                new AggregateException(failures)
            );
    }

    /// <summary>Disposes the transaction, the connection and the internal lock.</summary>
    public void Dispose()
    {
        _syncLock.Dispose();
        _transaction?.Dispose();
        _connection?.Dispose();
        _transaction = null;
        _connection = null;
    }

    /// <summary>Asynchronously disposes the transaction, the connection and the internal lock.</summary>
    /// <returns>A task that completes when the resources are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_transaction != null)
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }
        if (_connection != null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
        _syncLock.Dispose();
    }
}
