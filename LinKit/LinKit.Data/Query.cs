using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data;

/// <summary>Join flavor supported by <see cref="Query{TResult}.Join{TEntity}"/>.</summary>
public enum JoinKind
{
    /// <summary>Keeps only rows that have a match on both sides.</summary>
    Inner,

    /// <summary>Keeps every row of the left side, filling unmatched right columns with <c>NULL</c>.</summary>
    Left,
}

internal sealed record TableSource(TableModel Model, string Alias);

internal sealed record JoinPlan(TableSource Source, SqlNode On, JoinKind Kind);

internal sealed record SelectPlan(
    TableSource From,
    IReadOnlyList<SqlNode> Projection,
    IReadOnlyList<JoinPlan> Joins,
    SqlNode? Where,
    IReadOnlyList<Ordering> Order,
    IReadOnlyList<SqlNode> Group,
    SqlNode? Having,
    int? Skip = null,
    int? Take = null,
    bool Distinct = false
);

/// <summary>Implemented by generated projections so a query can select a subset of columns.</summary>
/// <typeparam name="T">Row type produced by the projection.</typeparam>
public interface IProjection<T>
{
    /// <summary>Returns the materialised projection.</summary>
    /// <returns>The projection itself.</returns>
    Projection<T> Build();
}

/// <summary>
/// Column list plus row reader that together describe what a query selects.
/// </summary>
/// <typeparam name="T">Row type produced by the projection.</typeparam>
/// <param name="columns">Expressions rendered as the <c>SELECT</c> list.</param>
/// <param name="read">Maps the current row of the reader to <typeparamref name="T"/>.</param>
public sealed class Projection<T>(IReadOnlyList<SqlExpression> columns, Func<DbDataReader, T> read)
    : IProjection<T>
{
    /// <summary>Expressions rendered as the <c>SELECT</c> list, in result order.</summary>
    public IReadOnlyList<SqlExpression> Columns { get; } = columns.ToArray();

    /// <summary>Reads one row of the result set.</summary>
    public Func<DbDataReader, T> Read { get; } = read;

    /// <summary>Returns the materialised projection.</summary>
    /// <returns>The projection itself.</returns>
    public Projection<T> Build() => this;
}

/// <summary>
/// Base class of the generated table accessors: schema metadata, SQL alias and the default projection of an entity.
/// </summary>
/// <typeparam name="TEntity">Entity type.</typeparam>
public abstract class Table<TEntity>
    where TEntity : class
{
    /// <summary>Physical table and column layout of the entity.</summary>
    public TableModel Model { get; }

    /// <summary>Alias used for this occurrence of the table in the generated SQL.</summary>
    public string Alias { get; }

    /// <summary>Default projection that selects every mapped column.</summary>
    public abstract Projection<TEntity> Projection { get; }

    /// <summary>Creates a table accessor for the given model and alias.</summary>
    /// <param name="model">Physical table layout.</param>
    /// <param name="alias">Alias used in the generated SQL; must not be blank.</param>
    protected Table(TableModel model, string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        Model = model;
        Alias = alias;
    }
}

/// <summary>
/// Immutable query builder that renders SQL for one entity or projection and executes it on a
/// <see cref="DataSession"/>. Every clause returns a new instance, so a query can be safely reused as a base.
/// </summary>
/// <typeparam name="TResult">Row type the query produces.</typeparam>
public sealed class Query<TResult>
{
    private readonly DataSession _session;
    private readonly SelectPlan _plan;
    private readonly Func<DbDataReader, TResult> _read;
    internal DataSession Session => _session;

    internal Query(DataSession session, SelectPlan plan, Func<DbDataReader, TResult> read)
    {
        _session = session;
        _plan = plan;
        _read = read;
    }

    internal static Query<TEntity> From<TEntity>(DataSession session, Table<TEntity> table)
        where TEntity : class =>
        new(
            session,
            new(
                new(table.Model, table.Alias),
                table.Projection.Columns.Select(column => column.Node).ToArray(),
                [],
                null,
                [],
                [],
                null
            ),
            table.Projection.Read
        );

    private Query<TResult> With(SelectPlan plan) => new(_session, plan, _read);

