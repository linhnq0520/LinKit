using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data;

/// <summary>
/// Optional value used by patch style updates, so "not supplied" and "set to the default value" stay distinguishable.
/// </summary>
/// <typeparam name="T">Value type.</typeparam>
public readonly struct PatchValue<T>
{
    /// <summary>Whether a value was supplied; when <see langword="false"/> the column is left untouched.</summary>
    public bool IsSet { get; }

    /// <summary>The supplied value; meaningless when <see cref="IsSet"/> is <see langword="false"/>.</summary>
    public T Value { get; }

    /// <summary>Marks the value as supplied.</summary>
    /// <param name="value">Value to write.</param>
    public PatchValue(T value)
    {
        Value = value;
        IsSet = true;
    }

    /// <summary>Wraps a value, marking it as supplied.</summary>
    /// <param name="value">Value to write.</param>
    /// <returns>A supplied patch value.</returns>
    public static implicit operator PatchValue<T>(T value) => new(value);
}

/// <summary>Describes the column changes of a set-based update, so callers can pre-build reusable patch objects.</summary>
/// <typeparam name="TColumns">Table columns type of the target table.</typeparam>
public interface IUpdate<TColumns>
{
    /// <summary>Projects the changes onto concrete column assignments.</summary>
    /// <param name="columns">Columns of the table being updated.</param>
    /// <returns>The assignments to apply.</returns>
    Assignment[] Build(TColumns columns);
}

/// <summary>
/// Minimal contract a repository implements so a query can join another table without owning its instance.
/// </summary>
/// <typeparam name="TEntity">Joined entity type.</typeparam>
/// <typeparam name="TTable">Strongly typed table columns of the joined entity.</typeparam>
/// <typeparam name="TOptional">Columns shape used for an outer join, where every column may be absent.</typeparam>
public interface IQuerySource<TEntity, TTable, TOptional>
    where TEntity : class
    where TTable : Table<TEntity>
{
    /// <summary>Session that owns the joined table; joins across sessions are rejected.</summary>
    DataSession Session { get; }

    /// <summary>Creates the joined table columns for an inner join.</summary>
    /// <param name="alias">Alias the joined table is given in the query.</param>
    /// <returns>Column members bound to <paramref name="alias"/>.</returns>
    TTable CreateTable(string alias);

    /// <summary>Creates the joined table columns for an outer join, where every column is nullable.</summary>
    /// <param name="alias">Alias the joined table is given in the query.</param>
    /// <returns>Nullable column members bound to <paramref name="alias"/>.</returns>
    TOptional CreateOptionalTable(string alias);
}

/// <summary>
/// Typed query builder that exposes a table's columns as lambda parameters instead of string names.
/// </summary>
/// <typeparam name="TResult">Row type the query currently returns.</typeparam>
/// <typeparam name="TColumns">Table columns type made available to the lambdas.</typeparam>
/// <remarks>
/// Every builder method returns a new instance; the source instance is never mutated, so a partially built query can be
/// safely reused as a starting point. Call one of the terminal methods to execute it, or <see cref="AsQuery"/> to drop
/// back to the lower level <see cref="Query{T}"/> API.
/// </remarks>
public class FluentQuery<TResult, TColumns>
{
    /// <summary>The underlying query the builder wraps.</summary>
    protected Query<TResult> Inner { get; }

    /// <summary>Column members passed to the predicates and projections.</summary>
    protected TColumns Columns { get; }

    /// <summary>Next free alias suffix, used to keep generated join aliases unique.</summary>
    protected int NextAlias { get; }

    /// <summary>Creates a builder over an existing query.</summary>
    /// <param name="query">Query to wrap.</param>
    /// <param name="columns">Column members bound to the table aliases already in the query.</param>
    /// <param name="nextAlias">First alias suffix that may be used for a new join.</param>
    public FluentQuery(Query<TResult> query, TColumns columns, int nextAlias = 1)
    {
        Inner = query;
        Columns = columns;
        NextAlias = nextAlias;
    }

    /// <summary>Returns a builder over a derived query, keeping the columns and alias state.</summary>
    /// <param name="query">Derived query.</param>
    /// <returns>A builder wrapping <paramref name="query"/>.</returns>
    protected FluentQuery<TResult, TColumns> With(Query<TResult> query) =>
        new(query, Columns, NextAlias);

    /// <summary>Drops the fluent wrapper and returns the underlying query.</summary>
    /// <returns>The wrapped <see cref="Query{T}"/>.</returns>
    public Query<TResult> AsQuery() => Inner;

