using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data;

public readonly struct PatchValue<T>
{
    public bool IsSet { get; }
    public T Value { get; }
    public PatchValue(T value) { Value = value; IsSet = true; }
    public static implicit operator PatchValue<T>(T value) => new(value);
}

public interface IUpdate<TColumns>
{
    Assignment[] Build(TColumns columns);
}

public interface IQuerySource<TEntity, TTable, TOptional> where TEntity : class where TTable : Table<TEntity>
{
    DataSession Session { get; }
    TTable CreateTable(string alias);
    TOptional CreateOptionalTable(string alias);
}

public class FluentQuery<TResult, TColumns>
{
    protected Query<TResult> Inner { get; }
    protected TColumns Columns { get; }
    protected int NextAlias { get; }

    public FluentQuery(Query<TResult> query, TColumns columns, int nextAlias = 1)
    {
        Inner = query;
        Columns = columns;
        NextAlias = nextAlias;
    }

    protected FluentQuery<TResult, TColumns> With(Query<TResult> query) => new(query, Columns, NextAlias);

    public Query<TResult> AsQuery() => Inner;

    public FluentQuery<TResult, TColumns> Where(Func<TColumns, Predicate> predicate) => With(Inner.Where(predicate(Columns)));
    public FluentQuery<TResult, TColumns> Where(Predicate predicate) => With(Inner.Where(predicate));
    public FluentQuery<TResult, TColumns> WhereIf(bool condition, Func<TColumns, Predicate> predicate) => condition ? Where(predicate) : this;
    public FluentQuery<TResult, TColumns> OrderBy(Func<TColumns, SqlExpression> column) => With(Inner.OrderBy(new Ordering(column(Columns), false)));
    public FluentQuery<TResult, TColumns> OrderByDescending(Func<TColumns, SqlExpression> column) => With(Inner.OrderBy(new Ordering(column(Columns), true)));
    public FluentQuery<TResult, TColumns> ThenBy(Func<TColumns, SqlExpression> column) => With(Inner.ThenBy(new Ordering(column(Columns), false)));
    public FluentQuery<TResult, TColumns> ThenByDescending(Func<TColumns, SqlExpression> column) => With(Inner.ThenBy(new Ordering(column(Columns), true)));
    public FluentQuery<TResult, TColumns> GroupBy(Func<TColumns, SqlExpression> column) => With(Inner.GroupBy(column(Columns)));
    public FluentQuery<TResult, TColumns> GroupBy(Func<TColumns, SqlExpression[]> columns) => With(Inner.GroupBy(columns(Columns)));
    public FluentQuery<TResult, TColumns> Having(Func<TColumns, Predicate> predicate) => With(Inner.Having(predicate(Columns)));
    public FluentQuery<T, TColumns> Select<T>(Func<TColumns, IProjection<T>> projection) => new(Inner.Select(projection(Columns).Build()), Columns, NextAlias);
    public FluentQuery<TResult, TColumns> Distinct() => With(Inner.Distinct());
    public FluentQuery<TResult, TColumns> Skip(int count) => With(Inner.Skip(count));
    public FluentQuery<TResult, TColumns> Take(int count) => With(Inner.Take(count));
    public Predicate Exists() => Inner.Exists();
    public CommandSpec ToCommand() => Inner.ToCommand();

    public Task<List<TResult>> ToListAsync(CancellationToken ct = default) => Inner.ToListAsync(ct);
    public IAsyncEnumerable<TResult> StreamAsync(CancellationToken ct = default) => Inner.StreamAsync(ct);
    public Task<TResult> FirstAsync(CancellationToken ct = default) => Inner.FirstAsync(ct);
    public Task<TResult?> FirstOrDefaultAsync(CancellationToken ct = default) => Inner.FirstOrDefaultAsync(ct);
    public Task<TResult> SingleAsync(CancellationToken ct = default) => Inner.SingleAsync(ct);
    public Task<TResult?> SingleOrDefaultAsync(CancellationToken ct = default) => Inner.SingleOrDefaultAsync(ct);
    public Task<long> CountAsync(CancellationToken ct = default) => Inner.CountAsync(ct);
    public Task<bool> AnyAsync(CancellationToken ct = default) => Inner.AnyAsync(ct);
    public Task<PagedResult<TResult>> ToPagedListAsync(int pageNumber, int pageSize, CancellationToken ct = default) => Inner.ToPagedListAsync(pageNumber, pageSize, ct);

    public FluentQuery<TResult, TColumns> WhereExists<TEntity, TTable, TOptional>(
        IQuerySource<TEntity, TTable, TOptional> source, Func<TColumns, TTable, Predicate> on, bool negate = false)
        where TEntity : class where TTable : Table<TEntity>
    {
        EnsureSession(source.Session);
        var table = source.CreateTable("exists" + NextAlias);
        var predicate = source.Session.From(table).Where(on(Columns, table)).Exists();
        return new(Inner.Where(negate ? !predicate : predicate), Columns, NextAlias + 1);
    }

