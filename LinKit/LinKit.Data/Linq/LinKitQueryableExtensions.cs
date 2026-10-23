using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinKit.Data.Linq;

namespace LinKit.Data;

/// <summary>
/// Asynchronous LINQ query execution extensions for LinKit and general IQueryable.
/// </summary>
public static class LinKitQueryableExtensions
{
    public static Task<List<T>> ToListAsync<T>(this IQueryable<T> source, CancellationToken ct = default) where T : class
    {
        if (source is ILinKitQueryable<T> lq)
        {
            return lq.ToListAsync(ct);
        }

        if (source.Provider is ILinKitAsyncQueryProvider provider)
        {
            return provider.ExecuteAsync<List<T>>(source.Expression, ct);
        }

        return Task.FromResult(source.ToList());
    }

    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, CancellationToken ct = default) where T : class
    {
        if (source is ILinKitQueryable<T> lq)
        {
            return lq.FirstOrDefaultAsync(ct);
        }

        if (source.Provider is ILinKitAsyncQueryProvider provider)
        {
            return provider.ExecuteAsync<T?>(source.Expression, ct);
        }

        return Task.FromResult(source.FirstOrDefault());
    }

    public static Task<long> CountAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
    {
        if (source.Provider is ILinKitAsyncQueryProvider provider)
        {
            return provider.ExecuteAsync<long>(source.Expression, ct);
        }

        return Task.FromResult((long)source.Count());
    }

    public static Task<bool> AnyAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
    {
        if (source.Provider is ILinKitAsyncQueryProvider provider)
        {
            return provider.ExecuteAsync<bool>(source.Expression, ct);
        }

        return Task.FromResult(source.Any());
    }

    public static Task<PagedResult<T>> ToPagedListAsync<T>(this IQueryable<T> source, int pageNumber, int pageSize, CancellationToken ct = default) where T : class
    {
        if (source is ILinKitQueryable<T> lq)
        {
            return lq.ToPagedListAsync(pageNumber, pageSize, ct);
        }

        if (source.Provider is ILinKitAsyncQueryProvider provider)
        {
            return provider.ExecutePagedAsync<T>(source.Expression, pageNumber, pageSize, ct);
        }

        var total = source.Count();
        var items = source.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<T>
        {
            Items = items,
            TotalCount = total,
            PageNumber = pageNumber,
            PageSize = pageSize
        });
    }
}