    /// <summary>Filters the query with a predicate built from the table columns.</summary>
    /// <param name="predicate">Builds the condition to apply.</param>
    /// <returns>A new builder with the condition appended using <c>AND</c>.</returns>
    public FluentQuery<TResult, TColumns> Where(Func<TColumns, Predicate> predicate) =>
        With(Inner.Where(predicate(Columns)));

    /// <summary>Filters the query with an already built predicate.</summary>
    /// <param name="predicate">Condition to apply.</param>
    /// <returns>A new builder with the condition appended using <c>AND</c>.</returns>
    public FluentQuery<TResult, TColumns> Where(Predicate predicate) =>
        With(Inner.Where(predicate));

    /// <summary>Applies a filter only when the condition is <see langword="true"/>.</summary>
    /// <param name="condition">Whether to apply the filter.</param>
    /// <param name="predicate">Builds the condition to apply.</param>
    /// <returns>The filtered builder, or the same instance when the condition is false.</returns>
    public FluentQuery<TResult, TColumns> WhereIf(
        bool condition,
        Func<TColumns, Predicate> predicate
    ) => condition ? Where(predicate) : this;

    /// <summary>Adds the first <c>ORDER BY</c> term, ascending.</summary>
    /// <param name="column">Expression to sort by.</param>
    /// <returns>A new ordered builder.</returns>
    public FluentQuery<TResult, TColumns> OrderBy(Func<TColumns, SqlExpression> column) =>
        With(Inner.OrderBy(new Ordering(column(Columns), false)));

    /// <summary>Adds the first <c>ORDER BY</c> term, descending.</summary>
    /// <param name="column">Expression to sort by.</param>
    /// <returns>A new ordered builder.</returns>
    public FluentQuery<TResult, TColumns> OrderByDescending(Func<TColumns, SqlExpression> column) =>
        With(Inner.OrderBy(new Ordering(column(Columns), true)));

    /// <summary>Adds a subsequent <c>ORDER BY</c> term, ascending.</summary>
    /// <param name="column">Expression to sort by.</param>
    /// <returns>A new ordered builder.</returns>
    public FluentQuery<TResult, TColumns> ThenBy(Func<TColumns, SqlExpression> column) =>
        With(Inner.ThenBy(new Ordering(column(Columns), false)));

    /// <summary>Adds a subsequent <c>ORDER BY</c> term, descending.</summary>
    /// <param name="column">Expression to sort by.</param>
    /// <returns>A new ordered builder.</returns>
    public FluentQuery<TResult, TColumns> ThenByDescending(Func<TColumns, SqlExpression> column) =>
        With(Inner.ThenBy(new Ordering(column(Columns), true)));

    /// <summary>Groups by a single expression.</summary>
    /// <param name="column">Expression to group by.</param>
    /// <returns>A new grouped builder.</returns>
    public FluentQuery<TResult, TColumns> GroupBy(Func<TColumns, SqlExpression> column) =>
        With(Inner.GroupBy(column(Columns)));

    /// <summary>Groups by several expressions in one call.</summary>
    /// <param name="columns">Expressions to group by.</param>
    /// <returns>A new grouped builder.</returns>
    public FluentQuery<TResult, TColumns> GroupBy(Func<TColumns, SqlExpression[]> columns) =>
        With(Inner.GroupBy(columns(Columns)));

    /// <summary>Filters the grouped rows, producing a <c>HAVING</c> clause.</summary>
    /// <param name="predicate">Builds the condition to apply.</param>
    /// <returns>A new builder with the condition appended using <c>AND</c>.</returns>
    public FluentQuery<TResult, TColumns> Having(Func<TColumns, Predicate> predicate) =>
        With(Inner.Having(predicate(Columns)));

    /// <summary>Projects the query onto a new row shape.</summary>
    /// <typeparam name="T">Projected row type.</typeparam>
    /// <param name="projection">Builds the projection from the table columns.</param>
    /// <returns>A builder over the projected query.</returns>
    public FluentQuery<T, TColumns> Select<T>(Func<TColumns, IProjection<T>> projection) =>
        new(Inner.Select(projection(Columns).Build()), Columns, NextAlias);

    /// <summary>Removes duplicate rows, producing <c>SELECT DISTINCT</c>.</summary>
    /// <returns>A new distinct builder.</returns>
    public FluentQuery<TResult, TColumns> Distinct() => With(Inner.Distinct());

