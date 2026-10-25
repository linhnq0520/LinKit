using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data;

public enum JoinKind { Inner, Left }
internal sealed record TableSource(TableModel Model, string Alias);
internal sealed record JoinPlan(TableSource Source, SqlNode On, JoinKind Kind);
internal sealed record SelectPlan(TableSource From, IReadOnlyList<SqlNode> Projection,
    IReadOnlyList<JoinPlan> Joins, SqlNode? Where, IReadOnlyList<Ordering> Order,
    IReadOnlyList<SqlNode> Group, SqlNode? Having, int? Skip = null, int? Take = null, bool Distinct = false);

public interface IProjection<T>
{
    Projection<T> Build();
}

public sealed class Projection<T>(IReadOnlyList<SqlExpression> columns, Func<DbDataReader, T> read) : IProjection<T>
{
    public IReadOnlyList<SqlExpression> Columns { get; } = columns.ToArray();
    public Func<DbDataReader, T> Read { get; } = read;
    public Projection<T> Build() => this;
}

public abstract class Table<TEntity> where TEntity : class
{
    public TableModel Model { get; }
    public string Alias { get; }
    public abstract Projection<TEntity> Projection { get; }
    protected Table(TableModel model, string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        Model = model;
        Alias = alias;
    }
}

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

    internal static Query<TEntity> From<TEntity>(DataSession session, Table<TEntity> table) where TEntity : class
        => new(session, new(new(table.Model, table.Alias), table.Projection.Columns.Select(column => column.Node).ToArray(), [], null, [], [], null), table.Projection.Read);

    private Query<TResult> With(SelectPlan plan) => new(_session, plan, _read);

    private void BeforePagination()
    {
        if (_plan.Skip is not null || _plan.Take is not null)
            throw new NotSupportedException("Apply filters, joins, grouping and ordering before pagination. Nested pagination requires an explicit subquery.");
    }

    public Query<TResult> Where(Predicate predicate)
    {
        BeforePagination();
        return With(_plan with { Where = _plan.Where is null ? predicate.Node : new BinaryNode(_plan.Where, "AND", predicate.Node) });
    }

    public Query<TResult> Join<TEntity>(Table<TEntity> table, Predicate on, JoinKind kind = JoinKind.Inner) where TEntity : class
    {
        BeforePagination();
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return With(_plan with { Joins = [.. _plan.Joins, new(new(table.Model, table.Alias), on.Node, kind)] });
    }

    public Query<T> Select<T>(Projection<T> projection)
    {
        BeforePagination();
        if (projection.Columns.Count == 0) throw new ArgumentException("Projection must contain columns.", nameof(projection));
        return new(_session, _plan with { Projection = projection.Columns.Select(column => column.Node).ToArray() }, projection.Read);
    }

    public Query<TResult> OrderBy(params Ordering[] order)
    {
        BeforePagination();
        return With(_plan with { Order = order.ToArray() });
    }

    public Query<TResult> ThenBy(Ordering order)
    {
        BeforePagination();
        if (_plan.Order.Count == 0) throw new InvalidOperationException("ThenBy requires OrderBy.");
        return With(_plan with { Order = [.. _plan.Order, order] });
    }

    public Query<TResult> GroupBy(params SqlExpression[] columns)
    {
        BeforePagination();
        return With(_plan with { Group = columns.Select(column => column.Node).ToArray() });
    }

    public Query<TResult> Having(Predicate predicate)
    {
        BeforePagination();
        if (_plan.Group.Count == 0) throw new InvalidOperationException("Having requires GroupBy.");
        return With(_plan with { Having = _plan.Having is null ? predicate.Node : new BinaryNode(_plan.Having, "AND", predicate.Node) });
    }

    public Query<TResult> Distinct()
    {
        BeforePagination();
        return With(_plan with { Distinct = true });
    }

    public Query<TResult> Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_plan.Take is not null) throw new NotSupportedException("Skip after Take requires a subquery. Use Skip before Take.");
        return With(_plan with { Skip = checked((_plan.Skip ?? 0) + count) });
    }

    public Query<TResult> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return With(_plan with { Take = Math.Min(_plan.Take ?? int.MaxValue, count) });
    }

    public Predicate Exists() => new(new ExistsNode(_plan with { Order = _plan.Skip is null && _plan.Take is null ? [] : _plan.Order }, false));

    public CommandSpec ToCommand()
    {
        var writer = new SqlWriter(_session.Dialect, _session.TenantId);
        return new(writer.Select(_plan), writer.Arguments);
    }

    public Task<List<TResult>> ToListAsync(CancellationToken ct = default) => _session.QueryAsync(ToCommand(), _read, ct);
    public IAsyncEnumerable<TResult> StreamAsync(CancellationToken ct = default) => _session.StreamAsync(ToCommand(), _read, ct);

    public CommandSpec UpdateCommand(IReadOnlyList<Assignment> assignments, long? expectedVersion = null)
    {
        var writer = new SqlWriter(_session.Dialect, _session.TenantId);
        return new(writer.Mutate(_plan, assignments, false, expectedVersion), writer.Arguments);
    }

    public CommandSpec DeleteCommand(long? expectedVersion = null)
    {
        var writer = new SqlWriter(_session.Dialect, _session.TenantId);
        return new(writer.Mutate(_plan, [], true, expectedVersion), writer.Arguments);
    }

    public Task<int> ExecuteUpdateAsync(IReadOnlyList<Assignment> assignments, long? expectedVersion = null, CancellationToken ct = default)
        => _session.ExecuteAsync(UpdateCommand(assignments, expectedVersion), ct);

    public Task<int> ExecuteDeleteAsync(long? expectedVersion = null, CancellationToken ct = default)
        => _session.ExecuteAsync(DeleteCommand(expectedVersion), ct);

    public async Task<TResult?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        var items = await Take(1).ToListAsync(ct).ConfigureAwait(false);
        return items.Count == 0 ? default : items[0];
    }

    public async Task<TResult> FirstAsync(CancellationToken ct = default)
    {
        var items = await Take(1).ToListAsync(ct).ConfigureAwait(false);
        return items.Count == 0 ? throw new InvalidOperationException("Sequence contains no elements.") : items[0];
    }

    public async Task<TResult?> SingleOrDefaultAsync(CancellationToken ct = default)
    {
        var items = await Take(2).ToListAsync(ct).ConfigureAwait(false);
        return items.Count switch { 0 => default, 1 => items[0], _ => throw new InvalidOperationException("Sequence contains more than one element.") };
    }

    public async Task<TResult> SingleAsync(CancellationToken ct = default)
    {
        var items = await Take(2).ToListAsync(ct).ConfigureAwait(false);
        return items.Count switch { 0 => throw new InvalidOperationException("Sequence contains no elements."), 1 => items[0], _ => throw new InvalidOperationException("Sequence contains more than one element.") };
    }

    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        var writer = new SqlWriter(_session.Dialect, _session.TenantId);
        var plan = _plan with { Order = _plan.Skip is null && _plan.Take is null ? [] : _plan.Order };
        var sql = writer.Select(plan);
        var count = _session.Dialect.Kind == DatabaseKind.SqlServer ? "COUNT_BIG(*)" : "COUNT(*)";
        var value = await _session.ScalarAsync(new($"SELECT {count} FROM ({sql}) {_session.Dialect.Quote("count_result")}", writer.Arguments), ct).ConfigureAwait(false);
        return DbValues.ConvertTo<long>(value!);
    }

    public async Task<bool> AnyAsync(CancellationToken ct = default) => await Take(1).CountAsync(ct).ConfigureAwait(false) != 0;

    public async Task<PagedResult<TResult>> ToPagedListAsync(int pageNumber, int pageSize, CancellationToken ct = default)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 10;
        var totalCount = await CountAsync(ct).ConfigureAwait(false);
        var items = totalCount == 0
            ? (IReadOnlyList<TResult>)Array.Empty<TResult>()
            : await Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        return new PagedResult<TResult>
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }
}
