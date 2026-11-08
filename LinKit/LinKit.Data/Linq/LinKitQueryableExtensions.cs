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
    /// <summary>Materialises the query asynchronously, using the LinKit provider when the source has one.</summary>
    /// <typeparam name="T">Entity type.</typeparam>
    /// <param name="source">Query to execute.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The materialised rows.</returns>
    /// <remarks>A plain <see cref="IQueryable{T}"/> that is not backed by LinKit falls back to synchronous execution.</remarks>
    public static Task<List<T>> ToListAsync<T>(
        this IQueryable<T> source,
        CancellationToken ct = default
    )
        where T : class
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

    /// <summary>Returns the first row asynchronously, or <see langword="null"/> when nothing matches.</summary>
    /// <typeparam name="T">Entity type.</typeparam>
    /// <param name="source">Query to execute.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The first row, or <see langword="null"/>.</returns>
    public static Task<T?> FirstOrDefaultAsync<T>(
        this IQueryable<T> source,
        CancellationToken ct = default
    )
        where T : class
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

    /// <summary>Counts the rows asynchronously on the server.</summary>
    /// <typeparam name="T">Entity type.</typeparam>
    /// <param name="source">Query to execute.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns>The row count.</returns>
    public static Task<long> CountAsync<T>(
        this IQueryable<T> source,
        CancellationToken ct = default
    )
    {
        if (source.Provider is ILinKitAsyncQueryProvider provider)
        {
            return provider.ExecuteAsync<long>(source.Expression, ct);
        }

        return Task.FromResult((long)source.Count());
    }

    /// <summary>Checks asynchronously whether any row matches.</summary>
    /// <typeparam name="T">Entity type.</typeparam>
    /// <param name="source">Query to execute.</param>
    /// <param name="ct">Token used to cancel the command.</param>
    /// <returns><see langword="true"/> when at least one row matches.</returns>
    public static Task<bool> AnyAsync<T>(this IQueryable<T> source, CancellationToken ct = default)
    {
        if (source.Provider is ILinKitAsyncQueryProvider provider)
        {
            return provider.ExecuteAsync<bool>(source.Expression, ct);
        }

        return Task.FromResult(source.Any());
    }

    /// <summary>Returns one page of rows plus the total count.</summary>
    /// <typeparam name="T">Entity type.</typeparam>
    /// <param name="source">Query to execute.</param>
    /// <param name="pageNumber">One-based page number.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="ct">Token used to cancel the commands.</param>
    /// <returns>The page and the total row count.</returns>
    public static Task<PagedResult<T>> ToPagedListAsync<T>(
        this IQueryable<T> source,
        int pageNumber,
        int pageSize,
        CancellationToken ct = default
    )
        where T : class
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
        return Task.FromResult(
            new PagedResult<T>
            {
                Items = items,
                TotalCount = total,
                PageNumber = pageNumber,
                PageSize = pageSize,
            }
        );
    }
}