    private void BeforePagination()
    {
        if (_plan.Skip is not null || _plan.Take is not null)
            throw new NotSupportedException(
                "Apply filters, joins, grouping and ordering before pagination. Nested pagination requires an explicit subquery."
            );
    }

    /// <summary>Adds a filter; repeated calls are combined with <c>AND</c>.</summary>
    /// <param name="predicate">Condition to add.</param>
    /// <returns>A new query with the filter applied.</returns>
    /// <exception cref="NotSupportedException">Pagination was already applied.</exception>
    public Query<TResult> Where(Predicate predicate)
    {
        BeforePagination();
        return With(
            _plan with
            {
                Where = _plan.Where is null
                    ? predicate.Node
                    : new BinaryNode(_plan.Where, "AND", predicate.Node),
            }
        );
    }

    /// <summary>Joins another table.</summary>
    /// <typeparam name="TEntity">Entity type of the joined table.</typeparam>
    /// <param name="table">Table to join, carrying the alias used in the join condition.</param>
    /// <param name="on">Join condition; put the tenant and soft-delete predicates of the joined table here so they
    /// are pushed into <c>ON</c> and keep a left join semantics.</param>
    /// <param name="kind">Join flavor; defaults to <see cref="JoinKind.Inner"/>.</param>
    /// <returns>A new query with the join applied.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a defined value.</exception>
    public Query<TResult> Join<TEntity>(
        Table<TEntity> table,
        Predicate on,
        JoinKind kind = JoinKind.Inner
    )
        where TEntity : class
    {
        BeforePagination();
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return With(
            _plan with
            {
                Joins = [.. _plan.Joins, new(new(table.Model, table.Alias), on.Node, kind)],
            }
        );
    }

    /// <summary>Replaces the selected columns and the row reader.</summary>
    /// <typeparam name="T">Row type produced by the projection.</typeparam>
    /// <param name="projection">Columns to select and the reader that maps them.</param>
    /// <returns>A new query that produces <typeparamref name="T"/>.</returns>
    /// <exception cref="ArgumentException">The projection selects no column.</exception>
    public Query<T> Select<T>(Projection<T> projection)
    {
        BeforePagination();
        if (projection.Columns.Count == 0)
            throw new ArgumentException("Projection must contain columns.", nameof(projection));
        return new(
            _session,
            _plan with
            {
                Projection = projection.Columns.Select(column => column.Node).ToArray(),
            },
            projection.Read
        );
    }

    /// <summary>Replaces the ordering.</summary>
    /// <param name="order">Ordering terms, in priority order.</param>
    /// <returns>A new query with the ordering applied.</returns>
    public Query<TResult> OrderBy(params Ordering[] order)
    {
        BeforePagination();
        return With(_plan with { Order = order.ToArray() });
    }

    /// <summary>Appends a tie-breaker ordering.</summary>
    /// <param name="order">Ordering term added after the existing ones.</param>
    /// <returns>A new query with the extra ordering.</returns>
    /// <exception cref="InvalidOperationException">No ordering was applied yet.</exception>
    public Query<TResult> ThenBy(Ordering order)
    {
        BeforePagination();
        if (_plan.Order.Count == 0)
            throw new InvalidOperationException("ThenBy requires OrderBy.");
        return With(_plan with { Order = [.. _plan.Order, order] });
    }

    /// <summary>Groups by the given expressions.</summary>
    /// <param name="columns">Grouping key expressions.</param>
    /// <returns>A new query with the grouping applied.</returns>
    public Query<TResult> GroupBy(params SqlExpression[] columns)
    {
        BeforePagination();
        return With(_plan with { Group = columns.Select(column => column.Node).ToArray() });
    }

    /// <summary>Filters the grouped rows; repeated calls are combined with <c>AND</c>.</summary>
    /// <param name="predicate">Condition applied to the groups.</param>
    /// <returns>A new query with the filter applied.</returns>
    /// <exception cref="InvalidOperationException">No grouping was applied yet.</exception>
    public Query<TResult> Having(Predicate predicate)
    {
        BeforePagination();
        if (_plan.Group.Count == 0)
            throw new InvalidOperationException("Having requires GroupBy.");
        return With(
            _plan with
            {
                Having = _plan.Having is null
                    ? predicate.Node
                    : new BinaryNode(_plan.Having, "AND", predicate.Node),
            }
        );
    }