    /// <summary>Skips a number of rows; requires an ordering for stable results.</summary>
    /// <param name="count">Rows to skip.</param>
    /// <returns>A new builder with the offset applied.</returns>
    public FluentQuery<TResult, TColumns> Skip(int count) => With(Inner.Skip(count));

    /// <summary>Limits the number of returned rows.</summary>
    /// <param name="count">Maximum rows to return.</param>
    /// <returns>A new builder with the limit applied.</returns>
    public FluentQuery<TResult, TColumns> Take(int count) => With(Inner.Take(count));

    /// <summary>Builds the existence probe this query would use as a subquery.</summary>
    /// <returns>A predicate that checks the query returns at least one row.</returns>
    public Predicate Exists() => Inner.Exists();

    /// <summary>Renders the current query to SQL without executing it, which is useful for logging and tests.</summary>
    /// <returns>The SQL text and its bound parameters.</returns>
    public CommandSpec ToCommand() => Inner.ToCommand();

    /// <summary>Executes the query and materialises every row.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The materialised rows.</returns>
    public Task<List<TResult>> ToListAsync(CancellationToken ct = default) => Inner.ToListAsync(ct);

    /// <summary>Executes the query and streams rows as they are read, without buffering the result set.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>Rows produced lazily while the reader is open.</returns>
    /// <remarks>Prefer this over <see cref="ToListAsync"/> for large result sets.</remarks>
    public IAsyncEnumerable<TResult> StreamAsync(CancellationToken ct = default) =>
        Inner.StreamAsync(ct);

    /// <summary>Returns the first row.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The first row.</returns>
    /// <exception cref="InvalidOperationException">The query returned no row.</exception>
    public Task<TResult> FirstAsync(CancellationToken ct = default) => Inner.FirstAsync(ct);

    /// <summary>Returns the first row, or <see langword="null"/> when the query matches nothing.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The first row, or <see langword="null"/>.</returns>
    public Task<TResult?> FirstOrDefaultAsync(CancellationToken ct = default) =>
        Inner.FirstOrDefaultAsync(ct);

    /// <summary>Returns the only row.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The single row.</returns>
    /// <exception cref="InvalidOperationException">The query returned no row or more than one row.</exception>
    public Task<TResult> SingleAsync(CancellationToken ct = default) => Inner.SingleAsync(ct);

    /// <summary>Returns the only row, or <see langword="null"/> when the query matches nothing.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The single row, or <see langword="null"/>.</returns>
    /// <exception cref="InvalidOperationException">The query returned more than one row.</exception>
    public Task<TResult?> SingleOrDefaultAsync(CancellationToken ct = default) =>
        Inner.SingleOrDefaultAsync(ct);

    /// <summary>Counts the matching rows on the server.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The row count.</returns>
    public Task<long> CountAsync(CancellationToken ct = default) => Inner.CountAsync(ct);

    /// <summary>Checks whether any row matches without counting the whole set.</summary>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns><see langword="true"/> when at least one row matches.</returns>
    public Task<bool> AnyAsync(CancellationToken ct = default) => Inner.AnyAsync(ct);

    /// <summary>Evaluates a single scalar expression over the filtered rows.</summary>
    /// <typeparam name="TValue">Scalar type.</typeparam>
    /// <param name="expression">Expression to evaluate, for example <c>Sql.Sum(table.Price)</c>.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The scalar value, or <see langword="null"/> when the aggregate has no input row.</returns>
    public Task<TValue?> ScalarAsync<TValue>(
        SqlExpression<TValue> expression,
        CancellationToken ct = default
    ) => Inner.ScalarAsync(expression, ct);

    /// <summary>Computes <c>SUM</c> over the filtered rows.</summary>
    /// <param name="expression">Numeric expression to sum.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The sum, or <see langword="null"/> when no row matches.</returns>
    public Task<decimal?> SumAsync(
        SqlExpression<decimal> expression,
        CancellationToken ct = default
    ) => Inner.SumAsync(expression, ct);

    /// <summary>Computes <c>AVG</c> over the filtered rows.</summary>
    /// <param name="expression">Numeric expression to average.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The average, or <see langword="null"/> when no row matches.</returns>
    public Task<decimal?> AverageAsync(
        SqlExpression<decimal> expression,
        CancellationToken ct = default
    ) => Inner.AverageAsync(expression, ct);