    public Task<int> ExecuteUpdateAsync(Func<TColumns, Assignment[]> assignments, long? expectedVersion = null, CancellationToken ct = default)
        => Inner.ExecuteUpdateAsync(assignments(Columns), expectedVersion, ct);

    public Task<int> ExecuteUpdateAsync(IUpdate<TColumns> changes, long? expectedVersion = null, CancellationToken ct = default)
        => Inner.ExecuteUpdateAsync(changes.Build(Columns), expectedVersion, ct);

    public Task<int> ExecuteDeleteAsync(long? expectedVersion = null, CancellationToken ct = default) => Inner.ExecuteDeleteAsync(expectedVersion, ct);
    public Task<int> DeleteWhereAsync(Func<TColumns, Predicate> predicate, long? expectedVersion = null, CancellationToken ct = default)
        => Where(predicate).ExecuteDeleteAsync(expectedVersion, ct);

    public JoinedQuery<TResult, TColumns, TTable> InnerJoin<TEntity, TTable, TOptional>(
        IQuerySource<TEntity, TTable, TOptional> source, Func<TColumns, TTable, Predicate> on)
        where TEntity : class where TTable : Table<TEntity>
    {
        EnsureSession(source.Session);
        var table = source.CreateTable("join" + NextAlias);
        return new(Inner.Join(table, on(Columns, table)), Columns, table, NextAlias + 1);
    }

    public JoinedQuery<TResult, TColumns, TOptional> LeftJoin<TEntity, TTable, TOptional>(
        IQuerySource<TEntity, TTable, TOptional> source, Func<TColumns, TTable, Predicate> on)
        where TEntity : class where TTable : Table<TEntity>
    {
        EnsureSession(source.Session);
        var alias = "join" + NextAlias;
        var table = source.CreateTable(alias);
        return new(Inner.Join(table, on(Columns, table), JoinKind.Left), Columns, source.CreateOptionalTable(alias), NextAlias + 1);
    }

    private void EnsureSession(DataSession session)
    {
        if (!ReferenceEquals(Inner.Session, session)) throw new InvalidOperationException("Joined repositories must belong to the same DataSession.");
    }
}

public sealed class JoinedQuery<TResult, TLeft, TRight> : FluentQuery<TResult, (TLeft Left, TRight Right)>
{
    public JoinedQuery(Query<TResult> query, TLeft left, TRight right, int nextAlias = 1) : base(query, (left, right), nextAlias) { }
    private JoinedQuery<TResult, TLeft, TRight> Joined(Query<TResult> query) => new(query, Columns.Left, Columns.Right, NextAlias);

    public JoinedQuery<TResult, TLeft, TRight> Where(Func<TLeft, TRight, Predicate> predicate) => Joined(Inner.Where(predicate(Columns.Left, Columns.Right)));
    public JoinedQuery<TResult, TLeft, TRight> WhereIf(bool condition, Func<TLeft, TRight, Predicate> predicate) => condition ? Where(predicate) : this;
    public JoinedQuery<TResult, TLeft, TRight> OrderBy(Func<TLeft, TRight, SqlExpression> column) => Joined(Inner.OrderBy(new Ordering(column(Columns.Left, Columns.Right), false)));
    public JoinedQuery<TResult, TLeft, TRight> OrderByDescending(Func<TLeft, TRight, SqlExpression> column) => Joined(Inner.OrderBy(new Ordering(column(Columns.Left, Columns.Right), true)));
    public JoinedQuery<TResult, TLeft, TRight> ThenBy(Func<TLeft, TRight, SqlExpression> column) => Joined(Inner.ThenBy(new Ordering(column(Columns.Left, Columns.Right), false)));
    public JoinedQuery<TResult, TLeft, TRight> ThenByDescending(Func<TLeft, TRight, SqlExpression> column) => Joined(Inner.ThenBy(new Ordering(column(Columns.Left, Columns.Right), true)));
    public JoinedQuery<TResult, TLeft, TRight> GroupBy(Func<TLeft, TRight, SqlExpression[]> columns) => Joined(Inner.GroupBy(columns(Columns.Left, Columns.Right)));
    public JoinedQuery<TResult, TLeft, TRight> Having(Func<TLeft, TRight, Predicate> predicate) => Joined(Inner.Having(predicate(Columns.Left, Columns.Right)));

    public JoinedQuery<T, TLeft, TRight> Select<T>(Func<TLeft, TRight, IProjection<T>> projection)
        => new(Inner.Select(projection(Columns.Left, Columns.Right).Build()), Columns.Left, Columns.Right, NextAlias);

    public new JoinedQuery<TResult, TLeft, TRight> Distinct() => Joined(Inner.Distinct());
    public new JoinedQuery<TResult, TLeft, TRight> Skip(int count) => Joined(Inner.Skip(count));
    public new JoinedQuery<TResult, TLeft, TRight> Take(int count) => Joined(Inner.Take(count));
}