    /// <summary>Selects distinct rows.</summary>
    /// <returns>A new query that emits <c>SELECT DISTINCT</c>.</returns>
    public Query<TResult> Distinct()
    {
        BeforePagination();
        return With(_plan with { Distinct = true });
    }

    /// <summary>Skips rows; call it before <see cref="Take"/>.</summary>
    /// <param name="count">Rows to skip; accumulated across calls.</param>
    /// <returns>A new query with the offset applied.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <exception cref="NotSupportedException"><see cref="Take"/> was already applied.</exception>
    public Query<TResult> Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_plan.Take is not null)
            throw new NotSupportedException(
                "Skip after Take requires a subquery. Use Skip before Take."
            );
        return With(_plan with { Skip = checked((_plan.Skip ?? 0) + count) });
    }

    /// <summary>Limits the number of returned rows.</summary>
    /// <param name="count">Maximum rows; the smallest requested value wins.</param>
    /// <returns>A new query with the limit applied.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public Query<TResult> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return With(_plan with { Take = Math.Min(_plan.Take ?? int.MaxValue, count) });
    }

    /// <summary>Turns the query into an <c>EXISTS</c> predicate so it can be used inside another query.</summary>
    /// <returns>A predicate that is true when the query returns at least one row.</returns>
    public Predicate Exists() =>
        new(
            new ExistsNode(
                _plan with
                {
                    Order = _plan.Skip is null && _plan.Take is null ? [] : _plan.Order,
                },
                false
            )
        );

    /// <summary>Renders the <c>SELECT</c> statement without executing it.</summary>
    /// <returns>The command text and its bound arguments.</returns>
    public CommandSpec ToCommand()
    {
        var writer = new SqlWriter(_session.Dialect, _session.TenantId);
        return new(writer.Select(_plan), writer.Arguments);
    }

    /// <summary>Executes the query and materialises every row.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The materialised rows.</returns>
    public Task<List<TResult>> ToListAsync(CancellationToken ct = default) =>
        _session.QueryAsync(ToCommand(), _read, ct);

    /// <summary>Executes the query and streams the rows without buffering the result set.</summary>
    /// <param name="ct">Token used to cancel the enumeration.</param>
    /// <returns>The rows in result order.</returns>
    public IAsyncEnumerable<TResult> StreamAsync(CancellationToken ct = default) =>
        _session.StreamAsync(ToCommand(), _read, ct);

    /// <summary>Renders the set-based <c>UPDATE</c> for the rows this query matches.</summary>
    /// <param name="assignments">Column assignments to apply.</param>
    /// <param name="expectedVersion">Original concurrency version, or <see langword="null"/> to skip the check.</param>
    /// <returns>The command text and its bound arguments.</returns>
    public CommandSpec UpdateCommand(
        IReadOnlyList<Assignment> assignments,
        long? expectedVersion = null
    )
    {
        var writer = new SqlWriter(_session.Dialect, _session.TenantId);
        return new(writer.Mutate(_plan, assignments, false, expectedVersion), writer.Arguments);
    }

    /// <summary>Renders the set-based <c>DELETE</c> for the rows this query matches.</summary>
    /// <param name="expectedVersion">Original concurrency version, or <see langword="null"/> to skip the check.</param>
    /// <returns>The command text and its bound arguments.</returns>
    public CommandSpec DeleteCommand(long? expectedVersion = null)
    {
        var writer = new SqlWriter(_session.Dialect, _session.TenantId);
        return new(writer.Mutate(_plan, [], true, expectedVersion), writer.Arguments);
    }

    /// <summary>Updates every matching row in one statement.</summary>
    /// <param name="assignments">Column assignments to apply.</param>
    /// <param name="expectedVersion">Original concurrency version, or <see langword="null"/> to skip the check.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The number of affected rows.</returns>
    public Task<int> ExecuteUpdateAsync(
        IReadOnlyList<Assignment> assignments,
        long? expectedVersion = null,
        CancellationToken ct = default
    ) => _session.ExecuteAsync(UpdateCommand(assignments, expectedVersion), ct);

    /// <summary>Deletes every matching row in one statement, honoring soft delete when the entity declares it.</summary>
    /// <param name="expectedVersion">Original concurrency version, or <see langword="null"/> to skip the check.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The number of affected rows.</returns>
    public Task<int> ExecuteDeleteAsync(
        long? expectedVersion = null,
        CancellationToken ct = default
    ) => _session.ExecuteAsync(DeleteCommand(expectedVersion), ct);

    /// <summary>
    /// Renders the existence probe this query executes for <see cref="AnyAsync"/>: one <c>SELECT 1</c> row, so the
    /// provider can stop at the first match. Exposed so callers can log or instrument the exact statement.
    /// </summary>
    public CommandSpec AnyCommand()
    {
        var writer = new SqlWriter(_session.Dialect, _session.TenantId);
        return new(writer.Probe(_plan), writer.Arguments);
    }

    /// <summary>
    /// Renders the projection-free count this query executes for <see cref="CountAsync"/>.
    /// </summary>
    public CommandSpec CountCommand()
    {
        var writer = new SqlWriter(_session.Dialect, _session.TenantId);
        return new(writer.Count(_plan), writer.Arguments);
    }

    /// <summary>
    /// Renders a single aggregate over the filtered rows, for example <c>AggregateCommand(Sql.Sum(columns.Price))</c>.
    /// </summary>
    public CommandSpec AggregateCommand<TValue>(SqlExpression<TValue> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var writer = new SqlWriter(_session.Dialect, _session.TenantId);
        return new(writer.Scalar(_plan, expression.Node), writer.Arguments);
    }

    /// <summary>
    /// Returns the first row or <c>null</c>. The statement is limited to one row, so the provider stops after the
    /// first match and no list is allocated to hold it.
    /// </summary>
    public Task<TResult?> FirstOrDefaultAsync(CancellationToken ct = default) =>
        _session.QueryFirstAsync(Take(1).ToCommand(), _read, ct);

    /// <summary>Returns the first row.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The first row.</returns>
    /// <exception cref="InvalidOperationException">The query returned no row.</exception>
    public async Task<TResult> FirstAsync(CancellationToken ct = default)
    {
        var item = await _session
            .QueryFirstAsync(Take(1).ToCommand(), _read, ct)
            .ConfigureAwait(false);
        return item is null
            ? throw new InvalidOperationException("Sequence contains no elements.")
            : item;
    }

    /// <summary>
    /// Returns the single row or <c>null</c>. At most two rows are read, which is all that is needed to detect the
    /// <see cref="InvalidOperationException"/> case without fetching the remaining rows.
    /// </summary>
    public async Task<TResult?> SingleOrDefaultAsync(CancellationToken ct = default)
    {
        var items = await _session
            .QueryTopAsync(Take(2).ToCommand(), _read, 2, ct)
            .ConfigureAwait(false);
        return items.Count switch
        {
            0 => default,
            1 => items[0],
            _ => throw new InvalidOperationException("Sequence contains more than one element."),
        };
    }

    /// <summary>Returns the only row.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The single row.</returns>
    /// <exception cref="InvalidOperationException">The query returned no row, or more than one.</exception>
    public async Task<TResult> SingleAsync(CancellationToken ct = default)
    {
        var items = await _session
            .QueryTopAsync(Take(2).ToCommand(), _read, 2, ct)
            .ConfigureAwait(false);
        return items.Count switch
        {
            0 => throw new InvalidOperationException("Sequence contains no elements."),
            1 => items[0],
            _ => throw new InvalidOperationException("Sequence contains more than one element."),
        };
    }

    /// <summary>
    /// Counts the rows the query matches. The projection is never rendered: without grouping or distinct the
    /// statement is a plain <c>SELECT COUNT(*) ... WHERE ...</c>, which lets the provider answer from the index
    /// instead of materialising every projected column first.
    /// </summary>
    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        var value = await _session.ScalarAsync(CountCommand(), ct).ConfigureAwait(false);
        return value is null ? 0L : DbValues.ConvertTo<long>(value);
    }

    /// <summary>
    /// Answers whether the query matches any row. Renders <c>SELECT 1 ... LIMIT 1</c> (or the dialect equivalent)
    /// rather than counting the whole matching set.
    /// </summary>
    public async Task<bool> AnyAsync(CancellationToken ct = default)
    {
        if (_plan.Take == 0)
            return false;
        return await _session.ReadExistsAsync(AnyCommand(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates a single aggregate over the filtered rows, for example
    /// <c>await query.ScalarAsync(Sql.Sum(columns.Price))</c>. Grouping, distinct, ordering and paging are rejected
    /// because an aggregate over a page is not the aggregate of the filtered set.
    /// </summary>
    public async Task<TValue?> ScalarAsync<TValue>(
        SqlExpression<TValue> expression,
        CancellationToken ct = default
    )
    {
        var value = await _session
            .ScalarAsync(AggregateCommand(expression), ct)
            .ConfigureAwait(false);
        return value is null ? default : DbValues.ConvertTo<TValue>(value);
    }

    /// <summary>Returns the sum of the expression over the matched rows.</summary>
    /// <param name="expression">Numeric expression to add up.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The sum, or <see langword="null"/> when no row matches.</returns>
    public Task<decimal?> SumAsync(
        SqlExpression<decimal> expression,
        CancellationToken ct = default
    ) => ScalarAsync(Sql.Sum(expression), ct);

    /// <summary>Returns the average of the expression over the matched rows.</summary>
    /// <param name="expression">Numeric expression to average.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The average, or <see langword="null"/> when no row matches.</returns>
    public Task<decimal?> AverageAsync(
        SqlExpression<decimal> expression,
        CancellationToken ct = default
    ) => ScalarAsync(Sql.Average(expression), ct);

    /// <summary>Returns the smallest value of the expression over the matched rows.</summary>
    /// <typeparam name="TValue">Value type of the expression.</typeparam>
    /// <param name="expression">Expression to aggregate.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The minimum, or <see langword="null"/> when no row matches.</returns>
    public Task<TValue?> MinAsync<TValue>(
        SqlExpression<TValue> expression,
        CancellationToken ct = default
    )
        where TValue : struct => ScalarAsync(Sql.Min(expression), ct);

    /// <summary>Returns the largest value of the expression over the matched rows.</summary>
    /// <typeparam name="TValue">Value type of the expression.</typeparam>
    /// <param name="expression">Expression to aggregate.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The maximum, or <see langword="null"/> when no row matches.</returns>
    public Task<TValue?> MaxAsync<TValue>(
        SqlExpression<TValue> expression,
        CancellationToken ct = default
    )
        where TValue : struct => ScalarAsync(Sql.Max(expression), ct);

    /// <summary>Returns the lexicographic minimum of a string expression.</summary>
    /// <param name="expression">String expression to aggregate.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The minimum, or <see langword="null"/> when no row matches.</returns>
    public Task<string?> MinAsync(ISqlValue<string> expression, CancellationToken ct = default) =>
        ScalarAsync(Sql.Min(expression), ct);

    /// <summary>Returns the lexicographic maximum of a string expression.</summary>
    /// <param name="expression">String expression to aggregate.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The maximum, or <see langword="null"/> when no row matches.</returns>
    public Task<string?> MaxAsync(ISqlValue<string> expression, CancellationToken ct = default) =>
        ScalarAsync(Sql.Max(expression), ct);

    /// <summary>Runs the count and the page query and returns them together.</summary>
    /// <param name="pageNumber">One-based page number; values below one are clamped to the first page.</param>
    /// <param name="pageSize">Rows per page; values below one use 10.</param>
    /// <param name="ct">Token used to cancel the commands.</param>
    /// <returns>The page and the total row count.</returns>
    /// <remarks>An empty result set skips the page query, and the offset is computed in 64-bit space so a very large
    /// page number clamps to an empty page instead of overflowing.</remarks>
    public async Task<PagedResult<TResult>> ToPagedListAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct = default
    )
    {
        if (pageNumber < 1)
            pageNumber = 1;
        if (pageSize < 1)
            pageSize = 10;
        var totalCount = await CountAsync(ct).ConfigureAwait(false);
        // The offset is computed in 64-bit space: a large page number must clamp to an empty page instead of
        // overflowing into a negative OFFSET.
        var offset = ((long)pageNumber - 1) * pageSize;
        var skip = offset > int.MaxValue ? int.MaxValue : (int)offset;
        var items =
            totalCount == 0
                ? (IReadOnlyList<TResult>)Array.Empty<TResult>()
                : await Skip(skip).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        return new PagedResult<TResult>
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize,
        };
    }
}