    /// <summary>Computes <c>MIN</c> over the filtered rows.</summary>
    /// <typeparam name="TValue">Value type to aggregate.</typeparam>
    /// <param name="expression">Expression to aggregate.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The minimum, or <see langword="null"/> when no row matches.</returns>
    public Task<TValue?> MinAsync<TValue>(
        SqlExpression<TValue> expression,
        CancellationToken ct = default
    )
        where TValue : struct => Inner.MinAsync(expression, ct);

    /// <summary>Computes <c>MAX</c> over the filtered rows.</summary>
    /// <typeparam name="TValue">Value type to aggregate.</typeparam>
    /// <param name="expression">Expression to aggregate.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The maximum, or <see langword="null"/> when no row matches.</returns>
    public Task<TValue?> MaxAsync<TValue>(
        SqlExpression<TValue> expression,
        CancellationToken ct = default
    )
        where TValue : struct => Inner.MaxAsync(expression, ct);

    /// <summary>Returns one page of rows plus the total count of the filtered set.</summary>
    /// <param name="pageNumber">One-based page number.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="ct">Token used to cancel the commands.</param>
    /// <returns>The page and the total row count.</returns>
    public Task<PagedResult<TResult>> ToPagedListAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct = default
    ) => Inner.ToPagedListAsync(pageNumber, pageSize, ct);

    /// <summary>Filters the query with a correlated <c>EXISTS</c> over another repository's table.</summary>
    /// <typeparam name="TEntity">Joined entity type.</typeparam>
    /// <typeparam name="TTable">Strongly typed table columns of the joined entity.</typeparam>
    /// <typeparam name="TOptional">Columns shape used for an outer join.</typeparam>
    /// <param name="source">Repository that supplies the joined table.</param>
    /// <param name="on">Builds the correlation predicate from both tables.</param>
    /// <param name="negate">Adds <c>NOT</c> in front of the probe when <see langword="true"/>.</param>
    /// <returns>A new builder with the subquery appended using <c>AND</c>.</returns>
    /// <exception cref="InvalidOperationException">The joined repository belongs to a different session.</exception>
    public FluentQuery<TResult, TColumns> WhereExists<TEntity, TTable, TOptional>(
        IQuerySource<TEntity, TTable, TOptional> source,
        Func<TColumns, TTable, Predicate> on,
        bool negate = false
    )
        where TEntity : class
        where TTable : Table<TEntity>
    {
        EnsureSession(source.Session);
        var table = source.CreateTable("exists" + NextAlias);
        var predicate = source.Session.From(table).Where(on(Columns, table)).Exists();
        return new(Inner.Where(negate ? !predicate : predicate), Columns, NextAlias + 1);
    }

    /// <summary>Executes a set-based <c>UPDATE</c> over the rows that match the current filters.</summary>
    /// <param name="assignments">Builds the column assignments from the table columns.</param>
    /// <param name="expectedVersion">Optimistic concurrency token the stored row must still hold.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The number of affected rows.</returns>
    /// <exception cref="InvalidOperationException">The query has no <c>Where</c> predicate.</exception>
    public Task<int> ExecuteUpdateAsync(
        Func<TColumns, Assignment[]> assignments,
        long? expectedVersion = null,
        CancellationToken ct = default
    ) => Inner.ExecuteUpdateAsync(assignments(Columns), expectedVersion, ct);

    /// <summary>Executes a set-based <c>UPDATE</c> using a prebuilt change description.</summary>
    /// <param name="changes">Description of the column changes.</param>
    /// <param name="expectedVersion">Optimistic concurrency token the stored row must still hold.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The number of affected rows.</returns>
    public Task<int> ExecuteUpdateAsync(
        IUpdate<TColumns> changes,
        long? expectedVersion = null,
        CancellationToken ct = default
    ) => Inner.ExecuteUpdateAsync(changes.Build(Columns), expectedVersion, ct);

    /// <summary>
    /// Executes a set-based <c>DELETE</c> over the rows that match the current filters, turning it into a soft-delete
    /// when the table declares a soft-delete column.
    /// </summary>
    /// <param name="expectedVersion">Optimistic concurrency token the stored row must still hold.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The number of affected rows.</returns>
    /// <exception cref="InvalidOperationException">The query has no <c>Where</c> predicate.</exception>
    public Task<int> ExecuteDeleteAsync(
        long? expectedVersion = null,
        CancellationToken ct = default
    ) => Inner.ExecuteDeleteAsync(expectedVersion, ct);

    /// <summary>Applies a predicate and deletes the matching rows in one call.</summary>
    /// <param name="predicate">Builds the condition that selects the rows to delete.</param>
    /// <param name="expectedVersion">Optimistic concurrency token the stored row must still hold.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The number of affected rows.</returns>
    public Task<int> DeleteWhereAsync(
        Func<TColumns, Predicate> predicate,
        long? expectedVersion = null,
        CancellationToken ct = default
    ) => Where(predicate).ExecuteDeleteAsync(expectedVersion, ct);

    /// <summary>Joins another repository's table with <c>INNER JOIN</c>.</summary>
    /// <typeparam name="TEntity">Joined entity type.</typeparam>
    /// <typeparam name="TTable">Strongly typed table columns of the joined entity.</typeparam>
    /// <typeparam name="TOptional">Columns shape of the joined table.</typeparam>
    /// <param name="source">Repository that supplies the joined table.</param>
    /// <param name="on">Builds the join predicate from both tables.</param>
    /// <returns>A builder whose lambdas receive both tables.</returns>
    /// <exception cref="InvalidOperationException">The joined repository belongs to a different session.</exception>
    public JoinedQuery<TResult, TColumns, TTable> InnerJoin<TEntity, TTable, TOptional>(
        IQuerySource<TEntity, TTable, TOptional> source,
        Func<TColumns, TTable, Predicate> on
    )
        where TEntity : class
        where TTable : Table<TEntity>
    {
        EnsureSession(source.Session);
        var table = source.CreateTable("join" + NextAlias);
        return new(Inner.Join(table, on(Columns, table)), Columns, table, NextAlias + 1);
    }

    /// <summary>Joins another repository's table with <c>LEFT JOIN</c>, so joined columns become nullable.</summary>
    /// <typeparam name="TEntity">Joined entity type.</typeparam>
    /// <typeparam name="TTable">Strongly typed table columns of the joined entity.</typeparam>
    /// <typeparam name="TOptional">Columns shape used for the outer join, where every column may be absent.</typeparam>
    /// <param name="source">Repository that supplies the joined table.</param>
    /// <param name="on">Builds the join predicate from both tables.</param>
    /// <returns>A builder whose lambdas receive both tables, the joined one through <typeparamref name="TOptional"/>.</returns>
    /// <exception cref="InvalidOperationException">The joined repository belongs to a different session.</exception>
    public JoinedQuery<TResult, TColumns, TOptional> LeftJoin<TEntity, TTable, TOptional>(
        IQuerySource<TEntity, TTable, TOptional> source,
        Func<TColumns, TTable, Predicate> on
    )
        where TEntity : class
        where TTable : Table<TEntity>
    {
        EnsureSession(source.Session);
        var alias = "join" + NextAlias;
        var table = source.CreateTable(alias);
        return new(
            Inner.Join(table, on(Columns, table), JoinKind.Left),
            Columns,
            source.CreateOptionalTable(alias),
            NextAlias + 1
        );
    }

    private void EnsureSession(DataSession session)
    {
        if (!ReferenceEquals(Inner.Session, session))
            throw new InvalidOperationException(
                "Joined repositories must belong to the same DataSession."
            );
    }
}

