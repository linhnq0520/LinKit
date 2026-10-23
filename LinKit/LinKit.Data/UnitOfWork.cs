using System;
using System.Data;
using System.Data.Common;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LinKit.Data;

public class DelegateDbConnectionFactory : IDbConnectionFactory
{
    private readonly Func<DbConnection> _connectionCreator;
    public DatabaseDialect Dialect { get; }

    public DelegateDbConnectionFactory(DatabaseDialect dialect, Func<DbConnection> connectionCreator)
    {
        Dialect = dialect;
        _connectionCreator = connectionCreator ?? throw new ArgumentNullException(nameof(connectionCreator));
    }

    public DbConnection CreateConnection() => _connectionCreator();

    public ValueTask<DbConnection> CreateConnectionAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(_connectionCreator());
}

public class UnitOfWork : IUnitOfWork
{
    private readonly IDbConnectionFactory _factory;
    private readonly IReadOnlyList<IUnitOfWorkInterceptor> _interceptors;
    private readonly ILogger<UnitOfWork>? _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private DbConnection? _connection;
    private DbTransaction? _transaction;

    public UnitOfWork(IDbConnectionFactory factory)
        : this(factory, null, null)
    {
    }

    public UnitOfWork(
        IDbConnectionFactory factory,
        IEnumerable<IUnitOfWorkInterceptor>? interceptors = null,
        ILogger<UnitOfWork>? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _interceptors = interceptors is null
            ? Array.Empty<IUnitOfWorkInterceptor>()
            : new List<IUnitOfWorkInterceptor>(interceptors);
        _logger = logger;
    }

    public DbTransaction? CurrentTransaction => _transaction;
    public DbConnection? CurrentConnection => _connection;

    public async Task<DbTransaction> BeginTransactionAsync(CancellationToken ct = default)
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

            _transaction = await _connection.BeginTransactionAsync(ct);
            return _transaction;
        }
        finally
        {
            _syncLock.Release();
        }
    }

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
                        rollbackException);
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
                    interceptor.AfterCommitAsync(currentConnection, CancellationToken.None));
            if (operationFailure is not null && hookFailure is not null)
                throw new AggregateException("The transaction committed, but cleanup and post-commit callbacks failed.", operationFailure, hookFailure);
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
                    interceptor.AfterRollbackAsync(currentConnection, CancellationToken.None));
            if (operationFailure is not null && hookFailure is not null)
                throw new AggregateException("The transaction was rolled back after a commit failure, and rollback callbacks also failed.", operationFailure, hookFailure);
            if (hookFailure is not null)
                ExceptionDispatchInfo.Capture(hookFailure).Throw();
        }

        if (operationFailure is not null)
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
    }

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
                    interceptor.AfterRollbackAsync(currentConnection, CancellationToken.None));
            if (operationFailure is not null && hookFailure is not null)
                throw new AggregateException("The transaction was rolled back, but cleanup and rollback callbacks failed.", operationFailure, hookFailure);
            if (hookFailure is not null)
                ExceptionDispatchInfo.Capture(hookFailure).Throw();
        }

        if (operationFailure is not null)
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
    }

    private async Task<Exception?> InvokePostTransactionHooksAsync(
        string hookName,
        DbConnection connection,
        Func<IUnitOfWorkInterceptor, DbConnection, Task> invoke)
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
            : new UnitOfWorkHookException(hookName, hookName == "AfterCommit", new AggregateException(failures));
    }

    public void Dispose()
    {
        _syncLock.Dispose();
        _transaction?.Dispose();
        _connection?.Dispose();
        _transaction = null;
        _connection = null;
    }

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
