using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace LinKit.Data.Linq;

/// <summary>
/// Implementation of ILinKitQueryable and IOrderedQueryable for LinKit entities.
/// </summary>
public sealed class LinKitQueryable<TEntity> : ILinKitQueryable<TEntity>, IOrderedQueryable<TEntity> where TEntity : class
{
    private readonly LinKitQueryProvider<TEntity> _provider;
    private readonly Expression _expression;

    public LinKitQueryable(LinKitQueryProvider<TEntity> provider)
    {
        _provider = provider;
        _expression = Expression.Constant(this);
    }

    public LinKitQueryable(LinKitQueryProvider<TEntity> provider, Expression expression)
    {
        _provider = provider;
        _expression = expression;
    }

    public Type ElementType => typeof(TEntity);

    public Expression Expression => _expression;

    public IQueryProvider Provider => _provider;

    public IEnumerator<TEntity> GetEnumerator()
    {
        var list = _provider.Execute<List<TEntity>>(_expression);
        return list.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Task<List<TEntity>> ToListAsync(CancellationToken ct = default)
    {
        return _provider.ExecuteAsync<List<TEntity>>(_expression, ct);
    }

    public Task<TEntity?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        return _provider.ExecuteAsync<TEntity?>(_expression, ct);
    }

    public Task<long> CountAsync(CancellationToken ct = default)
    {
        return _provider.ExecuteAsync<long>(_expression, ct);
    }

    public Task<bool> AnyAsync(CancellationToken ct = default)
    {
        return _provider.ExecuteAsync<bool>(_expression, ct);
    }

    public Task<PagedResult<TEntity>> ToPagedListAsync(int pageNumber, int pageSize, CancellationToken ct = default)
    {
        return _provider.ExecutePagedAsync<TEntity>(_expression, pageNumber, pageSize, ct);
    }
}
