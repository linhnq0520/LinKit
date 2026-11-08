using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data.Linq;

/// <summary>
/// <see cref="ILinKitQueryable{TEntity}"/> and <see cref="IOrderedQueryable{T}"/> implementation that carries the
/// expression tree instead of materialising it, so LINQ operators keep composing until a terminal runs.
/// </summary>
/// <typeparam name="TEntity">Entity type.</typeparam>
public sealed class LinKitQueryable<TEntity> : ILinKitQueryable<TEntity>, IOrderedQueryable<TEntity>
    where TEntity : class
{
    private readonly LinKitQueryProvider<TEntity> _provider;
    private readonly Expression _expression;

    /// <summary>Creates the root queryable over the whole table.</summary>
    /// <param name="provider">Provider that executes the composed expression.</param>
    public LinKitQueryable(LinKitQueryProvider<TEntity> provider)
    {
        _provider = provider;
        _expression = Expression.Constant(this);
    }

    /// <summary>Creates a queryable over a composed expression.</summary>
    /// <param name="provider">Provider that executes the expression.</param>
    /// <param name="expression">Expression tree accumulated by the LINQ operators.</param>
    public LinKitQueryable(LinKitQueryProvider<TEntity> provider, Expression expression)
    {
        _provider = provider;
        _expression = expression;
    }

    /// <inheritdoc/>
    public Type ElementType => typeof(TEntity);

    /// <inheritdoc/>
    public Expression Expression => _expression;

    /// <inheritdoc/>
    public IQueryProvider Provider => _provider;

    /// <summary>Executes the query synchronously and enumerates the materialised rows.</summary>
    /// <returns>An enumerator over the materialised rows.</returns>
    /// <remarks>Prefer <see cref="ToListAsync"/>; a synchronous enumeration blocks the calling thread.</remarks>
    public IEnumerator<TEntity> GetEnumerator()
    {
        var list = _provider.Execute<List<TEntity>>(_expression);
        return list.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public Task<List<TEntity>> ToListAsync(CancellationToken ct = default)
    {
        return _provider.ExecuteAsync<List<TEntity>>(_expression, ct);
    }

    /// <inheritdoc/>
    public Task<TEntity?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        return _provider.ExecuteAsync<TEntity?>(_expression, ct);
    }

    /// <inheritdoc/>
    public Task<long> CountAsync(CancellationToken ct = default)
    {
        return _provider.ExecuteAsync<long>(_expression, ct);
    }

    /// <inheritdoc/>
    public Task<bool> AnyAsync(CancellationToken ct = default)
    {
        return _provider.ExecuteAsync<bool>(_expression, ct);
    }

    /// <inheritdoc/>
    public Task<PagedResult<TEntity>> ToPagedListAsync(
        int pageNumber,
        int pageSize,
        CancellationToken ct = default
    )
    {
        return _provider.ExecutePagedAsync<TEntity>(_expression, pageNumber, pageSize, ct);
    }
}