/// <summary>
/// Query builder over two joined tables, so predicates, ordering and projections receive both column sets.
/// </summary>
/// <typeparam name="TResult">Row type the query currently returns.</typeparam>
/// <typeparam name="TLeft">Columns of the originating table.</typeparam>
/// <typeparam name="TRight">Columns of the joined table; nullable shapes are used for an outer join.</typeparam>
public sealed class JoinedQuery<TResult, TLeft, TRight>
    : FluentQuery<TResult, (TLeft Left, TRight Right)>
{
    /// <summary>Creates a builder over an already joined query.</summary>
    /// <param name="query">Query that already contains the join.</param>
    /// <param name="left">Columns of the originating table.</param>
    /// <param name="right">Columns of the joined table.</param>
    /// <param name="nextAlias">First alias suffix that may be used for another join.</param>
    public JoinedQuery(Query<TResult> query, TLeft left, TRight right, int nextAlias = 1)
        : base(query, (left, right), nextAlias) { }

    private JoinedQuery<TResult, TLeft, TRight> Joined(Query<TResult> query) =>
        new(query, Columns.Left, Columns.Right, NextAlias);

    /// <summary>Filters the query with a predicate built from both tables.</summary>
    /// <param name="predicate">Builds the condition to apply.</param>
    /// <returns>A new joined builder with the condition appended using <c>AND</c>.</returns>
    public JoinedQuery<TResult, TLeft, TRight> Where(Func<TLeft, TRight, Predicate> predicate) =>
        Joined(Inner.Where(predicate(Columns.Left, Columns.Right)));

    /// <summary>Applies a filter only when the condition is <see langword="true"/>.</summary>
    /// <param name="condition">Whether to apply the filter.</param>
    /// <param name="predicate">Builds the condition to apply.</param>
    /// <returns>The filtered builder, or the same instance when the condition is false.</returns>
    public JoinedQuery<TResult, TLeft, TRight> WhereIf(
        bool condition,
        Func<TLeft, TRight, Predicate> predicate
    ) => condition ? Where(predicate) : this;

    /// <summary>Adds the first <c>ORDER BY</c> term, ascending.</summary>
    /// <param name="column">Expression to sort by.</param>
    /// <returns>A new ordered builder.</returns>
    public JoinedQuery<TResult, TLeft, TRight> OrderBy(Func<TLeft, TRight, SqlExpression> column) =>
        Joined(Inner.OrderBy(new Ordering(column(Columns.Left, Columns.Right), false)));

    /// <summary>Adds the first <c>ORDER BY</c> term, descending.</summary>
    /// <param name="column">Expression to sort by.</param>
    /// <returns>A new ordered builder.</returns>
    public JoinedQuery<TResult, TLeft, TRight> OrderByDescending(
        Func<TLeft, TRight, SqlExpression> column
    ) => Joined(Inner.OrderBy(new Ordering(column(Columns.Left, Columns.Right), true)));

    /// <summary>Adds a subsequent <c>ORDER BY</c> term, ascending.</summary>
    /// <param name="column">Expression to sort by.</param>
    /// <returns>A new ordered builder.</returns>
    public JoinedQuery<TResult, TLeft, TRight> ThenBy(Func<TLeft, TRight, SqlExpression> column) =>
        Joined(Inner.ThenBy(new Ordering(column(Columns.Left, Columns.Right), false)));

    /// <summary>Adds a subsequent <c>ORDER BY</c> term, descending.</summary>
    /// <param name="column">Expression to sort by.</param>
    /// <returns>A new ordered builder.</returns>
    public JoinedQuery<TResult, TLeft, TRight> ThenByDescending(
        Func<TLeft, TRight, SqlExpression> column
    ) => Joined(Inner.ThenBy(new Ordering(column(Columns.Left, Columns.Right), true)));

    /// <summary>Groups by several expressions taken from both tables.</summary>
    /// <param name="columns">Expressions to group by.</param>
    /// <returns>A new grouped builder.</returns>
    public JoinedQuery<TResult, TLeft, TRight> GroupBy(
        Func<TLeft, TRight, SqlExpression[]> columns
    ) => Joined(Inner.GroupBy(columns(Columns.Left, Columns.Right)));

    /// <summary>Filters the grouped rows, producing a <c>HAVING</c> clause.</summary>
    /// <param name="predicate">Builds the condition to apply.</param>
    /// <returns>A new builder with the condition appended using <c>AND</c>.</returns>
    public JoinedQuery<TResult, TLeft, TRight> Having(Func<TLeft, TRight, Predicate> predicate) =>
        Joined(Inner.Having(predicate(Columns.Left, Columns.Right)));

    /// <summary>Projects the joined query onto a new row shape.</summary>
    /// <typeparam name="T">Projected row type.</typeparam>
    /// <param name="projection">Builds the projection from both tables.</param>
    /// <returns>A builder over the projected query.</returns>
    public JoinedQuery<T, TLeft, TRight> Select<T>(
        Func<TLeft, TRight, IProjection<T>> projection
    ) =>
        new(
            Inner.Select(projection(Columns.Left, Columns.Right).Build()),
            Columns.Left,
            Columns.Right,
            NextAlias
        );

    /// <summary>Removes duplicate rows, producing <c>SELECT DISTINCT</c>.</summary>
    /// <returns>A new distinct builder.</returns>
    public new JoinedQuery<TResult, TLeft, TRight> Distinct() => Joined(Inner.Distinct());

    /// <summary>Skips a number of rows; requires an ordering for stable results.</summary>
    /// <param name="count">Rows to skip.</param>
    /// <returns>A new builder with the offset applied.</returns>
    public new JoinedQuery<TResult, TLeft, TRight> Skip(int count) => Joined(Inner.Skip(count));

    /// <summary>Limits the number of returned rows.</summary>
    /// <param name="count">Maximum rows to return.</param>
    /// <returns>A new builder with the limit applied.</returns>
    public new JoinedQuery<TResult, TLeft, TRight> Take(int count) => Joined(Inner.Take(count));
}
