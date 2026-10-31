using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data;

internal static class Activities
{
    private static readonly ActivitySource Source = new("LinKit.Data", "1.0.0");
    public static Activity? StartActivity(string name, ActivityKind kind) => Source.StartActivity(name, kind);
}

public sealed class CommitOutcomeUnknownException(Exception inner) : Exception("Transaction commit outcome is unknown.", inner);

public sealed class DataSession(
    Func<DbConnection> connectionFactory,
    SqlDialect dialect,
    string? tenantId = null,
    Func<DbCommand, ValueTask>? configureCommand = null) : IAsyncDisposable
{
    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private bool _opened;
    private bool _disposed;
    private bool _faulted;
    private int _busy;

    public SqlDialect Dialect { get; } = dialect;
    public string? TenantId { get; } = tenantId;
    public bool HasTransaction => ActiveTransaction is not null;

    /// <summary>
    /// When set, the session borrows the scoped UnitOfWork's connection and transaction instead of opening its own,
    /// so business writes and outbox rows share one transaction. The UnitOfWork keeps sole ownership/disposal.
    /// </summary>
    internal UnitOfWork? SharedUnitOfWork { get; init; }

    private DbTransaction? ActiveTransaction => SharedUnitOfWork is { } shared ? shared.CurrentTransaction : _transaction;

    internal bool OwnsTransaction(DbTransaction transaction) => !_disposed && ReferenceEquals(ActiveTransaction, transaction);

    public Query<TEntity> From<TEntity>(Table<TEntity> table) where TEntity : class => Query<TEntity>.From(this, table);

    private void Enter()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) throw new InvalidOperationException("Session is unusable after transaction completion failed.");
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Concurrent operations on one DataSession are not supported. Use separate sessions.");
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        if (SharedUnitOfWork is { } shared)
        {
            _connection = await shared.EnsureConnectionAsync(ct).ConfigureAwait(false);
            _opened = true;
            return _connection;
        }
        _connection ??= connectionFactory();
        if (_connection.State != ConnectionState.Open)
        {
            if (_opened) throw new InvalidOperationException("The session connection was lost or closed. Create a new session; transaction and advisory-lock state cannot be recovered by reconnecting.");
            await _connection.OpenAsync(ct).ConfigureAwait(false);
        }
        _opened = true;
        return _connection;
    }

    private async Task<DbCommand> PrepareAsync(CommandSpec spec, CancellationToken ct)
    {
        if (Dialect.Kind == DatabaseKind.Oracle && configureCommand is null)
            throw new InvalidOperationException("Oracle requires configureCommand to set OracleCommand.BindByName = true.");
        if (spec.Arguments.Count > Dialect.MaxParameters) throw new NotSupportedException("Parameter limit exceeded. Split the operation into batches.");
        var connection = await OpenAsync(ct).ConfigureAwait(false);
        var command = connection.CreateCommand();
        try
        {
            command.Transaction = ActiveTransaction;
            command.CommandTimeout = 30;
            command.CommandText = spec.Sql;
            if (configureCommand is not null) await configureCommand(command).ConfigureAwait(false);
            for (var index = 0; index < spec.Arguments.Count; index++)
            {
                Dialect.Bind(command, index, spec.Arguments[index].Value, spec.Arguments[index].Column);
            }
            return command;
        }
        catch { await command.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<int> ExecuteAsync(CommandSpec spec, CancellationToken ct = default)
    {
        Enter();
        using var activity = Activities.StartActivity("db.execute", ActivityKind.Client);
        activity?.SetTag("db.system.name", Dialect.Kind.ToString());
        try
        {
            await using var command = await PrepareAsync(spec, ct).ConfigureAwait(false);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error) { activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name); throw; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public async Task<object?> ScalarAsync(CommandSpec spec, CancellationToken ct = default)
    {
        Enter();
        try
        {
            await using var command = await PrepareAsync(spec, ct).ConfigureAwait(false);
            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is DBNull ? null : result;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    internal async Task<object> InsertIdentityAsync(CommandSpec spec, ColumnModel key, CancellationToken ct)
    {
        if (Dialect.Kind != DatabaseKind.Oracle)
            return await ScalarAsync(spec, ct).ConfigureAwait(false) ?? throw new DataMappingException("INSERT returned no generated key.");
        Enter();
        try
        {
            await using var command = await PrepareAsync(spec, ct).ConfigureAwait(false);
            var parameter = command.CreateParameter();
            parameter.ParameterName = ":generated_key";
            parameter.DbType = key.Type;
            parameter.Direction = ParameterDirection.Output;
            command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return parameter.Value is null or DBNull ? throw new DataMappingException("INSERT returned no key.") : parameter.Value;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public async IAsyncEnumerable<T> StreamAsync<T>(CommandSpec spec, Func<DbDataReader, T> read,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Enter();
        using var activity = Activities.StartActivity("db.query", ActivityKind.Client);
        activity?.SetTag("db.system.name", Dialect.Kind.ToString());
        try
        {
            await using var command = await PrepareAsync(spec, ct).ConfigureAwait(false);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) yield return read(reader);
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public async Task<List<T>> QueryAsync<T>(CommandSpec spec, Func<DbDataReader, T> read, CancellationToken ct = default)
    {
        var results = new List<T>();
        await foreach (var item in StreamAsync(spec, read, ct).ConfigureAwait(false)) results.Add(item);
        return results;
    }

    public Task<int> ExecuteSqlAsync(string sql, IReadOnlyList<SqlArgument>? arguments = null, CancellationToken ct = default)
        => ExecuteAsync(new CommandSpec(sql, arguments ?? Array.Empty<SqlArgument>()), ct);

    public Task<object?> ScalarSqlAsync(string sql, IReadOnlyList<SqlArgument>? arguments = null, CancellationToken ct = default)
        => ScalarAsync(new CommandSpec(sql, arguments ?? Array.Empty<SqlArgument>()), ct);

    public Task<List<T>> QuerySqlAsync<T>(string sql, Func<DbDataReader, T> read, IReadOnlyList<SqlArgument>? arguments = null, CancellationToken ct = default)
        => QueryAsync(new CommandSpec(sql, arguments ?? Array.Empty<SqlArgument>()), read, ct);

    public async Task<SessionTransaction> BeginTransactionAsync(IsolationLevel isolation = IsolationLevel.Unspecified, CancellationToken ct = default)
    {
        Enter();
        try
        {
            if (ActiveTransaction is not null) throw new InvalidOperationException("Nested transactions are not supported.");
            if (SharedUnitOfWork is { } sharedUow)
            {
                var shared = await sharedUow.BeginTransactionAsync(ct).ConfigureAwait(false);
                _connection = shared.Connection;
                _opened = true;
                return new SessionTransaction(this, shared);
            }
            var connection = await OpenAsync(ct).ConfigureAwait(false);
            _transaction = await connection.BeginTransactionAsync(isolation, ct).ConfigureAwait(false);
            return new SessionTransaction(this, _transaction);
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    internal async Task CompleteAsync(DbTransaction expected, bool commit, CancellationToken ct)
    {
        Enter();
        try
        {
            if (!ReferenceEquals(ActiveTransaction, expected)) throw new InvalidOperationException("Transaction is no longer active.");
            if (SharedUnitOfWork is { } sharedUow)
            {
                try
                {
                    if (commit) await sharedUow.CommitAsync(ct).ConfigureAwait(false);
                    else await sharedUow.RollbackAsync(ct).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    _faulted = true;
                    if (commit) throw new CommitOutcomeUnknownException(error);
                    throw;
                }
                return;
            }
            try
            {
                if (commit) await expected.CommitAsync(ct).ConfigureAwait(false);
                else await expected.RollbackAsync(ct).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _faulted = true;
                if (commit) throw new CommitOutcomeUnknownException(error);
                throw;
            }
            finally { _transaction = null; await expected.DisposeAsync().ConfigureAwait(false); }
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) throw new InvalidOperationException("Dispose the active operation/stream before the session.");
        _disposed = true;
        if (SharedUnitOfWork is not null) return; // connection/transaction are owned and disposed by the UnitOfWork
        try { if (_transaction is not null) await _transaction.DisposeAsync().ConfigureAwait(false); }
        finally { if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false); }
    }
}

public sealed class SessionTransaction : IAsyncDisposable
{
    private readonly DataSession session;
    private readonly DbTransaction transaction;
    private bool _completed;

    internal SessionTransaction(DataSession session, DbTransaction transaction)
    {
        this.session = session;
        this.transaction = transaction;
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_completed) throw new InvalidOperationException("Transaction has completed.");
        await session.CompleteAsync(transaction, true, ct).ConfigureAwait(false);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_completed || !session.OwnsTransaction(transaction)) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await session.CompleteAsync(transaction, false, cleanup.Token).ConfigureAwait(false); }
        finally { _completed = !session.OwnsTransaction(transaction); }
    }
}
