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

    public static Activity? StartActivity(string name, ActivityKind kind) =>
        Source.StartActivity(name, kind);
}

/// <summary>
/// Thrown when a commit failed in a way that leaves the outcome unknown, for example a lost connection during
/// <c>COMMIT</c>. The transaction may or may not have been applied, so the operation must be reconciled by the caller.
/// </summary>
public sealed class CommitOutcomeUnknownException(Exception inner)
    : Exception("Transaction commit outcome is unknown.", inner);

/// <summary>
/// Owns one connection (and optionally one transaction) and executes commands against it.
/// </summary>
/// <remarks>
/// <para>
/// The session is scoped: it opens the connection lazily on the first command and keeps it open until the session is
/// disposed, so several commands share one round trip. All operations are instrumented with an
/// <see cref="ActivitySource"/> named <c>LinKit.Data</c>.
/// </para>
/// <para>
/// Concurrent operations on one session throw unless <see cref="AllowConcurrentOperations"/> is enabled. Commands that
/// can be replayed are routed through <see cref="IdempotentExecutor"/>; mutations are never retried.
/// </para>
/// </remarks>
public sealed class DataSession(
    Func<DbConnection> connectionFactory,
    SqlDialect dialect,
    string? tenantId = null,
    Func<DbCommand, ValueTask>? configureCommand = null
) : IAsyncDisposable
{
    private DbConnection? _connection;
    private DbTransaction? _transaction;
    private bool _opened;
    private bool _disposed;
    private bool _faulted;
    private int _busy;
    private SemaphoreSlim? _gate;

    /// <summary>Dialect used to quote identifiers, bind parameters and render provider specific SQL.</summary>
    public SqlDialect Dialect { get; } = dialect;

    /// <summary>Tenant of the current scope, or <see langword="null"/> when the scope is not tenant-scoped.</summary>
    public string? TenantId { get; } = tenantId;

    /// <summary>Whether a transaction, local or borrowed from the scoped unit of work, is active.</summary>
    public bool HasTransaction => ActiveTransaction is not null;

    /// <summary>
    /// Upper bound on the rows a single generated range statement (multi-row insert, batched update,
    /// chunked delete) may carry. Each statement is additionally clamped by the provider parameter
    /// limit and by <see cref="SqlDialect.MaxRowsPerInsertStatement"/>.
    /// </summary>
    public int BatchSize { get; init; } = LinKitDataOptions.DefaultBatchSize;

    /// <summary>
    /// Per-command timeout in seconds. A value of 0 means "no limit" (the provider default).
    /// </summary>
    public int CommandTimeout { get; init; } = LinKitDataOptions.DefaultCommandTimeout;

    /// <summary>Isolation level used by <see cref="BeginTransactionAsync"/> when the caller does not specify one.</summary>
    public IsolationLevel DefaultIsolationLevel { get; init; } = IsolationLevel.Unspecified;

    /// <summary>
    /// When true, concurrent operations on one session are serialized by a gate instead of throwing.
    /// Parallel handler queries then share a single connection, which is what most request scopes want;
    /// the alternative is one connection per operation.
    /// </summary>
    public bool AllowConcurrentOperations { get; init; }

    /// <summary>
    /// Optional policy wrapper applied only to idempotent operations (queries and the explicit
    /// <c>*Idempotent*</c> methods). Mutations are never retried by the session itself.
    /// </summary>
    public IDataOperationExecutor? IdempotentExecutor { get; init; }

    /// <summary>
    /// When set, the session borrows the scoped UnitOfWork's connection and transaction instead of opening its own,
    /// so business writes and outbox rows share one transaction. The UnitOfWork keeps sole ownership/disposal.
    /// </summary>
    internal UnitOfWork? SharedUnitOfWork { get; init; }

    private DbTransaction? ActiveTransaction =>
        SharedUnitOfWork is { } shared ? shared.CurrentTransaction : _transaction;

    internal bool OwnsTransaction(DbTransaction transaction) =>
        !_disposed && ReferenceEquals(ActiveTransaction, transaction);

    /// <summary>Starts a query over the given table.</summary>
    /// <typeparam name="TEntity">Entity type mapped by <paramref name="table"/>.</typeparam>
    /// <param name="table">Generated table accessor that carries the schema metadata of the entity.</param>
    /// <returns>A query builder bound to this session.</returns>
    public Query<TEntity> From<TEntity>(Table<TEntity> table)
        where TEntity : class => Query<TEntity>.From(this, table);

    // The gate is created on first use because AllowConcurrentOperations is an init-only property.
    private SemaphoreSlim? Gate
    {
        get
        {
            if (!AllowConcurrentOperations)
                return null;
            return _gate ?? CreateGate();
        }
    }

    private SemaphoreSlim CreateGate()
    {
        var created = new SemaphoreSlim(1, 1);
        return Interlocked.CompareExchange(ref _gate, created, null) ?? created;
    }

    private async ValueTask EnterAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted)
            throw new InvalidOperationException(
                "Session is unusable after transaction completion failed."
            );
        if (Gate is not { } gate)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                throw new InvalidOperationException(
                    "Concurrent operations on one DataSession are not supported. Use separate sessions, or enable "
                        + "LinKitDataOptions.UseConcurrentOperations() to serialize them on one connection."
                );
            return;
        }
        await gate.WaitAsync(ct).ConfigureAwait(false);
    }

    private void Exit()
    {
        if (_gate is { } gate)
            gate.Release();
        else
            Volatile.Write(ref _busy, 0);
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
            if (_opened)
                throw new InvalidOperationException(
                    "The session connection was lost or closed. Create a new session; transaction and advisory-lock state cannot be recovered by reconnecting."
                );
            await _connection.OpenAsync(ct).ConfigureAwait(false);
        }
        _opened = true;
        return _connection;
    }

    private async Task<DbCommand> PrepareAsync(CommandSpec spec, CancellationToken ct)
    {
        if (Dialect.Kind == DatabaseKind.Oracle && configureCommand is null)
            throw new InvalidOperationException(
                "Oracle requires configureCommand to set OracleCommand.BindByName = true."
            );
        if (spec.Arguments.Count > Dialect.MaxParameters)
            throw new NotSupportedException(
                "Parameter limit exceeded. Split the operation into batches."
            );
        var connection = await OpenAsync(ct).ConfigureAwait(false);
        var command = connection.CreateCommand();
        try
        {
            command.Transaction = ActiveTransaction;
            if (CommandTimeout > 0)
                command.CommandTimeout = CommandTimeout;
            command.CommandText = spec.Sql;
            if (configureCommand is not null)
                await configureCommand(command).ConfigureAwait(false);
            for (var index = 0; index < spec.Arguments.Count; index++)
            {
                Dialect.Bind(
                    command,
                    index,
                    spec.Arguments[index].Value,
                    spec.Arguments[index].Column
                );
            }
            return command;
        }
        catch
        {
            await command.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Executes a statement and returns the number of affected rows.</summary>
    /// <param name="spec">SQL text and its bound arguments.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The number of affected rows.</returns>
    public async Task<int> ExecuteAsync(CommandSpec spec, CancellationToken ct = default)
    {
        await EnterAsync(ct).ConfigureAwait(false);
        using var activity = Activities.StartActivity("db.execute", ActivityKind.Client);
        activity?.SetTag("db.system.name", Dialect.Kind.ToString());
        try
        {
            await using var command = await PrepareAsync(spec, ct).ConfigureAwait(false);
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
            throw;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Executes a statement and returns the first column of the first row.</summary>
    /// <param name="spec">SQL text and its bound arguments.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The scalar value, or <see langword="null"/> when the database returned <c>NULL</c> or no row.</returns>
    public async Task<object?> ScalarAsync(CommandSpec spec, CancellationToken ct = default)
    {
        await EnterAsync(ct).ConfigureAwait(false);
        using var activity = Activities.StartActivity("db.query", ActivityKind.Client);
        activity?.SetTag("db.system.name", Dialect.Kind.ToString());
        try
        {
            await using var command = await PrepareAsync(spec, ct).ConfigureAwait(false);
            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is DBNull ? null : result;
        }
        catch (Exception error)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
            throw;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Answers an existence probe without materialising a row: the statement is rendered as a single
    /// <c>SELECT 1</c> row, so the provider can stop at the first match instead of counting the whole result set.
    /// Safe to replay through the configured <see cref="IdempotentExecutor"/> because nothing is mutated.
    /// </summary>
    public Task<bool> ReadExistsAsync(CommandSpec spec, CancellationToken ct = default) =>
        RunIdempotentAsync(
            async token => await ScalarAsync(spec, token).ConfigureAwait(false) is not null,
            ct
        );

    /// <summary>
    /// <see cref="ScalarAsync"/> for statements that are safe to replay (a SELECT or an idempotent upsert check).
    /// The configured <see cref="IdempotentExecutor"/> policy is applied.
    /// </summary>
    public Task<object?> ScalarIdempotentAsync(CommandSpec spec, CancellationToken ct = default) =>
        RunIdempotentAsync(token => ScalarAsync(spec, token), ct);

    /// <summary>
    /// <see cref="ExecuteAsync"/> for statements that are safe to replay. Never use it for an INSERT that
    /// allocates a key or for a non-idempotent UPDATE/DELETE.
    /// </summary>
    public Task<int> ExecuteIdempotentAsync(CommandSpec spec, CancellationToken ct = default) =>
        RunIdempotentAsync(token => ExecuteAsync(spec, token), ct);

    private Task<T> RunIdempotentAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct
    ) => IdempotentExecutor is { } executor ? executor.ExecuteAsync(operation, ct) : operation(ct);

    /// <summary>
    /// Reads the first column of every returned row. Used to collect the generated keys of a multi-row
    /// <c>OUTPUT INSERTED</c> / <c>RETURNING</c> insert in statement order.
    /// </summary>
    internal async Task<List<object>> ScalarListAsync(
        CommandSpec spec,
        CancellationToken ct = default
    )
    {
        await EnterAsync(ct).ConfigureAwait(false);
        using var activity = Activities.StartActivity("db.query", ActivityKind.Client);
        activity?.SetTag("db.system.name", Dialect.Kind.ToString());
        try
        {
            await using var command = await PrepareAsync(spec, ct).ConfigureAwait(false);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var results = new List<object>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                    results.Add(reader.GetValue(0));
            }
            return results;
        }
        catch (Exception error)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
            throw;
        }
        finally
        {
            Exit();
        }
    }

    internal async Task<object> InsertIdentityAsync(
        CommandSpec spec,
        ColumnModel key,
        CancellationToken ct
    )
    {
        if (Dialect.Kind != DatabaseKind.Oracle)
            return await ScalarAsync(spec, ct).ConfigureAwait(false)
                ?? throw new DataMappingException("INSERT returned no generated key.");
        await EnterAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = await PrepareAsync(spec, ct).ConfigureAwait(false);
            var parameter = command.CreateParameter();
            parameter.ParameterName = ":generated_key";
            parameter.DbType = key.Type;
            parameter.Direction = ParameterDirection.Output;
            command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return parameter.Value is null or DBNull
                ? throw new DataMappingException("INSERT returned no key.")
                : parameter.Value;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Streams the rows of a query without buffering the result set.</summary>
    /// <typeparam name="T">Row type produced by <paramref name="read"/>.</typeparam>
    /// <param name="spec">SQL text and its bound arguments.</param>
    /// <param name="read">Maps the current row of the reader to a value.</param>
    /// <param name="ct">Token used to cancel the enumeration.</param>
    /// <returns>The rows in statement order.</returns>
    /// <remarks>The connected command stays open for the duration of the enumeration, so finish or dispose it before
    /// starting another operation on the same session.</remarks>
    public async IAsyncEnumerable<T> StreamAsync<T>(
        CommandSpec spec,
        Func<DbDataReader, T> read,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await EnterAsync(ct).ConfigureAwait(false);
        using var activity = Activities.StartActivity("db.query", ActivityKind.Client);
        activity?.SetTag("db.system.name", Dialect.Kind.ToString());
        try
        {
            await using var command = await PrepareAsync(spec, ct).ConfigureAwait(false);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                yield return read(reader);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Materializes a query. Because the whole result is buffered, a transient failure cannot leave a partially
    /// consumed stream behind, so the configured <see cref="IdempotentExecutor"/> policy may safely replay it.
    /// </summary>
    public Task<List<T>> QueryAsync<T>(
        CommandSpec spec,
        Func<DbDataReader, T> read,
        CancellationToken ct = default
    ) => RunIdempotentAsync(token => ReadRowsAsync(spec, read, int.MaxValue, token), ct);

    /// <summary>
    /// Materializes at most <paramref name="maxRows"/> rows. Used by <c>First</c>/<c>Single</c> terminals so the
    /// provider stops reading once the answer is known instead of streaming a result set nobody consumes.
    /// </summary>
    public Task<List<T>> QueryTopAsync<T>(
        CommandSpec spec,
        Func<DbDataReader, T> read,
        int maxRows,
        CancellationToken ct = default
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        return RunIdempotentAsync(token => ReadRowsAsync(spec, read, maxRows, token), ct);
    }

    /// <summary>
    /// Returns the first row and discards the rest of the result set. Unlike <see cref="QueryTopAsync"/> this never
    /// allocates a list, which is what the <c>First</c>/<c>Single</c> terminals want for the single-row case.
    /// </summary>
    public Task<T?> QueryFirstAsync<T>(
        CommandSpec spec,
        Func<DbDataReader, T> read,
        CancellationToken ct = default
    ) => RunIdempotentAsync(token => ReadFirstAsync(spec, read, token), ct);

    private async Task<List<T>> ReadRowsAsync<T>(
        CommandSpec spec,
        Func<DbDataReader, T> read,
        int maxRows,
        CancellationToken ct
    )
    {
        await EnterAsync(ct).ConfigureAwait(false);
        using var activity = Activities.StartActivity("db.query", ActivityKind.Client);
        activity?.SetTag("db.system.name", Dialect.Kind.ToString());
        try
        {
            await using var command = await PrepareAsync(spec, ct).ConfigureAwait(false);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var results = new List<T>();
            while (results.Count < maxRows && await reader.ReadAsync(ct).ConfigureAwait(false))
                results.Add(read(reader));
            return results;
        }
        catch (Exception error)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
            throw;
        }
        finally
        {
            Exit();
        }
    }

    private async Task<T?> ReadFirstAsync<T>(
        CommandSpec spec,
        Func<DbDataReader, T> read,
        CancellationToken ct
    )
    {
        await EnterAsync(ct).ConfigureAwait(false);
        using var activity = Activities.StartActivity("db.query", ActivityKind.Client);
        activity?.SetTag("db.system.name", Dialect.Kind.ToString());
        try
        {
            await using var command = await PrepareAsync(spec, ct).ConfigureAwait(false);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? read(reader) : default;
        }
        catch (Exception error)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
            throw;
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Executes a statement written as raw SQL text.</summary>
    /// <param name="sql">SQL text with dialect placeholders such as <c>@p0</c>.</param>
    /// <param name="arguments">Values bound to the placeholders in order.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The number of affected rows.</returns>
    public Task<int> ExecuteSqlAsync(
        string sql,
        IReadOnlyList<SqlArgument>? arguments = null,
        CancellationToken ct = default
    ) => ExecuteAsync(new CommandSpec(sql, arguments ?? Array.Empty<SqlArgument>()), ct);

    /// <summary>Executes a raw SQL statement and returns the first column of the first row.</summary>
    /// <param name="sql">SQL text with dialect placeholders such as <c>@p0</c>.</param>
    /// <param name="arguments">Values bound to the placeholders in order.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The scalar value, or <see langword="null"/> when the database returned <c>NULL</c> or no row.</returns>
    public Task<object?> ScalarSqlAsync(
        string sql,
        IReadOnlyList<SqlArgument>? arguments = null,
        CancellationToken ct = default
    ) => ScalarAsync(new CommandSpec(sql, arguments ?? Array.Empty<SqlArgument>()), ct);

    /// <summary>Executes a raw SQL query and maps every row.</summary>
    /// <typeparam name="T">Row type produced by <paramref name="read"/>.</typeparam>
    /// <param name="sql">SQL text with dialect placeholders such as <c>@p0</c>.</param>
    /// <param name="read">Maps the current row of the reader to a value.</param>
    /// <param name="arguments">Values bound to the placeholders in order.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The materialised rows.</returns>
    public Task<List<T>> QuerySqlAsync<T>(
        string sql,
        Func<DbDataReader, T> read,
        IReadOnlyList<SqlArgument>? arguments = null,
        CancellationToken ct = default
    ) => QueryAsync(new CommandSpec(sql, arguments ?? Array.Empty<SqlArgument>()), read, ct);

    /// <summary>Starts a transaction, or joins the scoped unit of work's transaction when one is shared.</summary>
    /// <param name="isolation">Isolation level; <see cref="IsolationLevel.Unspecified"/> uses <see cref="DefaultIsolationLevel"/>.</param>
    /// <param name="ct">Token used to cancel the operation.</param>
    /// <returns>A handle that commits on <see cref="SessionTransaction.CommitAsync"/> and rolls back on dispose.</returns>
    /// <exception cref="InvalidOperationException">A transaction is already active; nesting is not supported.</exception>
    public async Task<SessionTransaction> BeginTransactionAsync(
        IsolationLevel isolation = IsolationLevel.Unspecified,
        CancellationToken ct = default
    )
    {
        await EnterAsync(ct).ConfigureAwait(false);
        try
        {
            if (ActiveTransaction is not null)
                throw new InvalidOperationException("Nested transactions are not supported.");
            var effectiveIsolation =
                isolation == IsolationLevel.Unspecified ? DefaultIsolationLevel : isolation;
            if (SharedUnitOfWork is { } sharedUow)
            {
                var shared = await sharedUow
                    .BeginTransactionAsync(effectiveIsolation, ct)
                    .ConfigureAwait(false);
                _connection = shared.Connection;
                _opened = true;
                return new SessionTransaction(this, shared);
            }
            var connection = await OpenAsync(ct).ConfigureAwait(false);
            _transaction = await connection
                .BeginTransactionAsync(effectiveIsolation, ct)
                .ConfigureAwait(false);
            return new SessionTransaction(this, _transaction);
        }
        finally
        {
            Exit();
        }
    }

    internal async Task CompleteAsync(DbTransaction expected, bool commit, CancellationToken ct)
    {
        await EnterAsync(ct).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(ActiveTransaction, expected))
                throw new InvalidOperationException("Transaction is no longer active.");
            if (SharedUnitOfWork is { } sharedUow)
            {
                try
                {
                    if (commit)
                        await sharedUow.CommitAsync(ct).ConfigureAwait(false);
                    else
                        await sharedUow.RollbackAsync(ct).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    _faulted = true;
                    if (commit)
                        throw new CommitOutcomeUnknownException(error);
                    throw;
                }
                return;
            }
            try
            {
                if (commit)
                    await expected.CommitAsync(ct).ConfigureAwait(false);
                else
                    await expected.RollbackAsync(ct).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _faulted = true;
                if (commit)
                    throw new CommitOutcomeUnknownException(error);
                throw;
            }
            finally
            {
                _transaction = null;
                await expected.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>Releases the connection; a borrowed unit of work connection is left to its owner.</summary>
    /// <returns>A task that completes when the connection is released.</returns>
    /// <exception cref="InvalidOperationException">An operation or stream is still active on the session.</exception>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        if (Gate is { } gate)
        {
            await gate.WaitAsync().ConfigureAwait(false);
        }
        else if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "Dispose the active operation/stream before the session."
            );
        }
        _disposed = true;
        if (SharedUnitOfWork is not null)
            return; // connection/transaction are owned and disposed by the UnitOfWork
        try
        {
            if (_transaction is not null)
                await _transaction.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_connection is not null)
                await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Handle returned by <see cref="DataSession.BeginTransactionAsync"/>: commit explicitly, or dispose to roll back.
/// </summary>
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

    /// <summary>Commits the transaction.</summary>
    /// <param name="ct">Token used to cancel the commit.</param>
    /// <returns>A task that completes when the transaction is committed.</returns>
    /// <exception cref="InvalidOperationException">The transaction already completed.</exception>
    /// <exception cref="CommitOutcomeUnknownException">The commit failed; the outcome must be reconciled.</exception>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (_completed)
            throw new InvalidOperationException("Transaction has completed.");
        await session.CompleteAsync(transaction, true, ct).ConfigureAwait(false);
        _completed = true;
    }

    /// <summary>Rolls back unless the transaction already completed; failures are swallowed because the session is
    /// discarded anyway.</summary>
    /// <returns>A task that completes when the rollback attempt finishes.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_completed || !session.OwnsTransaction(transaction))
            return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await session.CompleteAsync(transaction, false, cleanup.Token).ConfigureAwait(false);
        }
        finally
        {
            _completed = !session.OwnsTransaction(transaction);
        }
    }
}
